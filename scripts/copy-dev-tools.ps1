# Chép công cụ dev (ffmpeg, ffprobe, native_tts) từ một thư mục nguồn sang tools\ (chỉ đọc nguồn). Chạy lại an toàn.
param([Parameter(Mandatory)][string]$Source)

$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\tools'
New-Item -ItemType Directory -Force $target | Out-Null

foreach ($name in 'ffmpeg.exe', 'ffprobe.exe') {
    $src = Join-Path $Source $name
    if (Test-Path $src) { Copy-Item $src $target -Force } else { Write-Warning "Thiếu $src" }
}

$native = Join-Path $Source 'native_tts'
if (Test-Path $native) {
    Copy-Item $native (Join-Path $target 'native_tts') -Recurse -Force
} else {
    Write-Warning "Thiếu $native"
}
