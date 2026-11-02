import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import grpc  # noqa: E402

from aivs_worker.gen import aivs_worker_pb2 as pb  # noqa: E402,F401
from aivs_worker.gen import aivs_worker_pb2_grpc as pb_grpc  # noqa: E402,F401
from aivs_worker.server import TOKEN_HEADER, build_server  # noqa: E402

TOKEN = "test-token"


class WorkerTestCase(unittest.TestCase):
    def server_args(self):
        return {}

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.models_dir = os.path.join(self.tmp.name, "models")
        os.makedirs(self.models_dir)
        self.server, port, self.state = build_server(self.models_dir, TOKEN, **self.server_args())
        self.server.start()
        self.addCleanup(lambda: self.server.stop(0).wait())
        self.channel = grpc.insecure_channel(f"127.0.0.1:{port}")
        self.addCleanup(self.channel.close)
        self.metadata = ((TOKEN_HEADER, TOKEN),)

    def path(self, name):
        return os.path.join(self.tmp.name, name)
