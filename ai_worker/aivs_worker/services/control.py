import importlib.util
import os
import platform

from .. import __version__
from ..gen import aivs_worker_pb2 as pb
from ..gen import aivs_worker_pb2_grpc as pb_grpc
from . import asr, voice_conversion


def _module(*packages):
    missing = [p for p in packages if importlib.util.find_spec(p) is None]
    if missing:
        return pb.ModuleStatus(available=False, reason=f"Thiếu gói Python: {', '.join(missing)}.")
    return pb.ModuleStatus(available=True)


class ControlService(pb_grpc.WorkerControlServicer):
    def __init__(self, state):
        self._state = state

    def GetCapabilities(self, request, context):
        models_dir = self._state.models_dir
        cache = self._state.cache
        whisper_dir = os.path.join(models_dir, "asr", "turbo")
        denoiser_path = os.path.join(models_dir, "denoise", "denoiser.onnx")
        speaker_path = os.path.join(models_dir, "speaker", "speaker_encoder.onnx")
        textdet_path = os.path.join(models_dir, "vision", "text_det.onnx")
        inpaint_path = os.path.join(models_dir, "vision", "migan.onnx")
        separation_path = os.path.join(models_dir, "separation", "UVR-MDX-NET-Voc_FT.onnx")
        roformer_path = os.path.join(models_dir, "separation", "mel_band_roformer_kim_vocals.ckpt")
        rvc = (pb.ModuleStatus(available=True) if voice_conversion.rvc_available()
               else pb.ModuleStatus(available=False, reason=voice_conversion.UNAVAILABLE))
        return pb.Capabilities(
            worker_version=__version__,
            python_version=platform.python_version(),
            asr=_module("faster_whisper"),
            vad=_module("faster_whisper", "soundfile"),
            denoise=_module("onnxruntime", "numpy", "soundfile"),
            voice_conversion=rvc,
            models=[
                pb.ModelInfo(name="turbo", path=whisper_dir, installed=asr.model_installed(whisper_dir),
                             loaded=cache.is_loaded("asr")),
                pb.ModelInfo(name="denoiser", path=denoiser_path, installed=os.path.isfile(denoiser_path),
                             loaded=cache.is_loaded("denoise")),
                pb.ModelInfo(name="speaker", path=speaker_path, installed=os.path.isfile(speaker_path),
                             loaded=cache.is_loaded("speaker")),
                pb.ModelInfo(name="textdet", path=textdet_path, installed=os.path.isfile(textdet_path),
                             loaded=cache.is_loaded("text_det")),
                pb.ModelInfo(name="inpaint", path=inpaint_path, installed=os.path.isfile(inpaint_path),
                             loaded=cache.is_loaded("inpaint")),
                pb.ModelInfo(name="separation", path=separation_path, installed=os.path.isfile(separation_path),
                             loaded=cache.is_loaded("separation")),
                pb.ModelInfo(name="roformer", path=roformer_path,
                             installed=os.path.isfile(roformer_path) and importlib.util.find_spec("torch") is not None,
                             loaded=False),
            ])

    def UnloadModels(self, request, context):
        return pb.UnloadModelsResponse(unloaded=self._state.cache.unload(list(request.modules)))

    def Shutdown(self, request, context):
        self._state.request_shutdown(request.grace_ms)
        return pb.ShutdownResponse()
