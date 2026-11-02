import unittest

import support
from support import grpc, pb, pb_grpc


class ControlTests(support.WorkerTestCase):
    def test_capabilities_lists_models_and_rvc_unavailable(self):
        caps = pb_grpc.WorkerControlStub(self.channel).GetCapabilities(pb.GetCapabilitiesRequest(), metadata=self.metadata)
        self.assertTrue(caps.asr.available)
        self.assertEqual({m.name for m in caps.models}, {"turbo", "denoiser"})
        self.assertFalse(any(m.installed for m in caps.models))
        self.assertFalse(caps.voice_conversion.available)
        self.assertTrue(caps.voice_conversion.reason)

    def test_voice_conversion_unimplemented_without_package(self):
        stub = pb_grpc.VoiceConversionStub(self.channel)
        with self.assertRaises(grpc.RpcError) as caught:
            stub.ListModels(pb.ListVoiceModelsRequest(), metadata=self.metadata)
        self.assertEqual(caught.exception.code(), grpc.StatusCode.UNIMPLEMENTED)
        with self.assertRaises(grpc.RpcError) as caught:
            list(stub.Convert(pb.VoiceConvertRequest(), metadata=self.metadata))
        self.assertEqual(caught.exception.code(), grpc.StatusCode.UNIMPLEMENTED)

    def test_unload_models_releases_loaded(self):
        self.state.cache.get("asr", "k", lambda: object())
        stub = pb_grpc.WorkerControlStub(self.channel)
        response = stub.UnloadModels(pb.UnloadModelsRequest(), metadata=self.metadata)
        self.assertEqual(list(response.unloaded), ["asr"])
        self.assertFalse(self.state.cache.is_loaded("asr"))

    def test_shutdown_stops_server(self):
        stub = pb_grpc.WorkerControlStub(self.channel)
        stub.Shutdown(pb.ShutdownRequest(grace_ms=0), metadata=self.metadata)
        # wait_for_termination trả True khi hết timeout mà server vẫn chạy.
        self.assertFalse(self.server.wait_for_termination(timeout=5))


if __name__ == "__main__":
    unittest.main()
