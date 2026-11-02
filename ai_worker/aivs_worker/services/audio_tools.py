import os
import time

import grpc
import numpy as np

from ..gen import aivs_worker_pb2 as pb
from ..gen import aivs_worker_pb2_grpc as pb_grpc
from ._common import ensure_file, fail, read_mono

VAD_RATE = 16000
DENOISE_RATE = 44100
# Cùng cấu hình STFT với resemble-enhance Denoiser (hop 420, n_fft = hop * 4).
N_FFT = 1680
HOP = 420
_WIN = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(N_FFT) / N_FFT)   # Hann periodic như torch
# Lõi ONNX chỉ xử lý ổn định một đoạn <= ~30 s.
CHUNK_SECONDS = 30


def _stft(x):
    pad = N_FFT // 2
    xp = np.pad(x, (pad, pad), mode="reflect")
    n = 1 + (len(xp) - N_FFT) // HOP
    idx = np.arange(N_FFT)[None, :] + HOP * np.arange(n)[:, None]
    spec = np.fft.rfft(xp[idx] * _WIN[None, :], n=N_FFT, axis=1).T
    return spec[:, :-1]                                   # nguồn bỏ khung cuối


def _istft(spec, out_len):
    spec = np.concatenate([spec, spec[:, -1:]], axis=1)
    frames = np.fft.irfft(spec, n=N_FFT, axis=0) * _WIN[:, None]
    count = spec.shape[1]
    length = N_FFT + HOP * (count - 1)
    out = np.zeros(length)
    wsum = np.zeros(length)
    w2 = _WIN ** 2
    for i in range(count):
        start = i * HOP
        out[start:start + N_FFT] += frames[:, i]
        wsum[start:start + N_FFT] += w2
    out = out / np.maximum(wsum, 1e-11)
    out = out[N_FFT // 2:length - N_FFT // 2]
    if len(out) < out_len:
        out = np.pad(out, (0, out_len - len(out)))
    return out[:out_len]


class OnnxDenoiser:
    """Khử nhiễu resemble-enhance bằng ONNX + numpy (không torch); STFT/iSTFT nằm ngoài đồ thị."""

    def __init__(self, session):
        self._session = session

    @classmethod
    def from_file(cls, path):
        import onnxruntime as ort
        options = ort.SessionOptions()
        options.intra_op_num_threads = 2
        options.inter_op_num_threads = 1
        # ORT mặc định busy-wait trên mọi lõi; tắt để không ăn CPU khi rảnh.
        options.add_session_config_entry("session.intra_op.allow_spinning", "0")
        options.add_session_config_entry("session.inter_op.allow_spinning", "0")
        return cls(ort.InferenceSession(path, options, providers=["CPUExecutionProvider"]))

    def denoise_chunk(self, wav):
        length = len(wav)
        peak = max(float(np.abs(wav).max()), 1e-7)
        x = np.pad(wav / peak, (0, 441))                  # npad=441 như resemble inference_chunk
        x = x / (np.abs(x).max() + 1e-7)
        spec = _stft(x)
        phase = np.angle(spec)
        outputs = self._session.run(None, {
            "mag": np.abs(spec)[None].astype(np.float32),
            "cos": np.cos(phase)[None].astype(np.float32),
            "sin": np.sin(phase)[None].astype(np.float32)})
        mag, cos, sin = (o[0] for o in outputs[:3])
        out = _istft(mag * (cos + 1j * sin), len(x))
        return (out[:length] * peak).astype(np.float32)


SPEAKER_RATE = 16000
# SDK VieNeu (prepare_reference) chỉ lấy 8 s đầu của mẫu.
SPEAKER_SECONDS = 8


class OnnxSpeakerEncoder:
    """Embedding người nói 192 chiều của VieNeu v3 Turbo: fbank Kaldi 80 mel (16 kHz, trừ trung bình) → speaker_encoder.onnx.
    Cùng tiền xử lý với vieneu/_v3_turbo_engine/speaker của SDK (kaldi-native-fbank + soxr, không torch)."""

    def __init__(self, session):
        self._session = session

    @classmethod
    def from_file(cls, path):
        import onnxruntime as ort
        options = ort.SessionOptions()
        options.intra_op_num_threads = 2
        options.inter_op_num_threads = 1
        options.add_session_config_entry("session.intra_op.allow_spinning", "0")
        options.add_session_config_entry("session.inter_op.allow_spinning", "0")
        return cls(ort.InferenceSession(path, options, providers=["CPUExecutionProvider"]))

    def embed(self, wav, rate):
        import kaldi_native_fbank as knf
        import soxr
        wav = wav[:SPEAKER_SECONDS * rate]
        if rate != SPEAKER_RATE:
            wav = soxr.resample(wav, rate, SPEAKER_RATE)
        options = knf.FbankOptions()
        frame = options.frame_opts
        frame.samp_freq = SPEAKER_RATE
        frame.frame_shift_ms = 10.0
        frame.frame_length_ms = 25.0
        frame.dither = 0.0
        frame.snip_edges = True
        frame.window_type = "povey"
        frame.remove_dc_offset = True
        frame.preemph_coeff = 0.97
        options.mel_opts.num_bins = 80
        fbank = knf.OnlineFbank(options)
        fbank.accept_waveform(SPEAKER_RATE, np.asarray(wav, dtype=np.float32))
        fbank.input_finished()
        if fbank.num_frames_ready == 0:
            raise ValueError("Audio quá ngắn.")
        feats = np.stack([fbank.get_frame(i) for i in range(fbank.num_frames_ready)]).astype(np.float32)
        feats -= feats.mean(axis=0, keepdims=True)
        inputs = self._session.get_inputs()[0].name
        return self._session.run(None, {inputs: feats[None]})[0][0].astype(np.float32)


SEPARATE_RATE = 44100
MDX_HOP = 1024
# Tham số STFT theo model_data.json của UVR: n_fft, hệ số bù, model ra lời (True) hay ra nền (False).
MDX_MODELS = {
    "Kim_Vocal_2": (7680, 1.009, True),
    "UVR-MDX-NET-Voc_FT": (7680, 1.021, True),
    "UVR-MDX-NET-Inst_HQ_3": (6144, 1.022, False),
    "UVR-MDX-NET-Inst_HQ_4": (5120, 1.019, False),
    "UVR-MDX-NET-Inst_HQ_5": (5120, 1.01, False),
}


class ChunkedSeparator:
    """Tách theo đoạn cố định (chunk mẫu, chồng lấn overlap, đệm trim mẫu im lặng ở đầu) nên RAM không tăng theo độ dài video.
    Lớp con cung cấp chunk, overlap, trim và background(mix (2, chunk)) → nền không lời cùng kích thước."""
    chunk = 0
    overlap = 0.25
    trim = 0

    def separate(self, read, total, write, lock):
        """Generator, trả tỉ lệ đã xong sau mỗi đoạn. read(start, count) → (count, 2); write((n, 2)).
        Ghép chồng lấn: phần trước đoạn kế tiếp đã đủ trọng số nên ghi ngay. Khoá suy luận theo từng đoạn để ASR/khử nhiễu chen được."""
        chunk, trim = self.chunk, self.trim
        step = int(chunk * (1 - self.overlap))
        win = np.hanning(chunk)
        acc = np.zeros((2, chunk))
        wsum = np.zeros(chunk)
        pos = 0
        while pos < trim + total:
            start = pos - trim
            seg = np.zeros((2, chunk))
            a, b = max(0, start), min(total, start + chunk)
            if b > a:
                seg[:, a - start:b - start] = read(a, b - a).T
            with lock:
                acc += self.background(seg) * win
            wsum += win
            o0, o1 = max(0, start), min(total, start + step)
            if o1 > o0:
                done = acc[:, :step] / np.maximum(wsum[:step], 1e-8)
                write(np.clip(done[:, o0 - start:o1 - start].T, -1.0, 1.0))
            acc = np.roll(acc, -step, axis=1)
            acc[:, -step:] = 0
            wsum = np.roll(wsum, -step)
            wsum[-step:] = 0
            pos += step
            yield min(1.0, pos / (trim + total))


class OnnxVocalSeparator(ChunkedSeparator):
    """Tách giọng MDX-Net (UVR) bằng ONNX + numpy, STFT/iSTFT ngoài đồ thị như audio-separator.
    Chồng lấn 25 % giữa các đoạn ~5,9 s (mặc định UVR)."""

    def __init__(self, session, n_fft, compensate, vocals_primary):
        self._session = session
        self._input = session.get_inputs()[0].name
        _, _, self.dim_f, self.dim_t = session.get_inputs()[0].shape
        self.n_fft, self.compensate, self.vocals_primary = n_fft, compensate, vocals_primary
        self.chunk = MDX_HOP * (self.dim_t - 1)
        self.trim = n_fft // 2
        self._win = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(n_fft) / n_fft)   # Hann periodic như torch

    @classmethod
    def from_file(cls, path, threads=0):
        import onnxruntime as ort
        name = os.path.splitext(os.path.basename(path))[0]
        if name not in MDX_MODELS:
            raise ValueError(f"Chưa có tham số cho model {name}.")
        options = ort.SessionOptions()
        options.intra_op_num_threads = threads or max(1, (os.cpu_count() or 2) // 2)
        options.inter_op_num_threads = 1
        # Tắt arena + memory pattern: đỉnh RAM ~2,5 GB → ~1,3 GB, tốc độ như cũ (đo trên i5-11400).
        options.enable_cpu_mem_arena = False
        options.enable_mem_pattern = False
        options.add_session_config_entry("session.intra_op.allow_spinning", "0")
        options.add_session_config_entry("session.inter_op.allow_spinning", "0")
        return cls(ort.InferenceSession(path, options, providers=["CPUExecutionProvider"]), *MDX_MODELS[name])

    def _stft(self, x):
        pad = self.n_fft // 2
        xp = np.pad(x, ((0, 0), (pad, pad)), mode="reflect")
        idx = np.arange(self.n_fft)[None, :] + MDX_HOP * np.arange(self.dim_t)[:, None]
        spec = np.fft.rfft(xp[:, idx] * self._win, axis=-1)[..., :self.dim_f].transpose(0, 2, 1)
        out = np.stack([spec.real, spec.imag], axis=1).reshape(1, 4, self.dim_f, self.dim_t)
        out[:, :, :3, :] = 0                              # UVR bỏ 3 bin thấp nhất
        return out.astype(np.float32)

    def _istft(self, s):
        s = s.reshape(2, 2, self.dim_f, self.dim_t)
        spec = np.zeros((2, self.n_fft // 2 + 1, self.dim_t), dtype=np.complex128)
        spec[:, :self.dim_f] = s[:, 0] + 1j * s[:, 1]
        frames = np.fft.irfft(spec.transpose(0, 2, 1), n=self.n_fft, axis=-1) * self._win
        length = self.n_fft + MDX_HOP * (self.dim_t - 1)
        out = np.zeros((2, length))
        wsum = np.zeros(length)
        w2 = self._win ** 2
        for t in range(self.dim_t):
            start = t * MDX_HOP
            out[:, start:start + self.n_fft] += frames[:, t]
            wsum[start:start + self.n_fft] += w2
        out /= np.maximum(wsum, 1e-11)
        return out[:, self.n_fft // 2:self.n_fft // 2 + self.chunk]

    def background(self, mix):
        """mix (2, chunk) → nền không lời cùng kích thước."""
        out = self._istft(self._session.run(None, {self._input: self._stft(mix)})[0])
        return mix - self.compensate * out if self.vocals_primary else out


class RoformerSeparator(ChunkedSeparator):
    """Mel-Band RoFormer (Kim, vocals) bằng PyTorch CPU: đoạn 8 s, chồng lấn 50 % như cấu hình gốc (num_overlap 2)."""
    overlap = 0.5

    def __init__(self, model, chunk):
        self._model = model
        self.chunk = chunk

    @classmethod
    def from_file(cls, path, threads=0):
        try:
            import torch
        except ImportError as error:
            raise ModuleNotFoundError("Thiếu PyTorch trong AI worker (S16 → cài lại worker).") from error
        from . import roformer
        torch.set_num_threads(threads or max(1, (os.cpu_count() or 2) // 2))
        return cls(roformer.load_kim_vocals(path), roformer.KIM_VOCALS["chunk"])

    def background(self, mix):
        import torch
        with torch.inference_mode():
            vocals = self._model(torch.from_numpy(mix.astype(np.float32))[None])[0].numpy()
        return mix - vocals


def separator_from_file(path, threads=0):
    """.ckpt → RoFormer (PyTorch), còn lại → MDX-Net ONNX."""
    if path.lower().endswith(".ckpt"):
        return RoformerSeparator.from_file(path, threads)
    return OnnxVocalSeparator.from_file(path, threads)


def _speech_timestamps(audio, options):
    from faster_whisper.vad import VadOptions, get_speech_timestamps
    return get_speech_timestamps(audio, VadOptions(**options), sampling_rate=VAD_RATE)


class AudioToolsService(pb_grpc.AudioToolsServicer):
    def __init__(self, state, denoiser_factory=OnnxDenoiser.from_file, vad=_speech_timestamps,
                 speaker_factory=OnnxSpeakerEncoder.from_file, separator_factory=separator_from_file):
        self._state = state
        self._denoiser_factory = denoiser_factory
        self._speaker_factory = speaker_factory
        self._separator_factory = separator_factory
        self._vad = vad

    def DetectSpeech(self, request, context):
        ensure_file(context, request.audio_path, "file audio")
        audio = read_mono(request.audio_path, VAD_RATE, context)
        options = dict(threshold=request.threshold or 0.5,
                       min_speech_duration_ms=request.min_speech_ms or 250,
                       min_silence_duration_ms=request.min_silence_ms or 400,
                       speech_pad_ms=request.speech_pad_ms or 30)
        try:
            with self._state.cache.inference_lock:
                regions = self._vad(audio, options)
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Phát hiện lời nói lỗi: {error}")
        return pb.DetectSpeechResponse(
            regions=[pb.TimeRange(start=round(r["start"] / VAD_RATE, 3), end=round(r["end"] / VAD_RATE, 3))
                     for r in regions],
            duration=len(audio) / VAD_RATE)

    def Denoise(self, request, context):
        import soundfile as sf
        ensure_file(context, request.input_path, "file audio")
        ensure_file(context, request.model_path, "model denoiser")
        if not request.output_path or not os.path.isabs(request.output_path):
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, "output_path phải là đường dẫn tuyệt đối.")
        audio = read_mono(request.input_path, DENOISE_RATE, context)
        strength = min(1.0, max(0.0, request.strength or 1.0))
        try:
            denoiser = self._state.cache.get("denoise", request.model_path,
                                             lambda: self._denoiser_factory(request.model_path))
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Không nạp được model denoiser: {error}")

        step = CHUNK_SECONDS * DENOISE_RATE
        pieces = []
        yield pb.TaskEvent(progress=pb.Progress(fraction=0, message="Đang khử nhiễu..."))
        try:
            with self._state.cache.inference_lock:
                for start in range(0, len(audio), step):
                    if not context.is_active():
                        return
                    pieces.append(denoiser.denoise_chunk(audio[start:start + step]))
                    yield pb.TaskEvent(progress=pb.Progress(
                        fraction=min(1.0, (start + step) / len(audio)), message="Đang khử nhiễu..."))
            cleaned = np.concatenate(pieces)
            if strength < 1.0:
                cleaned = strength * cleaned + (1.0 - strength) * audio
            if not np.isfinite(cleaned).all():
                fail(context, grpc.StatusCode.INTERNAL, "Audio sau khử nhiễu không hợp lệ.")
            partial = request.output_path + ".partial"
            sf.write(partial, cleaned, DENOISE_RATE, subtype="PCM_16", format="WAV")
            os.replace(partial, request.output_path)
        except grpc.RpcError:
            raise
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Khử nhiễu lỗi: {error}")
        yield pb.TaskEvent(completed=pb.TaskCompleted(
            output_path=request.output_path, duration=len(cleaned) / DENOISE_RATE))

    def SpeakerEmbedding(self, request, context):
        import soundfile as sf
        ensure_file(context, request.input_path, "file audio")
        ensure_file(context, request.model_path, "model speaker encoder")
        try:
            audio, rate = sf.read(request.input_path, dtype="float32")
        except Exception as error:
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, f"Không đọc được WAV: {error}")
        if audio.ndim != 1:
            audio = audio.mean(axis=1)
        if not len(audio) or not np.isfinite(audio).all():
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, "Audio rỗng hoặc không hợp lệ.")
        try:
            encoder = self._state.cache.get("speaker", request.model_path,
                                            lambda: self._speaker_factory(request.model_path))
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Không nạp được model speaker encoder: {error}")
        try:
            with self._state.cache.inference_lock:
                embedding = encoder.embed(audio, rate)
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Tính embedding giọng lỗi: {error}")
        if not np.isfinite(embedding).all():
            fail(context, grpc.StatusCode.INTERNAL, "Embedding giọng không hợp lệ.")
        return pb.SpeakerEmbeddingResponse(embedding=embedding.tolist())

    def SeparateVocals(self, request, context):
        import soundfile as sf
        ensure_file(context, request.input_path, "file audio")
        ensure_file(context, request.model_path, "model tách giọng")
        if not request.output_path or not os.path.isabs(request.output_path):
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, "output_path phải là đường dẫn tuyệt đối.")
        try:
            source = sf.SoundFile(request.input_path)
        except Exception as error:
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, f"Không đọc được WAV: {error}")
        with source:
            if source.samplerate != SEPARATE_RATE or source.channels not in (1, 2) or not source.frames:
                fail(context, grpc.StatusCode.INVALID_ARGUMENT,
                     f"Cần WAV {SEPARATE_RATE} Hz mono/stereo, file là {source.samplerate} Hz, {source.channels} kênh.")
            try:
                separator = self._state.cache.get("separation", request.model_path,
                                                  lambda: self._separator_factory(request.model_path, request.threads))
            except ModuleNotFoundError as error:
                fail(context, grpc.StatusCode.FAILED_PRECONDITION, str(error))
            except Exception as error:
                fail(context, grpc.StatusCode.INTERNAL, f"Không nạp được model tách giọng: {error}")

            def read(start, count):
                source.seek(start)
                data = source.read(count, dtype="float64", always_2d=True)
                return np.repeat(data, 2, axis=1) if data.shape[1] == 1 else data

            partial = request.output_path + ".partial"
            yield pb.TaskEvent(progress=pb.Progress(fraction=0, message="Đang tách giọng..."))
            try:
                with sf.SoundFile(partial, "w", SEPARATE_RATE, 2, "PCM_16", format="WAV") as sink:
                    def write(block):
                        if not np.isfinite(block).all():
                            raise ValueError("Audio sau tách giọng không hợp lệ.")
                        sink.write(block)

                    for fraction in separator.separate(read, source.frames, write, self._state.cache.inference_lock):
                        if not context.is_active():
                            break
                        # Khoá suy luận nhả giữa các đoạn: đánh dấu còn bận để watcher rảnh không tắt worker giữa chừng.
                        self._state.last_activity = time.monotonic()
                        yield pb.TaskEvent(progress=pb.Progress(fraction=fraction, message="Đang tách giọng..."))
                if not context.is_active():
                    os.remove(partial)
                    return
                os.replace(partial, request.output_path)
            except grpc.RpcError:
                raise
            except Exception as error:
                if os.path.exists(partial):
                    os.remove(partial)
                fail(context, grpc.StatusCode.INTERNAL, f"Tách giọng lỗi: {error}")
            duration = source.frames / SEPARATE_RATE
        yield pb.TaskEvent(completed=pb.TaskCompleted(output_path=request.output_path, duration=duration))
