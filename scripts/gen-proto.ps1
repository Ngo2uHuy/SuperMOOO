# Sinh aivs_worker_pb2*.py từ proto\aivs_worker.proto; cần grpcio-tools trong Python được dùng.
param([string]$Python = "python")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "ai_worker\aivs_worker\gen"
& $Python -m grpc_tools.protoc "-I$root\proto" "--python_out=$out" "--grpc_python_out=$out" "$root\proto\aivs_worker.proto"
if ($LASTEXITCODE -ne 0) { throw "protoc lỗi ($LASTEXITCODE)" }
# protoc sinh import tuyệt đối; trong package phải là import tương đối.
$grpcFile = Join-Path $out "aivs_worker_pb2_grpc.py"
(Get-Content $grpcFile -Raw) -replace '(?m)^import aivs_worker_pb2 as', 'from . import aivs_worker_pb2 as' |
    Set-Content $grpcFile -NoNewline -Encoding utf8
New-Item -ItemType File -Force (Join-Path $out "__init__.py") | Out-Null
