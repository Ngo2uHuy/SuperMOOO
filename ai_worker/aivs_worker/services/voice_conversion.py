import importlib.util
import os

import grpc

from ..gen import aivs_worker_pb2 as pb
from ..gen import aivs_worker_pb2_grpc as pb_grpc
from ._common import ensure_file, fail

UNAVAILABLE = "Chưa cài module chuyển giọng RVC (gói rvc-python). Cài ở Cài đặt > AI worker."


def rvc_available():
    return importlib.util.find_spec("rvc_python") is not None


class VoiceConversionService(pb_grpc.VoiceConversionServicer):
    def __init__(self, state):
        self._state = state

    def ListModels(self, request, context):
        if not rvc_available():
            fail(context, grpc.StatusCode.UNIMPLEMENTED, UNAVAILABLE)
        root = request.models_dir or os.path.join(self._state.models_dir, "rvc")
        models = []
        if os.path.isdir(root):
            for name in sorted(os.listdir(root)):
                stem, ext = os.path.splitext(name)
                if ext.lower() != ".pth":
                    continue
                index = os.path.join(root, stem + ".index")
                models.append(pb.VoiceModel(name=stem, model_path=os.path.join(root, name),
                                            index_path=index if os.path.isfile(index) else ""))
        return pb.ListVoiceModelsResponse(models=models)

    def Convert(self, request, context):
        if not rvc_available():
            fail(context, grpc.StatusCode.UNIMPLEMENTED, UNAVAILABLE)
        ensure_file(context, request.input_path, "file audio")
        ensure_file(context, request.model_path, "model RVC")
        if not request.output_path or not os.path.isabs(request.output_path):
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, "output_path phải là đường dẫn tuyệt đối.")
        yield pb.TaskEvent(progress=pb.Progress(fraction=-1, message="Đang chuyển giọng..."))
        try:
            from rvc_python.infer import RVCInference
            with self._state.cache.inference_lock:
                rvc = RVCInference(device=request.device or "cpu:0")
                rvc.load_model(request.model_path, index_path=request.index_path)
                rvc.set_params(f0method=request.f0_method or "rmvpe", f0up_key=request.pitch_semitones,
                               index_rate=request.index_rate or 0.5)
                partial = request.output_path + ".partial.wav"
                rvc.infer_file(request.input_path, partial)
            os.replace(partial, request.output_path)
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Chuyển giọng lỗi: {error}")
        yield pb.TaskEvent(completed=pb.TaskCompleted(output_path=request.output_path, duration=0))
