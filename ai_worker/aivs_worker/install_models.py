"""Tải model ghim phiên bản vào <models-dir>: Whisper turbo (CTranslate2), denoiser ONNX, speaker encoder VieNeu, phát hiện chữ + inpaint (phụ đề cứng), tách giọng MDX-Net + Mel-Band RoFormer. Chạy: python -m aivs_worker.install_models --models-dir <dir>"""
import argparse
import hashlib
import json
import os
import shutil
import sys

TURBO_REPO = "mobiuslabsgmbh/faster-whisper-large-v3-turbo"
TURBO_REVISION = "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf"
TURBO_FILES = ("config.json", "model.bin", "tokenizer.json", "vocabulary.json", "preprocessor_config.json")
DENOISER_REPO = "pnnbao-ump/VieNeu-TTS-v3-Nano"
DENOISER_REVISION = "aba295eb96a6fa6003ebe417cc1f2802a7adc1dc"
DENOISER_SHA256 = "b7621953291cfe05e695a9c0ff4255aa2f93239fc17c26627e18b7b6b8f72f0b"
# Speaker encoder của VieNeu v3 Turbo: embedding 192 chiều cho clone giọng tiếng Việt.
SPEAKER_REPO = "pnnbao-ump/VieNeu-TTS-v3-Turbo"
SPEAKER_REVISION = "61b85e3d937fbbacb387714180e8182823512523"
SPEAKER_SHA256 = "a6ac6a63997761ae2997373e2ee1c47040854b4b759ea41ec48e4e42df0f4d73"
# Phát hiện chữ PP-OCRv4 det (PaddleOCR, Apache-2.0) bản ONNX của RapidOCR: tìm/xoá phụ đề cứng.
TEXTDET_REPO = "SWHL/RapidOCR"
TEXTDET_REVISION = "1cfba2e90fc938db55889873735088de210cc173"
TEXTDET_FILE = "PP-OCRv4/ch_PP-OCRv4_det_infer.onnx"
TEXTDET_SHA256 = "d2a7720d45a54257208b1e13e36a8479894cb74155a5efe29462512d42f49da9"
# MI-GAN (Picsart, MIT) pipeline ONNX: inpaint vùng phụ đề.
INPAINT_REPO = "andraniksargsyan/migan"
INPAINT_REVISION = "406830d0fa60666da0071c342ad2fbc8f30c5c64"
INPAINT_FILE = "migan_pipeline_v2.onnx"
INPAINT_SHA256 = "6f1f3530a1a2324b19752018ce756088b07973cda8d7d890034ace5c8a48c40b"
# UVR-MDX-NET-Voc_FT (UVR, ONNX): tách giọng gốc khi lồng tiếng. Bản sao trên HF trùng mã băm với bản phát hành TRvlvr/model_repo.
SEPARATION_REPO = "Blane187/all_public_uvr_models"
SEPARATION_REVISION = "fddec39677560e41e3194f24a9e4c4cd32ef0e83"
SEPARATION_FILE = "UVR-MDX-NET-Voc_FT.onnx"
SEPARATION_SHA256 = "534b2070fcc7df514b13ef660dc8cbb328679c2374d04354a5c42bb14ecce111"
# Mel-Band RoFormer vocals của Kim (MIT, PyTorch): tách giọng chất lượng cao, mặc định khi bật "Tách giọng gốc".
ROFORMER_REPO = "KimberleyJSN/melbandroformer"
ROFORMER_REVISION = "ac9b0614ab3cd7f77219e18ba494dfd93956c348"
ROFORMER_FILE = "MelBandRoformer.ckpt"
ROFORMER_SHA256 = "87201f4d31afb5bc79993230fc49446918425574db48c01c405e44f365c7559e"
# Qwen3 1.7B Q4_K_M (GGUF, Apache-2.0): LLM local cho Livestream bán hàng (S20), chạy bằng llama-server.
LIVELLM_REPO = "unsloth/Qwen3-1.7B-GGUF"
LIVELLM_REVISION = "d7f544eead698dbd1f15126ef60b45a1e1933222"
LIVELLM_FILE = "Qwen3-1.7B-Q4_K_M.gguf"
LIVELLM_SHA256 = "b139949c5bd74937ad8ed8c8cf3d9ffb1e99c866c823204dc42c0d91fa181897"

