import os

import grpc

from ..gen import aivs_worker_pb2 as pb
from ..gen import aivs_worker_pb2_grpc as pb_grpc
from ._common import ensure_file, fail, read_mono

MODEL_FILES = ("model.bin", "config.json", "tokenizer.json")
# faster-whisper dùng mã Whisper, khác một số mã ISO/locale của app.
LANGUAGE_ALIASES = {"fil": "tl", "arb": "ar", "cmn": "zh", "nb": "no", "iw": "he"}


def normalize_language(language):
    code = (language or "").strip().lower().replace("_", "-").split("-")[0]
    if not code or code == "auto":
        return None
    return LANGUAGE_ALIASES.get(code, code)


def resolve_model_dir(model, models_dir):
    if not model or model == "turbo":
        return os.path.join(models_dir, "asr", "turbo")
    return model


def model_installed(path):
    return all(os.path.isfile(os.path.join(path, name)) for name in MODEL_FILES)


def _default_loader(path, device, compute_type):
    from faster_whisper import WhisperModel
    import os
    try:
        threads = max(1, min(64, int(os.environ.get("AIVS_CPU_THREADS", "4"))))
    except ValueError:
        threads = 4
    return WhisperModel(path, device=device, compute_type=compute_type, cpu_threads=threads, num_workers=1)


class AsrService(pb_grpc.AsrServicer):
    def __init__(self, state, loader=_default_loader):
        self._state = state
        self._loader = loader

    def Transcribe(self, request, context):
        ensure_file(context, request.audio_path, "file audio")
        models_dir = request.models_dir or self._state.models_dir
        model_dir = resolve_model_dir(request.model, models_dir)
        if not model_installed(model_dir):
            fail(context, grpc.StatusCode.FAILED_PRECONDITION,
                 f"Chưa cài model Whisper tại {model_dir}. Cài model ở Cài đặt > AI worker.")
        device = request.device or "cpu"
        compute_type = request.compute_type or "int8"
        if request.task not in ("", "transcribe", "translate"):
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, f"Tác vụ không hợp lệ: {request.task}")

        # Đọc sẵn mảng 16 kHz thay vì đưa đường dẫn: bỏ phụ thuộc giải mã của PyAV (bản mới lệch API với faster-whisper).
        audio = read_mono(request.audio_path, 16000, context)
        cache = self._state.cache
        try:
            model = cache.get("asr", (model_dir, device, compute_type),
                              lambda: self._loader(model_dir, device, compute_type))
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Không nạp được model Whisper: {error}")

        with cache.inference_lock:
            yield from self._run(model, audio, request, context)

    def _run(self, model, audio, request, context):
        yield pb.TranscribeEvent(progress=pb.Progress(fraction=-1, message="Đang khởi chạy nhận diện..."))
        options = dict(
            language=normalize_language(request.language),
            task=request.task or "transcribe",
            vad_filter=request.vad_filter,
            beam_size=request.beam_size or 5,
            word_timestamps=request.word_timestamps,
        )
        if request.vad_filter:
            options["vad_parameters"] = dict(min_silence_duration_ms=400)
        if request.initial_prompt:
            options["initial_prompt"] = request.initial_prompt
        try:
            segments, info = model.transcribe(audio, **options)
            count = 0
            for item in segments:
                if not context.is_active():
                    return
                text = item.text.strip()
                if not text:
                    continue
                count += 1
                words = [pb.Word(start=w.start, end=w.end, text=w.word, probability=w.probability)
                         for w in (item.words or [])]
                yield pb.TranscribeEvent(segment=pb.Segment(
                    index=count, start=round(item.start, 3), end=round(item.end, 3), text=text,
                    avg_logprob=item.avg_logprob, no_speech_prob=item.no_speech_prob, words=words))
                if info.duration > 0:
                    yield pb.TranscribeEvent(progress=pb.Progress(
                        fraction=min(1.0, item.end / info.duration), message=f"{count} câu"))
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Nhận diện giọng nói lỗi: {error}")
        yield pb.TranscribeEvent(completed=pb.TranscribeCompleted(
            language=info.language, language_probability=info.language_probability,
            duration=info.duration, segment_count=count))
