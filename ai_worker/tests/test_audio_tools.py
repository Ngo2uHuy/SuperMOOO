import time
import unittest

import numpy as np
import soundfile as sf

import support
from support import grpc, pb, pb_grpc


class IdentityCore:
    """Session giả: trả nguyên (mag, cos, sin) nên kết quả phải gần bằng tín hiệu vào."""

    def __init__(self, delay=0.0):
        self.calls = 0
        self.delay = delay

    def run(self, _, inputs):
        self.calls += 1
        time.sleep(self.delay)
        return [inputs["mag"], inputs["cos"], inputs["sin"]]


class AudioToolsTests(support.WorkerTestCase):
    def server_args(self):
        from aivs_worker.services.audio_tools import OnnxDenoiser
        self.core = IdentityCore()
        return dict(denoiser_factory=lambda path: OnnxDenoiser(self.core))

    def wav(self, name, samples, rate):
        path = self.path(name)
        sf.write(path, samples, rate, subtype="PCM_16")
        return path

    def test_detect_speech_on_silence_runs_real_vad(self):
        path = self.wav("silence.wav", np.zeros(16000, dtype="float32"), 16000)
        # Dùng VAD thật (Silero đi kèm faster-whisper) nên cần gói đã cài, không cần model tải thêm.
        stub = pb_grpc.AudioToolsStub(self.channel)
        response = stub.DetectSpeech(pb.DetectSpeechRequest(audio_path=path), metadata=self.metadata)
        self.assertEqual(len(response.regions), 0)
        self.assertAlmostEqual(response.duration, 1.0, places=3)

    def test_detect_speech_converts_samples_to_seconds(self):
        path = self.wav("tone.wav", np.zeros(32000, dtype="float32"), 16000)
        self.server.stop(0).wait()
        server, port, _ = support.build_server(
            self.models_dir, support.TOKEN, vad=lambda audio, options: [{"start": 8000, "end": 16000}])
        server.start()
        self.addCleanup(lambda: server.stop(0).wait())
        with grpc.insecure_channel(f"127.0.0.1:{port}") as channel:
            response = pb_grpc.AudioToolsStub(channel).DetectSpeech(
                pb.DetectSpeechRequest(audio_path=path), metadata=self.metadata)
        self.assertEqual([(r.start, r.end) for r in response.regions], [(0.5, 1.0)])

    def test_detect_speech_rejects_wrong_sample_rate(self):
        path = self.wav("rate.wav", np.zeros(44100, dtype="float32"), 44100)
        with self.assertRaises(grpc.RpcError) as caught:
            pb_grpc.AudioToolsStub(self.channel).DetectSpeech(pb.DetectSpeechRequest(audio_path=path), metadata=self.metadata)
        self.assertEqual(caught.exception.code(), grpc.StatusCode.INVALID_ARGUMENT)

    def test_denoise_roundtrip_preserves_signal_and_reports_completed(self):
        t = np.arange(44100) / 44100
        signal = (0.5 * np.sin(2 * np.pi * 440 * t)).astype("float32")
        source = self.wav("in.wav", signal, 44100)
        model = self.path("denoiser.onnx")
        open(model, "w").close()
        output = self.path("out.wav")
        events = list(pb_grpc.AudioToolsStub(self.channel).Denoise(
            pb.DenoiseRequest(input_path=source, output_path=output, model_path=model), metadata=self.metadata))
        self.assertTrue(events[-1].HasField("completed"))
        cleaned, rate = sf.read(output, dtype="float32")
        self.assertEqual(rate, 44100)
        self.assertEqual(len(cleaned), len(signal))
        # Lõi đồng nhất: phần giữa khớp (chỉ lệch do lượng tử 16 bit); hai mép lệch là hành vi gốc của iSTFT bỏ khung cuối.
        self.assertLess(float(np.abs(cleaned[1000:-1000] - signal[1000:-1000]).max()), 1e-3)

    def test_denoise_cancel_stops_before_all_chunks(self):
        self.core.delay = 0.2
        source = self.wav("long.wav", np.full(44100 * 120, 0.1, dtype="float32"), 44100)
        model = self.path("denoiser.onnx")
        open(model, "w").close()
        call = pb_grpc.AudioToolsStub(self.channel).Denoise(
            pb.DenoiseRequest(input_path=source, output_path=self.path("o.wav"), model_path=model), metadata=self.metadata)
        for event in call:
            if event.HasField("progress") and event.progress.fraction > 0:
                break
        call.cancel()
        time.sleep(0.8)
        self.assertLess(self.core.calls, 4)

    def test_denoise_missing_model_is_not_found(self):
        source = self.wav("in.wav", np.zeros(44100, dtype="float32"), 44100)
        with self.assertRaises(grpc.RpcError) as caught:
            list(pb_grpc.AudioToolsStub(self.channel).Denoise(
                pb.DenoiseRequest(input_path=source, output_path=self.path("o.wav"),
                                  model_path=self.path("none.onnx")), metadata=self.metadata))
        self.assertEqual(caught.exception.code(), grpc.StatusCode.NOT_FOUND)


if __name__ == "__main__":
    unittest.main()
