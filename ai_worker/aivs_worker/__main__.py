import argparse
import os
import sys
import threading

from .server import build_server


def _exit_when_stdin_closes(server):
    # C# giữ stdin mở; cha chết thì pipe đóng, worker tự thoát thay vì mồ côi.
    sys.stdin.buffer.read()
    server.stop(0)


def main(argv=None):
    parser = argparse.ArgumentParser(prog="aivs_worker")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--models-dir", required=True)
    parser.add_argument("--idle-seconds", type=int, default=0)
    parser.add_argument("--watch-stdin", action="store_true")
    args = parser.parse_args(argv)

    token = os.environ.get("AIVS_WORKER_TOKEN", "")
    if not token:
        print("Thiếu biến môi trường AIVS_WORKER_TOKEN.", file=sys.stderr)
        return 2
    server, port, _ = build_server(os.path.abspath(args.models_dir), token, args.port, args.idle_seconds)
    server.start()
    if args.watch_stdin:
        threading.Thread(target=_exit_when_stdin_closes, args=(server,), daemon=True).start()
    # Dòng READY là kênh duy nhất trên stdout; mọi log khác đi stderr.
    print(f"AIVS_WORKER_READY port={port}", flush=True)
    server.wait_for_termination()
    return 0


if __name__ == "__main__":
    sys.exit(main())
