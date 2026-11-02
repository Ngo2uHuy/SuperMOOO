import gc
import threading


class ModelCache:
    """Nạp model lười, giữ theo (module, khoá); inference_lock tuần tự hoá suy luận vì model CPU không an toàn đa luồng."""

    def __init__(self):
        self._lock = threading.RLock()
        self._items = {}
        self.inference_lock = threading.RLock()

    def get(self, module, key, loader):
        with self._lock:
            entry = self._items.get(module)
            if entry is None or entry[0] != key:
                self._items[module] = (key, loader())
            return self._items[module][1]

    def is_loaded(self, module):
        with self._lock:
            return module in self._items

    def unload(self, modules=()):
        # Đang suy luận thì không giải phóng, tránh làm hỏng lời gọi đang chạy.
        if not self.inference_lock.acquire(blocking=False):
            return []
        try:
            with self._lock:
                names = [m for m in (modules or list(self._items)) if m in self._items]
                for name in names:
                    del self._items[name]
            gc.collect()
            return names
        finally:
            self.inference_lock.release()