# Các GGUF audio.cpp dùng đúng tên/thư mục mà NativeTtsServerManager.ModelPath đọc.
VIENEU_REPO = "pnnbao-ump/VieNeu-TTS-v3-Turbo"
VIENEU_REVISION = "61b85e3d937fbbacb387714180e8182823512523"
VIENEU_SHA256 = "a60569e5d7dd6f24cbb7bb2e7c472bb988ad19563a4e78f0cc67cc53fd189bb4"
VIENEU_MANIFEST_SHA256 = "c11561e2b31636c1cd6d7244894342d37e03283171f9cfc4bd9fbb782debda7e"
AUDIOCPP_REPO = "audio-cpp/audio.cpp-gguf"
AUDIOCPP_REVISION = "874fcb41ececda20bcbc0782d6b4ea7f7d069ea8"
NATIVE_TTS_MODELS = {
    "omnivoice": ("OmniVoice", "OmniVoice-GGUF/omnivoice-q8_0.gguf", "2f4be637278043c6842de5b85d681532030e9eb6ffe0f8b0e320f68238e3da8b", "69492fe6b3f35ec13e49e5d2ed388bc9c24473d4"),
    "supertonic": ("Supertonic3", "Supertonic-3-GGUF/supertonic-3-q8_0.gguf", "9edd05cf92feab49c6b29721d14c86566459dca7f4a6eeb99306372cdce03697", AUDIOCPP_REVISION),
    "qwen3": ("Qwen3TTS", "Qwen3-TTS-12Hz-1.7B-CustomVoice-GGUF/qwen3-tts-12hz-1.7b-customvoice-q8_0.gguf", "c8ac3f94b6e73f137c0e7ecc9270bc8faec7a476ff799b87e48844d1d0987ac3", AUDIOCPP_REVISION),
    "cosyvoice3": ("CosyVoice3", "CosyVoice3-GGUF/cosyvoice3-q8_0.gguf", "d13bd731ead92d6d514d5749d9b59ea1b3ae735569fa46978250caaf5c983133", AUDIOCPP_REVISION),
    "irodori": ("Irodori", "Irodori-TTS-v4-Small-GGUF/irodori-tts-v4-small-q8_0.gguf", "9be716e8ab66c887417acf446d198e5dc53f1669bb90136a410706232f1a0d38", AUDIOCPP_REVISION),
    "chatterbox": ("Chatterbox", "Chatterbox-GGUF/chatterbox-q8_0.gguf", "45d71d8d248ba831014813bbbbba74c7d786dd9447c02090fe05c44344f69453", AUDIOCPP_REVISION),
    "breeze": ("Breeze", "Breeze-TTS-2-GGUF/breeze-tts-2-q8_0.gguf", "b17b89b1c21107c44e8d556ab0a856d7acd06bdd0c914e68e0e57f6e33f555a3", AUDIOCPP_REVISION),
}
HYMT2_REPO = "tencent/Hy-MT2-1.8B-GGUF"
HYMT2_REVISION = "a0c709d9fac510f2c807aa3af52872340dc37a4a"
HYMT2_FILE = "Hy-MT2-1.8B-Q4_K_M.gguf"
HYMT2_SHA256 = "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699"


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def install_turbo(models_dir):
    from huggingface_hub import hf_hub_download
    target = os.path.join(models_dir, "asr", "turbo")
    os.makedirs(target, exist_ok=True)
    for name in TURBO_FILES:
        print(f"Đang tải turbo/{name}", flush=True)
        cached = hf_hub_download(TURBO_REPO, name, revision=TURBO_REVISION)
        _place(cached, os.path.join(target, name))


def install_denoiser(models_dir):
    _install_onnx(os.path.join(models_dir, "denoise", "denoiser.onnx"), DENOISER_REPO, DENOISER_REVISION, DENOISER_SHA256)


def install_speaker(models_dir):
    _install_onnx(os.path.join(models_dir, "speaker", "speaker_encoder.onnx"), SPEAKER_REPO, SPEAKER_REVISION, SPEAKER_SHA256)


def install_textdet(models_dir):
    _install_onnx(os.path.join(models_dir, "vision", "text_det.onnx"), TEXTDET_REPO, TEXTDET_REVISION, TEXTDET_SHA256, TEXTDET_FILE)


def install_inpaint(models_dir):
    _install_onnx(os.path.join(models_dir, "vision", "migan.onnx"), INPAINT_REPO, INPAINT_REVISION, INPAINT_SHA256, INPAINT_FILE)


def install_separation(models_dir):
    _install_onnx(os.path.join(models_dir, "separation", SEPARATION_FILE), SEPARATION_REPO, SEPARATION_REVISION, SEPARATION_SHA256)


def install_roformer(models_dir):
    _install_onnx(os.path.join(models_dir, "separation", "mel_band_roformer_kim_vocals.ckpt"), ROFORMER_REPO, ROFORMER_REVISION,
                  ROFORMER_SHA256, ROFORMER_FILE)


