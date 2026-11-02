import unittest

import support
from support import grpc, pb, pb_grpc


class AuthTests(support.WorkerTestCase):
    def test_wrong_token_rejected(self):
        stub = pb_grpc.WorkerControlStub(self.channel)
        with self.assertRaises(grpc.RpcError) as caught:
            stub.GetCapabilities(pb.GetCapabilitiesRequest(), metadata=((support.TOKEN_HEADER, "sai"),))
        self.assertEqual(caught.exception.code(), grpc.StatusCode.UNAUTHENTICATED)

    def test_missing_token_rejected(self):
        stub = pb_grpc.WorkerControlStub(self.channel)
        with self.assertRaises(grpc.RpcError) as caught:
            stub.GetCapabilities(pb.GetCapabilitiesRequest())
        self.assertEqual(caught.exception.code(), grpc.StatusCode.UNAUTHENTICATED)

    def test_streaming_rpc_rejected_without_token(self):
        stub = pb_grpc.AsrStub(self.channel)
        with self.assertRaises(grpc.RpcError) as caught:
            list(stub.Transcribe(pb.TranscribeRequest(audio_path="C:\\x.wav")))
        self.assertEqual(caught.exception.code(), grpc.StatusCode.UNAUTHENTICATED)

    def test_valid_token_accepted(self):
        stub = pb_grpc.WorkerControlStub(self.channel)
        self.assertTrue(stub.GetCapabilities(pb.GetCapabilitiesRequest(), metadata=self.metadata).worker_version)

    def test_empty_token_refuses_to_start(self):
        with self.assertRaises(ValueError):
            support.build_server(self.models_dir, "")


if __name__ == "__main__":
    unittest.main()
