import hmac
import threading
import time
from concurrent import futures

import grpc

from .gen import aivs_worker_pb2_grpc as pb_grpc
from .model_cache import ModelCache
from .services import asr, audio_tools, control, video_text, voice_conversion

TOKEN_HEADER = "x-aivs-token"


class WorkerState:
    def __init__(self, models_dir):
        self.models_dir = models_dir
        self.cache = ModelCache()
        self.last_activity = time.monotonic()
        self.server = None

    def request_shutdown(self, grace_ms):
        # Chờ một nhịp để phản hồi Shutdown kịp gửi trước khi server dừng.
        threading.Timer(0.05, lambda: self.server.stop(max(0, grace_ms) / 1000)).start()


class TokenInterceptor(grpc.ServerInterceptor):
    def __init__(self, token, state):
        self._token = token.encode()
        self._state = state
        self._deny = grpc.unary_unary_rpc_method_handler(self._abort)

    @staticmethod
    def _abort(request, context):
        context.abort(grpc.StatusCode.UNAUTHENTICATED, "Token worker không hợp lệ.")

    def intercept_service(self, continuation, handler_call_details):
        self._state.last_activity = time.monotonic()
        metadata = dict(handler_call_details.invocation_metadata or ())
        supplied = metadata.get(TOKEN_HEADER, "").encode()
        if not hmac.compare_digest(supplied, self._token):
            return self._deny
        return continuation(handler_call_details)


def build_server(models_dir, token, port=0, idle_seconds=0, asr_loader=None, denoiser_factory=None, vad=None):
    """Trả về (server, port, state). Chỉ bind 127.0.0.1; token bắt buộc ở mọi RPC."""
    if not token:
        raise ValueError("Thiếu token worker.")
    state = WorkerState(models_dir)
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=2),
                         interceptors=[TokenInterceptor(token, state)])
    state.server = server
    pb_grpc.add_WorkerControlServicer_to_server(control.ControlService(state), server)
    pb_grpc.add_AsrServicer_to_server(
        asr.AsrService(state, **({"loader": asr_loader} if asr_loader else {})), server)
    tools_args = {k: v for k, v in (("denoiser_factory", denoiser_factory), ("vad", vad)) if v}
    pb_grpc.add_AudioToolsServicer_to_server(audio_tools.AudioToolsService(state, **tools_args), server)
    pb_grpc.add_VoiceConversionServicer_to_server(voice_conversion.VoiceConversionService(state), server)
    pb_grpc.add_VideoTextServicer_to_server(video_text.VideoTextService(state), server)
    bound = server.add_insecure_port(f"127.0.0.1:{port}")
    if not bound:
        raise RuntimeError("Không bind được cổng loopback.")
    if idle_seconds > 0:
        threading.Thread(target=_watch_idle, args=(state, idle_seconds), daemon=True).start()
    return server, bound, state


def _watch_idle(state, idle_seconds):
    while True:
        time.sleep(min(1.0, idle_seconds))
        # Đang suy luận thì giữ inference_lock nên không bị coi là rảnh.
        if time.monotonic() - state.last_activity < idle_seconds:
            continue
        if state.cache.inference_lock.acquire(blocking=False):
            state.cache.inference_lock.release()
            state.server.stop(0)
            return
