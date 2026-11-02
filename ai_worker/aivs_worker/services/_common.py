import grpc
import numpy as np

CANCELLED_MESSAGE = "Đã huỷ."


def fail(context, code, message):
    context.abort(code, message)


def ensure_file(context, path, label):
    import os
    if not path or not os.path.isabs(path):
        fail(context, grpc.StatusCode.INVALID_ARGUMENT, f"{label} phải là đường dẫn tuyệt đối.")
    if not os.path.isfile(path):
        fail(context, grpc.StatusCode.NOT_FOUND, f"Không tìm thấy {label}: {path}")


def read_mono(path, expected_rate, context):
    import soundfile as sf
    try:
        audio, rate = sf.read(path, dtype="float32")
    except Exception as error:
        fail(context, grpc.StatusCode.INVALID_ARGUMENT, f"Không đọc được WAV: {error}")
    if audio.ndim != 1:
        audio = audio.mean(axis=1)
    if rate != expected_rate:
        fail(context, grpc.StatusCode.INVALID_ARGUMENT,
             f"Cần WAV {expected_rate} Hz, file là {rate} Hz. Chuẩn bị bằng ffmpeg trước.")
    if not len(audio) or not np.isfinite(audio).all():
        fail(context, grpc.StatusCode.INVALID_ARGUMENT, "Audio rỗng hoặc không hợp lệ.")
    return audio
