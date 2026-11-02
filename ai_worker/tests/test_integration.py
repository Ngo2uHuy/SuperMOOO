"""Chạy model thật; bỏ qua nếu không đặt AIVS_TEST_MODELS_DIR (thư mục chứa asr\\turbo) và AIVS_TEST_SPEECH (WAV 16 kHz mono có tiếng nói)."""
import os
import unittest

import support
from support import pb, pb_grpc

MODELS_DIR = os.environ.get("AIVS_TEST_MODELS_DIR")
SPEECH = os.environ.get("AIVS_TEST_SPEECH")


@unittest.skipUnless(MODELS_DIR and SPEECH, "Cần AIVS_TEST_MODELS_DIR và AIVS_TEST_SPEECH")
class RealWhisperTests(support.WorkerTestCase):
    def test_turbo_returns_segments_and_detects_speech(self):
        stub = pb_grpc.AsrStub(self.channel)
        events = list(stub.Transcribe(
            pb.TranscribeRequest(audio_path=SPEECH, model="turbo", models_dir=MODELS_DIR, vad_filter=True),
            metadata=self.metadata))
        segments = [e.segment for e in events if e.HasField("segment")]
        self.assertTrue(segments)
        self.assertTrue(all(s.text for s in segments))
        self.assertTrue(events[-1].HasField("completed"))

        vad = pb_grpc.AudioToolsStub(self.channel).DetectSpeech(pb.DetectSpeechRequest(audio_path=SPEECH), metadata=self.metadata)
        self.assertTrue(vad.regions)


if __name__ == "__main__":
    unittest.main()
