"""Phụ đề cứng trong video: tìm dải phụ đề, mốc thời gian có chữ, xoá chữ bằng inpaint.
Chỉ numpy + onnxruntime; khung hình lấy qua ffmpeg do C# truyền đường dẫn. Model: PP-OCRv4 det (ONNX của RapidOCR), MI-GAN."""
import os
import subprocess
import threading
from concurrent.futures import ThreadPoolExecutor

import grpc
import numpy as np

from ..gen import aivs_worker_pb2 as pb
from ..gen import aivs_worker_pb2_grpc as pb_grpc
from ._common import ensure_file, fail

NO_WINDOW = 0x08000000 if os.name == "nt" else 0
DET_LIMIT = 960            # cạnh dài khi tìm vùng (PP-OCR mặc định 960)
DET_THRESHOLD = 0.3
MEAN = np.array([0.485, 0.456, 0.406], np.float32).reshape(1, 1, 3)
STD = np.array([0.229, 0.224, 0.225], np.float32).reshape(1, 1, 3)


def _session(path, threads):
    import onnxruntime as ort
    options = ort.SessionOptions()
    options.intra_op_num_threads = threads
    options.inter_op_num_threads = 1
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    return ort.InferenceSession(path, options, providers=["CPUExecutionProvider"])


class TextDetector:
    """PP-OCR det (DB): ảnh BGR có cạnh chia hết 32 → bản đồ xác suất chữ cùng kích thước."""

    def __init__(self, session):
        self._session = session
        self._input = session.get_inputs()[0].name

    @classmethod
    def from_file(cls, path):
        return cls(_session(path, 4))

    def prob(self, bgr):
        x = ((bgr.astype(np.float32) / 255.0 - MEAN) / STD).transpose(2, 0, 1)[None]
        return self._session.run(None, {self._input: x})[0][0, 0]