def install_livellm(models_dir):
    target = os.path.join(models_dir, "llm", "Qwen3-1.7B", LIVELLM_FILE)
    _install_onnx(target, LIVELLM_REPO, LIVELLM_REVISION, LIVELLM_SHA256)


def install_vieneu(models_dir):
    root = os.path.join(models_dir, "native_tts", "VieNeu")
    _install_onnx(os.path.join(root, "vieneu-v3-turbo-q8_0.gguf"), VIENEU_REPO, VIENEU_REVISION,
                  VIENEU_SHA256, "gguf/vieneu-v3-turbo-q8_0.gguf")
    voices = os.path.join(root, "voices")
    manifest = os.path.join(voices, "manifest.json")
    _install_onnx(manifest, VIENEU_REPO, VIENEU_REVISION, VIENEU_MANIFEST_SHA256, "gguf/voices/manifest.json")
    with open(manifest, encoding="utf-8") as stream:
        voice_ids = [voice["id"] for voice in json.load(stream)["voices"]]
    for voice_id in voice_ids:
        if not voice_id or any(char not in "abcdefghijklmnopqrstuvwxyz0123456789_" for char in voice_id):
            raise RuntimeError(f"ID giọng VieNeu không hợp lệ: {voice_id}")
        for name in ("ref_codes.txt", "speaker.emb.txt"):
            relative = f"gguf/voices/{voice_id}/{name}"
            _install_pinned_file(os.path.join(voices, voice_id, name), VIENEU_REPO, VIENEU_REVISION, relative)


def install_native_tts(models_dir, family):
    folder, relative, digest, revision = NATIVE_TTS_MODELS[family]
    target = os.path.join(models_dir, "native_tts", folder, os.path.basename(relative))
    _install_onnx(target, AUDIOCPP_REPO, revision, digest, relative)


def install_hymt2(models_dir):
    _install_onnx(os.path.join(models_dir, "translate", "Hy-MT2", HYMT2_FILE), HYMT2_REPO,
                  HYMT2_REVISION, HYMT2_SHA256, HYMT2_FILE)


def _install_pinned_file(target, repo, revision, relative):
    # Các file voice nhỏ là Git blob tại commit cố định; hf_hub_download kiểm tra blob theo ETag.
    from huggingface_hub import hf_hub_download
    cached = hf_hub_download(repo, relative, revision=revision)
    digest = sha256(cached)
    if os.path.isfile(target) and sha256(target) == digest:
        return
    os.makedirs(os.path.dirname(target), exist_ok=True)
    print(f"Đang tải {relative}", flush=True)
    _place(cached, target)


def _install_onnx(target, repo, revision, digest, file=None):
    from huggingface_hub import hf_hub_download
    name = os.path.basename(target)
    if os.path.isfile(target) and sha256(target) == digest:
        return
    os.makedirs(os.path.dirname(target), exist_ok=True)
    print(f"Đang tải {name}", flush=True)
    cached = hf_hub_download(repo, file or name, revision=revision)
    if sha256(cached) != digest:
        raise RuntimeError(f"{name} sai mã băm SHA-256.")
    _place(cached, target)


def _place(source, target):
    # Ghi qua .partial để lần chạy sau không nhầm file dở là model đã cài.
    partial = target + ".partial"
    shutil.copyfile(source, partial)
    os.replace(partial, target)


def main(argv=None):
    parser = argparse.ArgumentParser(prog="aivs_worker.install_models")
    parser.add_argument("--models-dir", required=True)
    parser.add_argument("--models", nargs="+", choices=("turbo", "denoiser", "speaker", "textdet", "inpaint", "separation", "roformer", "livellm",
                                                      "vieneu", "omnivoice", "supertonic", "qwen3", "cosyvoice3", "irodori", "chatterbox", "breeze", "hymt2"),
                        default=["turbo", "denoiser", "speaker", "textdet", "inpaint", "separation", "roformer"])
    args = parser.parse_args(argv)
    models_dir = os.path.abspath(args.models_dir)
    if "turbo" in args.models:
        install_turbo(models_dir)
    if "denoiser" in args.models:
        install_denoiser(models_dir)
    if "speaker" in args.models:
        install_speaker(models_dir)
    if "textdet" in args.models:
        install_textdet(models_dir)
    if "inpaint" in args.models:
        install_inpaint(models_dir)
    if "separation" in args.models:
        install_separation(models_dir)
    if "roformer" in args.models:
        install_roformer(models_dir)
    if "livellm" in args.models:
        install_livellm(models_dir)
    if "vieneu" in args.models:
        install_vieneu(models_dir)
    for family in NATIVE_TTS_MODELS:
        if family in args.models:
            install_native_tts(models_dir, family)
    if "hymt2" in args.models:
        install_hymt2(models_dir)
    print("Xong.", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
