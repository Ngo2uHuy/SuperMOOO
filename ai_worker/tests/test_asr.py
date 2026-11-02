import os
import time
import unittest
from types import SimpleNamespace

import support
from support import grpc, pb, pb_grpc
from aivs_worker.services.asr import normalize_language


def make_wav(path, seconds=1, rate=16000):
    import numpy as np
    import soundfile as sf
    sf.write(path, np.zeros(int(seconds * rate), dtype="float32"), rate, subtype="PCM_16")


class FakeModel:
    def __init__(self, count=3):
        self.count = count
        self.produced = 0
        self.options = None

    def transcribe(self, path, **options):
        self.options = options
        info = SimpleNamespace(language="vi", language_probability=0.9, duration=float(self.count))
        return self._segments(), info

    def _segments(self):
        for i in range(self.count):
            self.produced += 1
            text = "  " if i == 1 else f" câu {i} "
            word = SimpleNamespace(start=i, end=i + 0.5, word="câu", probability=0.8)
            yield SimpleNamespace(start=float(i), end=i + 1.0, text=text, avg_logprob=-0.1,
                                  no_speech_prob=0.01, words=[word])
            time.sleep(0.002)


class AsrTests(support.WorkerTestCase):
    def server_args(self):
        self.model = FakeModel()
        return dict(asr_loader=lambda path, device, compute: self.model)

    def setUp(self):
        super().setUp()
        self.turbo = os.path.join(self.models_dir, "asr", "turbo")
        os.makedirs(self.turbo)
        for name in ("model.bin", "config.json", "tokenizer.json"):
            open(os.path.join(self.turbo, name), "w").close()
        self.wav = self.path("a.wav")
        make_wav(self.wav)
        self.stub = pb_grpc.AsrStub(self.channel)

    def request(self, **kwargs):
        return pb.TranscribeRequest(audio_path=self.wav, model="turbo", **kwargs)

    def test_stream_ends_with_single_completed_and_skips_blank(self):
        events = list(self.stub.Transcribe(self.request(language="fil", word_timestamps=True, vad_filter=True),
                                           metadata=self.metadata))
        segments = [e.segment for e in events if e.HasField("segment")]
        completed = [e.completed for e in events if e.HasField("completed")]
        self.assertEqual([s.text for s in segments], ["câu 0", "câu 2"])
        self.assertEqual([s.index for s in segments], [1, 2])
        self.assertEqual(len(completed), 1)
        self.assertTrue(events[-1].HasField("completed"))
        self.assertEqual(completed[0].segment_count, 2)
        self.assertEqual(completed[0].language, "vi")
        self.assertEqual(self.model.options["language"], "tl")
        self.assertTrue(self.model.options["vad_filter"])
        self.assertEqual(self.model.options["beam_size"], 5)

    def test_missing_model_is_failed_precondition(self):
        os.remove(os.path.join(self.turbo, "model.bin"))
        with self.assertRaises(grpc.RpcError) as caught:
            list(self.stub.Transcribe(self.request(), metadata=self.metadata))
        self.assertEqual(caught.exception.code(), grpc.StatusCode.FAILED_PRECONDITION)

    def test_missing_audio_is_not_found(self):
        with self.assertRaises(grpc.RpcError) as caught:
            list(self.stub.Transcribe(pb.TranscribeRequest(audio_path=self.path("none.wav")), metadata=self.metadata))
        self.assertEqual(caught.exception.code(), grpc.StatusCode.NOT_FOUND)

    def test_cancel_stops_stream(self):
        self.model.count = 100_000
        call = self.stub.Transcribe(self.request(), metadata=self.metadata)
        next(call)
        call.cancel()
        time.sleep(0.3)
        settled = self.model.produced
        time.sleep(0.3)
        self.assertEqual(self.model.produced, settled)
        self.assertLess(settled, 100_000)

    def test_language_normalization(self):
        self.assertEqual(normalize_language("pt-BR"), "pt")
        self.assertEqual(normalize_language("cmn"), "zh")
        self.assertIsNone(normalize_language("auto"))
        self.assertIsNone(normalize_language(""))


if __name__ == "__main__":
    unittest.main()
