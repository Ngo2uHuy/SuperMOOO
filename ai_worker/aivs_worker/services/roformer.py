"""Mel-Band RoFormer (Kim, vocals) chạy PyTorch CPU, không einops/librosa/rotary_embedding_torch.
Port tối thiểu từ lucidrains/BS-RoFormer (bản ZFTurbo, Kim dùng để train): tên module/tham số giữ nguyên để nạp thẳng checkpoint."""
import numpy as np
import torch
import torch.nn.functional as F
from torch import nn

# Cấu hình config_vocals_mel_band_roformer.yaml (KimberleyJensen/Mel-Band-Roformer-Vocal-Model).
KIM_VOCALS = dict(dim=384, depth=6, num_bands=60, dim_head=64, heads=8, sample_rate=44100,
                  n_fft=2048, hop=441, chunk=352800, mask_estimator_depth=2)


class RMSNorm(nn.Module):
    def __init__(self, dim):
        super().__init__()
        self.scale = dim ** 0.5
        self.gamma = nn.Parameter(torch.ones(dim))

    def forward(self, x):
        return F.normalize(x, dim=-1) * self.scale * self.gamma


class RotaryEmbedding(nn.Module):
    """Như rotary_embedding_torch: tần số lặp xen kẽ (f0, f0, f1, f1, ...) và xoay từng cặp kề nhau."""

    def __init__(self, dim, theta=10000):
        super().__init__()
        self.freqs = nn.Parameter(1.0 / theta ** (torch.arange(0, dim, 2)[:dim // 2].float() / dim), requires_grad=False)

    def rotate(self, t):
        angles = torch.arange(t.shape[-2], dtype=self.freqs.dtype)[:, None] * self.freqs
        angles = angles.repeat_interleave(2, dim=-1)
        x1, x2 = t.unflatten(-1, (-1, 2)).unbind(-1)
        return t * angles.cos() + torch.stack((-x2, x1), dim=-1).flatten(-2) * angles.sin()


class FeedForward(nn.Module):
    def __init__(self, dim, mult=4):
        super().__init__()
        inner = int(dim * mult)
        self.net = nn.Sequential(RMSNorm(dim), nn.Linear(dim, inner), nn.GELU(), nn.Dropout(0.0),
                                 nn.Linear(inner, dim), nn.Dropout(0.0))

    def forward(self, x):
        return self.net(x)


class Attention(nn.Module):
    def __init__(self, dim, heads, dim_head, rotary_embed):
        super().__init__()
        self.heads = heads
        inner = heads * dim_head
        self.rotary_embed = rotary_embed
        self.norm = RMSNorm(dim)
        self.to_qkv = nn.Linear(dim, inner * 3, bias=False)
        self.to_gates = nn.Linear(dim, heads)
        self.to_out = nn.Sequential(nn.Linear(inner, dim, bias=False), nn.Dropout(0.0))

    def forward(self, x):
        x = self.norm(x)
        b, n, _ = x.shape
        q, k, v = self.to_qkv(x).view(b, n, 3, self.heads, -1).permute(2, 0, 3, 1, 4)
        q, k = self.rotary_embed.rotate(q), self.rotary_embed.rotate(k)
        out = F.scaled_dot_product_attention(q, k, v)
        out = out * self.to_gates(x).transpose(1, 2).unsqueeze(-1).sigmoid()
        return self.to_out(out.transpose(1, 2).reshape(b, n, -1))


class Transformer(nn.Module):
    def __init__(self, dim, depth, heads, dim_head, rotary_embed):
        super().__init__()
        self.layers = nn.ModuleList([nn.ModuleList([Attention(dim, heads, dim_head, rotary_embed), FeedForward(dim)])
                                     for _ in range(depth)])
        self.norm = RMSNorm(dim)

    def forward(self, x):
        for attn, ff in self.layers:
            x = attn(x) + x
            x = ff(x) + x
        return self.norm(x)


class BandSplit(nn.Module):
    def __init__(self, dim, dim_inputs):
        super().__init__()
        self.dim_inputs = dim_inputs
        self.to_features = nn.ModuleList([nn.Sequential(RMSNorm(d), nn.Linear(d, dim)) for d in dim_inputs])

    def forward(self, x):
        return torch.stack([f(part) for part, f in zip(x.split(self.dim_inputs, dim=-1), self.to_features)], dim=-2)


def _mlp(dim_in, dim_out, dim_hidden, depth):
    # Bản lucidrains Kim dùng để train: depth lớp ẩn (bản mới hơn là depth - 1), checkpoint có 3 Linear.
    dims = (dim_in, *((dim_hidden,) * depth), dim_out)
    layers = []
    for i, (a, b) in enumerate(zip(dims[:-1], dims[1:])):
        layers.append(nn.Linear(a, b))
        if i < len(dims) - 2:
            layers.append(nn.Tanh())
    return nn.Sequential(*layers)


class MaskEstimator(nn.Module):
    def __init__(self, dim, dim_inputs, depth, expansion=4):
        super().__init__()
        self.to_freqs = nn.ModuleList([nn.Sequential(_mlp(dim, d * 2, dim * expansion, depth), nn.GLU(dim=-1))
                                       for d in dim_inputs])

    def forward(self, x):
        return torch.cat([mlp(band) for band, mlp in zip(x.unbind(dim=-2), self.to_freqs)], dim=-1)


def _mel_filter_mask(sample_rate, n_fft, n_mels):
    """Vùng khác 0 của librosa.filters.mel (Slaney, norm slaney, float32) → dải tần mỗi băng."""
    def hz_to_mel(f):
        f = np.asanyarray(f, dtype=np.float64)
        mel = f / (200.0 / 3)
        log = f >= 1000.0
        return np.where(log, 15.0 + np.log(np.maximum(f, 1e-10) / 1000.0) / (np.log(6.4) / 27.0), mel)

    def mel_to_hz(m):
        m = np.asanyarray(m, dtype=np.float64)
        return np.where(m >= 15.0, 1000.0 * np.exp((np.log(6.4) / 27.0) * (m - 15.0)), m * (200.0 / 3))

    fftfreqs = np.fft.rfftfreq(n=n_fft, d=1.0 / sample_rate)
    mel_f = mel_to_hz(np.linspace(hz_to_mel(0.0), hz_to_mel(sample_rate / 2.0), n_mels + 2))
    fdiff = np.diff(mel_f)
    ramps = np.subtract.outer(mel_f, fftfreqs)
    weights = np.zeros((n_mels, len(fftfreqs)), dtype=np.float32)
    for i in range(n_mels):
        weights[i] = np.maximum(0, np.minimum(-ramps[i] / fdiff[i], ramps[i + 2] / fdiff[i + 1]))
    weights *= (2.0 / (mel_f[2:n_mels + 2] - mel_f[:n_mels]))[:, None]
    weights[0, 0] = 1.0           # như bản gốc: ép băng đầu/cuối phủ bin 0 và bin cuối
    weights[-1, -1] = 1.0
    return torch.from_numpy(weights > 0)


class MelBandRoformer(nn.Module):
    """Stereo, 1 stem. forward(mix (b, 2, t)) → stem (b, 2, t)."""

    def __init__(self, dim, depth, num_bands, dim_head, heads, sample_rate, n_fft, hop, mask_estimator_depth, **_):
        super().__init__()
        self.n_fft, self.hop = n_fft, hop
        transformer = dict(dim=dim, depth=1, heads=heads, dim_head=dim_head)
        time_rotary, freq_rotary = RotaryEmbedding(dim_head), RotaryEmbedding(dim_head)
        self.layers = nn.ModuleList([nn.ModuleList([Transformer(rotary_embed=time_rotary, **transformer),
                                                    Transformer(rotary_embed=freq_rotary, **transformer)])
                                     for _ in range(depth)])
        freqs_per_band = _mel_filter_mask(sample_rate, n_fft, num_bands)
        freqs = freqs_per_band.shape[1]
        freq_indices = torch.arange(freqs).expand(num_bands, freqs)[freqs_per_band]
        freq_indices = (freq_indices[:, None] * 2 + torch.arange(2)).reshape(-1)     # xen kẽ 2 kênh stereo
        self.register_buffer("freq_indices", freq_indices, persistent=False)
        self.register_buffer("num_bands_per_freq", freqs_per_band.sum(0), persistent=False)
        dim_inputs = tuple(2 * 2 * int(f) for f in freqs_per_band.sum(1).tolist())
        self.band_split = BandSplit(dim, dim_inputs)
        self.mask_estimators = nn.ModuleList([MaskEstimator(dim, dim_inputs, mask_estimator_depth)])
        self.register_buffer("window", torch.hann_window(n_fft), persistent=False)

    def forward(self, mix):
        b, channels, length = mix.shape
        spec = torch.stft(mix.reshape(b * channels, length), self.n_fft, self.hop, window=self.window, return_complex=True)
        spec = torch.view_as_real(spec)                                   # (b*s, f, t, 2)
        spec = spec.view(b, channels, *spec.shape[1:]).permute(0, 2, 1, 3, 4).reshape(b, -1, spec.shape[2], 2)  # (b, f*s, t, 2)
        x = spec[:, self.freq_indices]                                    # (b, F', t, 2)
        x = x.permute(0, 2, 1, 3).reshape(b, x.shape[2], -1)              # (b, t, F'*2)
        x = self.band_split(x)                                            # (b, t, bands, d)
        _, t, bands, d = x.shape
        for time_t, freq_t in self.layers:
            x = time_t(x.transpose(1, 2).reshape(b * bands, t, d)).view(b, bands, t, d).transpose(1, 2)
            x = freq_t(x.reshape(b * t, bands, d)).view(b, t, bands, d)
        mask = self.mask_estimators[0](x)                                 # (b, t, F'*2)
        mask = torch.view_as_complex(mask.view(b, t, -1, 2).transpose(1, 2).contiguous())   # (b, F', t)
        spec = torch.view_as_complex(spec.contiguous())                   # (b, f*s, t)
        # Bin thuộc nhiều băng: lấy trung bình mask của các băng chồng nhau.
        summed = torch.zeros_like(spec).index_add_(1, self.freq_indices, mask)
        denom = self.num_bands_per_freq.repeat_interleave(channels).clamp(min=1e-8)[:, None]
        spec = spec * (summed / denom)
        spec = spec.view(b, -1, channels, t).transpose(1, 2).reshape(b * channels, -1, t)
        out = torch.istft(spec, self.n_fft, self.hop, window=self.window, length=length)
        return out.view(b, channels, length)


def load_kim_vocals(path):
    model = MelBandRoformer(**KIM_VOCALS)
    # mmap + assign: dùng thẳng tensor trong file thay vì chép thêm một bản 0,9 GB lúc nạp.
    state = torch.load(path, map_location="cpu", weights_only=True, mmap=True)
    model.load_state_dict(state.get("state_dict", state), assign=True)
    return model.eval()