class Inpainter:
    """MI-GAN pipeline: ảnh RGB uint8 + mask (255 = giữ, 0 = vẽ lại); tự cắt quanh lỗ, resize 512 và dán lại."""

    def __init__(self, session):
        self._session = session

    @classmethod
    def from_file(cls, path):
        import onnxruntime as ort
        # ENABLE_ALL thêm bước đổi layout NCHWc làm MI-GAN chậm hơn trên CPU (đo i5-11400: 1,25 s → 1,0 s/khung).
        options = ort.SessionOptions()
        options.intra_op_num_threads = max(1, (os.cpu_count() or 2) // 2)
        options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_EXTENDED
        options.add_session_config_entry("session.intra_op.allow_spinning", "0")
        return cls(ort.InferenceSession(path, options, providers=["CPUExecutionProvider"]))

    def __call__(self, bgr, hole):
        image = np.ascontiguousarray(bgr[:, :, ::-1].transpose(2, 0, 1))[None]
        mask = np.where(hole, 0, 255).astype(np.uint8)[None, None]
        out = self._session.run(None, {"image": image, "mask": mask})[0][0]
        return np.ascontiguousarray(out.transpose(1, 2, 0)[:, :, ::-1])


def _components(binary):
    """Bbox (x0, y0, x1, y1) các thành phần liên thông theo run (không cần OpenCV/scipy)."""
    parent = []

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    runs, prev = [], []
    for y in range(binary.shape[0]):
        row = binary[y]
        if not row.any():
            prev = []
            continue
        d = np.diff(np.concatenate(([0], row.view(np.int8), [0])))
        cur, j = [], 0
        for s, e in zip(np.flatnonzero(d == 1), np.flatnonzero(d == -1)):
            label = len(parent)
            parent.append(label)
            while j < len(prev) and prev[j][1] <= s:
                j += 1
            k = j
            while k < len(prev) and prev[k][0] < e:
                ra, rb = find(label), find(prev[k][2])
                if ra != rb:
                    parent[rb] = ra
                k += 1
            cur.append((s, e, label))
            runs.append((y, s, e, label))
        prev = cur
    boxes = {}
    for y, s, e, label in runs:
        r = find(label)
        b = boxes.get(r)
        boxes[r] = (min(b[0], s), min(b[1], y), max(b[2], e), max(b[3], y + 1)) if b else (s, y, e, y + 1)
    return list(boxes.values())


def text_boxes(prob, min_height=4):
    """Hộp dòng chữ (x0, y0, x1, y1): nối ký tự cùng dòng bằng giãn ngang, rồi nở lại phần DB đã co (~unclip)."""
    binary = prob > DET_THRESHOLD
    h, w = binary.shape
    k = max(2, w // 120)
    c = np.cumsum(np.pad(binary, ((0, 0), (k, k))).astype(np.int32), axis=1)
    joined = (c[:, 2 * k:2 * k + w] - c[:, :w]) > 0
    out = []
    for x0, y0, x1, y1 in _components(joined):
        if y1 - y0 < min_height:
            continue
        pad = int(round((y1 - y0) * 0.3))
        out.append((max(0, x0 - pad), max(0, y0 - pad), min(w, x1 + pad), min(h, y1 + pad)))
    return out


def _dilate(mask, r):
    h, w = mask.shape
    c = np.cumsum(np.pad(mask, ((0, 0), (r + 1, r))).astype(np.int32), axis=1)
    mask = (c[:, 2 * r + 1:] - c[:, :w]) > 0
    c = np.cumsum(np.pad(mask, ((r + 1, r), (0, 0))).astype(np.int32), axis=0)
    return (c[2 * r + 1:] - c[:h]) > 0


def _fit32(w, h, limit):
    s = min(1.0, limit / max(w, h))
    return max(32, int(round(w * s / 32)) * 32), max(32, int(round(h * s / 32)) * 32)


def _grab(ffmpeg, video, t, w, h):
    raw = subprocess.run([ffmpeg, "-v", "error", "-nostdin", "-ss", f"{t:.3f}", "-i", video, "-frames:v", "1", "-vf", f"scale={w}:{h}",
                          "-f", "rawvideo", "-pix_fmt", "bgr24", "-"], capture_output=True, creationflags=NO_WINDOW).stdout
    return np.frombuffer(raw, np.uint8)[:w * h * 3].reshape(h, w, 3) if len(raw) >= w * h * 3 else None


def locate(ffmpeg, video, width, height, duration, detector, samples=48, is_active=lambda: True):
    """Lấy mẫu khung đều theo thời lượng, phát hiện dòng chữ, gom theo (tâm y, tâm x).
    Phụ đề: cùng dải ngang, căn giữa, có ở nhiều khung và nội dung đổi; logo/chữ cố định: điểm ảnh gần như không đổi → loại."""
    dw, dh = _fit32(width, height, DET_LIMIT)
    times = [duration * (i + 0.5) / samples for i in range(samples)]
    with ThreadPoolExecutor(4) as pool:
        frames = list(pool.map(lambda t: _grab(ffmpeg, video, t, dw, dh), times))
    items = []
    for i, frame in enumerate(frames):
        if frame is None:
            continue
        if not is_active():
            return None, 0
        for x0, y0, x1, y1 in text_boxes(detector.prob(frame)):
            bw, bh = (x1 - x0) / dw, (y1 - y0) / dh
            if 0.012 <= bh <= 0.2 and bw >= bh * dh / dw * 1.2:
                items.append((i, x0 / dw, y0 / dh, x1 / dw, y1 / dh))
    count = sum(f is not None for f in frames)
    return _cluster(items, frames, count, dw, dh), count


def _cluster(items, frames, count, dw, dh):
    if not items or count == 0:
        return None
    cy = np.array([(it[2] + it[4]) / 2 for it in items])
    hs = np.array([it[4] - it[2] for it in items])
    cx = np.array([(it[1] + it[3]) / 2 for it in items])
    used = np.zeros(len(items), bool)
    candidates = []
    for idx in np.argsort(cy):
        if used[idx]:
            continue
        m = (~used) & (np.abs(cy - cy[idx]) < np.maximum(hs, hs[idx]) * 0.5) & (np.abs(cx - cx[idx]) < 0.08)
        my, mx, mh = np.median(cy[m]), np.median(cx[m]), np.median(hs[m])
        m = (~used) & (np.abs(cy - my) < mh * 0.6) & (np.abs(cx - mx) < 0.08)
        if not m.any():
            continue
        used |= m
        group = np.flatnonzero(m)
        seen = sorted({items[i][0] for i in group})
        if len(seen) < max(3, count * 0.08):
            continue
        x0s, y0s = np.array([items[i][1] for i in group]), np.array([items[i][2] for i in group])
        x1s, y1s = np.array([items[i][3] for i in group]), np.array([items[i][4] for i in group])
        box = [np.percentile(x0s, 5), np.percentile(y0s, 10), np.percentile(x1s, 95), np.percentile(y1s, 90)]
        a, b = int(box[0] * dw), int(box[1] * dh)
        c, d = max(a + 2, int(box[2] * dw)), max(b + 2, int(box[3] * dh))
        crops = [frames[f][b:d, a:c].mean(axis=2) for f in seen]
        change = float(np.median([np.abs(crops[k] - crops[k - 1]).mean() for k in range(1, len(crops))]))
        width_std = float(np.std(x1s - x0s))
        static = change < 5
        ratio = len(seen) / count
        centered = 1.0 - min(1.0, abs((box[0] + box[2]) / 2 - 0.5) / 0.25)
        edge = 1.0 if box[1] > 0.55 or box[3] < 0.3 else 0.6
        score = ratio * (0.5 + 0.5 * centered) * edge * (0.15 if static else 1.0) * min(1.0, 0.4 + width_std * 20 + change / 30)
        candidates.append(dict(box=[float(v) for v in box], ratio=ratio, change=change, static=static, score=score, frames=len(seen)))
    if not candidates:
        return None
    candidates.sort(key=lambda c: -c["score"])
    best = candidates[0]
    bx = best["box"]
    # Phụ đề hai dòng: dòng kề trên/dưới, cùng tâm, nội dung đổi.
    for cand in candidates[1:]:
        cb, hh = cand["box"], bx[3] - bx[1]
        if cand["static"] or cand["change"] < 15 or cand["ratio"] < best["ratio"] * 0.25:
            continue
        if abs((cb[0] + cb[2]) / 2 - (bx[0] + bx[2]) / 2) < 0.08 and (0 <= bx[1] - cb[3] < hh * 0.8 or 0 <= cb[1] - bx[3] < hh * 0.8):
            bx = [min(bx[0], cb[0]), min(bx[1], cb[1]), max(bx[2], cb[2]), max(bx[3], cb[3])]
    # Đệm dọc và nới ngang tối thiểu 40% khung: câu dài hơn các câu đã thấy trong mẫu.
    hh, mid = bx[3] - bx[1], (bx[0] + bx[2]) / 2
    half = max((bx[2] - bx[0]) / 2 + 0.02, 0.2)
    box = [max(0.0, mid - half), max(0.0, bx[1] - hh * 0.3), min(1.0, mid + half), min(1.0, bx[3] + hh * 0.3)]
    second = candidates[1]["score"] if len(candidates) > 1 else 0.0
    confidence = 0.1 if best["static"] else min(1.0, best["ratio"] * 1.6) * (1 - 0.5 * second / max(best["score"], 1e-6))
    return dict(box=box, confidence=float(confidence), frames=best["frames"])


def _band(width, height, box):
    x, y, w, h = (v / 100 for v in box)
    cw, ch = max(2, int(width * w) // 2 * 2), max(2, int(height * h) // 2 * 2)
    cx, cy = min(int(width * x), width - cw), min(int(height * y), height - ch)
    return cx, cy, cw, ch


def _decoder(ffmpeg, video, vf):
    return subprocess.Popen([ffmpeg, "-v", "error", "-nostdin", "-threads", "4", "-i", video, "-vf", vf, "-f", "rawvideo", "-pix_fmt", "bgr24", "-"],
                            stdout=subprocess.PIPE, stdin=subprocess.DEVNULL, creationflags=NO_WINDOW)


def timeline(ffmpeg, video, width, height, duration, box, detector, step=0.25, merge_gap=0.35, pad=0.15,
             on_progress=lambda f: None, is_active=lambda: True):
    """Giải mã riêng dải đã chọn mỗi `step` giây; mẫu có dòng chữ phủ qua giữa dải → đoạn [a, b] đã gộp khe ngắn và đệm hai đầu."""
    cx, cy, cw, ch = _band(width, height, box)
    s = min(1.0, DET_LIMIT / cw)
    ow, oh = max(32, int(round(cw * s / 32)) * 32), max(32, int(round(ch * s / 32)) * 32)
    proc = _decoder(ffmpeg, video, f"fps={1 / step},crop={cw}:{ch}:{cx}:{cy},scale={ow}:{oh}")
    size, present, total = ow * oh * 3, [], max(1, int(duration / step))
    try:
        while True:
            buf = proc.stdout.read(size)
            if len(buf) < size:
                break
            if not is_active():
                return None
            lines = [b for b in text_boxes(detector.prob(np.frombuffer(buf, np.uint8).reshape(oh, ow, 3)))
                     if b[3] - b[1] > oh * 0.2 and b[0] < ow * 0.56 and b[2] > ow * 0.44]
            present.append(sum((b[2] - b[0]) * (b[3] - b[1]) for b in lines) > ow * oh * 0.004)
            if len(present) % 20 == 0:
                on_progress(min(0.99, len(present) / total))
    finally:
        proc.kill()
        proc.wait()
    segments = []
    for i, on in enumerate(present):
        if not on:
            continue
        a, b = i * step - step / 2, i * step + step / 2
        if segments and a - segments[-1][1] <= merge_gap:
            segments[-1][1] = b
        else:
            segments.append([a, b])
    return [(round(max(0.0, a - pad), 3), round(min(duration, b + pad), 3)) for a, b in segments]


def inpaint(ffmpeg, video, width, height, fps, box, segments, output, detector, inpainter, keyframe_every=1, context_threshold=4.0,
            on_progress=lambda f: None, is_active=lambda: True):
    """Ghi video 'patch' (cửa sổ = dải + ngữ cảnh xung quanh) cùng fps nguồn; khung ngoài đoạn có chữ giữ nguyên.
    Dùng lại phần đã vẽ khi mặt nạ gần như trùng và nền quanh chữ không đổi (giảm nhấp nháy, nhanh hơn);
    keyframe_every > 1 dùng lại cả khi nền đổi (chế độ nhanh, có thể bóng mờ khi cảnh chuyển động)."""
    bx, by, bw, bh = _band(width, height, box)
    # MI-GAN cần ngữ cảnh quanh lỗ: cửa sổ nới 1,5 chiều cao dải trên/dưới, 1 chiều cao hai bên (đo: vệt tối trong lỗ 14% → 4–6%).
    x0, x1 = max(0, bx - bh) // 2 * 2, min(width, bx + bw + bh) // 2 * 2
    y0, y1 = max(0, by - bh * 3 // 2) // 2 * 2, min(height, by + bh + bh * 3 // 2) // 2 * 2
    ww, wh = x1 - x0, y1 - y0
    top = by - y0
    # Dò chữ trên dải + nửa chiều cao dải mỗi phía: dòng chữ sát mép vùng vẫn trọn hộp (cả viền/bóng chữ).
    dt = max(0, top - bh // 2)
    dh32, dw32 = (min(wh - dt, 2 * bh) + 31) // 32 * 32, (ww + 31) // 32 * 32
    # Ma trận màu cố định hai chiều + x264 lossless để phần không vẽ lại khớp điểm ảnh nguồn sau overlay (không lộ viền cửa sổ).
    proc = _decoder(ffmpeg, video, f"crop={ww}:{wh}:{x0}:{y0},scale=in_color_matrix=bt709:in_range=tv,format=bgr24")
    partial = output + ".partial.mp4"
    enc = subprocess.Popen([ffmpeg, "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{ww}x{wh}", "-r", f"{fps:.6f}", "-i", "-",
                            "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p", "-c:v", "libx264", "-preset", "veryfast",
                            "-qp", "0", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv", partial],
                           stdin=subprocess.PIPE, creationflags=NO_WINDOW)
    size = ww * wh * 3
    total = max(1, int(round(segments[-1][1] * fps))) if segments else 1
    stats = dict(frames=0, text=0, inpainted=0)
    last, since, seg, index = None, 0, 0, 0
    try:
        while True:
            buf = proc.stdout.read(size)
            if len(buf) < size:
                break
            if not is_active():
                return None
            t = index / fps
            index += 1
            stats["frames"] += 1
            if index % 30 == 0:
                on_progress(min(0.99, index / total))
            while seg < len(segments) and segments[seg][1] < t:
                seg += 1
            if seg >= len(segments):
                break   # sau đoạn chữ cuối: overlay eof_action=pass để lộ lại nguồn
            if t < segments[seg][0]:
                enc.stdin.write(buf)
                last = None
                continue
            frame = np.frombuffer(buf, np.uint8).reshape(wh, ww, 3)
            band = np.zeros((dh32, dw32, 3), np.uint8)
            src = frame[dt:dt + dh32]
            band[:src.shape[0], :src.shape[1]] = src
            hole = np.zeros((wh, ww), bool)
            for b0, b1, b2, b3 in text_boxes(detector.prob(band)):
                mid = dt + (b1 + b3) / 2
                if b3 - b1 < bh * 0.15 or not top - bh * 0.25 <= mid <= top + bh * 1.25:
                    continue
                p = int((b3 - b1) * 0.5) + 3   # DB (nhất là PP-OCRv4) co vùng chữ → nở thêm để phủ cả viền/bóng chữ
                hole[max(0, dt + b1 - p):dt + b3 + p, max(0, b0 - p):b2 + p] = True
            if not hole.any():
                enc.stdin.write(buf)
                last = None
                continue
            stats["text"] += 1
            reuse = False
            if last is not None:
                lm, lframe, lfill = last
                iou = (lm & hole).sum() / max(1, (lm | hole).sum())
                if iou > 0.9:
                    ring = ~_dilate(lm | hole, 8)
                    diff = np.abs(frame.astype(np.int16) - lframe.astype(np.int16)).max(axis=2)[ring]
                    reuse = (diff.mean() if diff.size else 0) < context_threshold or since < keyframe_every - 1
            if reuse:
                out = frame.copy()
                cover = lm | hole
                out[cover] = lfill[cover]
                since += 1
            else:
                out = inpainter(frame, hole)
                last = (hole, frame.copy(), out)
                stats["inpainted"] += 1
                since = 0
            enc.stdin.write(out.tobytes())
        enc.stdin.close()
        if enc.wait() != 0:
            raise RuntimeError("ffmpeg không ghi được video patch.")
        os.replace(partial, output)
    finally:
        proc.kill()
        proc.wait()
        if enc.poll() is None:
            enc.kill()
            enc.wait()
        if os.path.exists(partial):
            os.remove(partial)
    stats.update(x=x0, y=y0, width=ww, height=wh)
    return stats


def _stream(work, progress_event, is_active):
    """Chạy work(on_progress) ở luồng phụ (gRPC sync) và phát tiến độ mỗi giây; trả (kết quả, lỗi)."""
    events, box = [], {}

    def run():
        try:
            box["result"] = work(events.append)
        except Exception as error:
            box["error"] = error

    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    sent = None
    while thread.is_alive():
        thread.join(1.0)
        if events and events[-1] != sent and is_active():
            sent = events[-1]
            yield progress_event(sent)
    yield box


class VideoTextService(pb_grpc.VideoTextServicer):
    def __init__(self, state, detector_factory=TextDetector.from_file, inpainter_factory=Inpainter.from_file):
        self._state = state
        self._detector_factory = detector_factory
        self._inpainter_factory = inpainter_factory

    def _video(self, request, context):
        ensure_file(context, request.ffmpeg_path, "ffmpeg")
        ensure_file(context, request.video_path, "file video")
        ensure_file(context, request.det_model_path, "model phát hiện chữ")
        if request.width <= 0 or request.height <= 0 or request.duration <= 0:
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, "Thiếu kích thước/thời lượng video.")
        try:
            return self._state.cache.get("text_det", request.det_model_path, lambda: self._detector_factory(request.det_model_path))
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Không nạp được model phát hiện chữ: {error}")

    @staticmethod
    def _box(request, context):
        b = request.box
        if b.w <= 0 or b.h <= 0 or b.x < 0 or b.y < 0 or b.x + b.w > 100.001 or b.y + b.h > 100.001:
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, "Vùng phụ đề phải nằm trong khung hình (0–100%).")
        return b.x, b.y, b.w, b.h

    def LocateSubtitles(self, request, context):
        detector = self._video(request, context)
        try:
            with self._state.cache.inference_lock:
                found, count = locate(request.ffmpeg_path, request.video_path, request.width, request.height, request.duration,
                                      detector, request.sample_count or 48, context.is_active)
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Tìm vùng phụ đề lỗi: {error}")
        if found is None:
            return pb.LocateSubtitlesResponse(found=False, frames_sampled=count)
        x0, y0, x1, y1 = found["box"]
        return pb.LocateSubtitlesResponse(
            found=True, confidence=round(found["confidence"], 3), frames_with_text=found["frames"], frames_sampled=count,
            box=pb.PercentRect(x=round(x0 * 100, 1), y=round(y0 * 100, 1), w=round((x1 - x0) * 100, 1), h=round((y1 - y0) * 100, 1)))

    def TextTimeline(self, request, context):
        detector = self._video(request, context)
        box = self._box(request, context)
        yield pb.TextTimelineEvent(progress=pb.Progress(fraction=0, message="Đang dò lúc có chữ..."))

        def work(on_progress):
            with self._state.cache.inference_lock:
                return timeline(request.ffmpeg_path, request.video_path, request.width, request.height, request.duration, box, detector,
                                request.step or 0.25, on_progress=on_progress, is_active=context.is_active)

        for event in _stream(work, lambda f: pb.TextTimelineEvent(progress=pb.Progress(fraction=f, message="Đang dò lúc có chữ...")),
                             context.is_active):
            if not isinstance(event, dict):
                yield event
                continue
            if "error" in event:
                fail(context, grpc.StatusCode.INTERNAL, f"Dò lúc có chữ lỗi: {event['error']}")
            if event.get("result") is None:
                return
            yield pb.TextTimelineEvent(result=pb.TextTimelineResult(
                segments=[pb.TimeRange(start=a, end=b) for a, b in event["result"]], duration=request.duration))

    def InpaintSubtitles(self, request, context):
        detector = self._video(request, context)
        box = self._box(request, context)
        ensure_file(context, request.inpaint_model_path, "model inpaint")
        if not request.output_path or not os.path.isabs(request.output_path):
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, "output_path phải là đường dẫn tuyệt đối.")
        if request.fps <= 0:
            fail(context, grpc.StatusCode.INVALID_ARGUMENT, "Thiếu fps video.")
        try:
            inpainter = self._state.cache.get("inpaint", request.inpaint_model_path,
                                              lambda: self._inpainter_factory(request.inpaint_model_path))
        except Exception as error:
            fail(context, grpc.StatusCode.INTERNAL, f"Không nạp được model inpaint: {error}")
        segments = sorted((r.start, r.end) for r in request.segments if r.end > r.start) or [(0.0, request.duration)]
        yield pb.InpaintEvent(progress=pb.Progress(fraction=0, message="Đang xoá phụ đề gốc (inpaint)..."))

        def work(on_progress):
            with self._state.cache.inference_lock:
                return inpaint(request.ffmpeg_path, request.video_path, request.width, request.height, request.fps, box, segments,
                               request.output_path, detector, inpainter, max(1, request.keyframe_every or 1),
                               on_progress=on_progress, is_active=context.is_active)

        for event in _stream(work, lambda f: pb.InpaintEvent(progress=pb.Progress(fraction=f, message="Đang xoá phụ đề gốc (inpaint)...")),
                             context.is_active):
            if not isinstance(event, dict):
                yield event
                continue
            if "error" in event:
                fail(context, grpc.StatusCode.INTERNAL, f"Xoá phụ đề (inpaint) lỗi: {event['error']}")
            stats = event.get("result")
            if stats is None:
                return
            yield pb.InpaintEvent(completed=pb.InpaintCompleted(
                output_path=request.output_path, x=stats["x"], y=stats["y"], width=stats["width"], height=stats["height"],
                frames=stats["frames"], inpainted=stats["inpainted"]))
