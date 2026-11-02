# Đóng gói bản cài (Velopack): publish self-contained win-x64, kèm tools\ và ai_worker\ (mã nguồn).
# Python và model tự cài ở lần mở đầu. Cần `dotnet tool restore` (vpk khớp bản gói Velopack đã pin).
param(
    [string]$Version = '1.0.0',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\release'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root ('artifacts\publish-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $root 'src\SuperMOOO.App\SuperMOOO.App.csproj'

Push-Location $root
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore thất bại.' }
    $tools = Join-Path $root 'tools'
    foreach ($required in 'ffmpeg.exe', 'ffprobe.exe', 'yt-dlp.exe', 'native_tts\audiocpp_server.exe',
            'native_tts\audiocpp_cli.exe', 'native_tts\sea_g2p_rs.dll', 'native_tts\sea_g2p.bin',
            'audiocpp_v0.9.2_cpu\audiocpp_server.exe', 'audiocpp_v0.9.2_vulkan\audiocpp_server.exe', 'llama_cpp\llama-server.exe') {
        if (-not (Test-Path (Join-Path $tools $required))) { throw "Thiếu công cụ bắt buộc: tools\$required" }
    }
    dotnet publish $project -c $Configuration -r win-x64 --self-contained true --artifacts-path (Join-Path $root 'artifacts\build') -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish thất bại.' }

    # tools\: ffmpeg, ffprobe, yt-dlp (nếu có), TTS native (exe + dll, không kèm model .gguf).
    $packedTools = Join-Path $publish 'tools'
    New-Item -ItemType Directory -Force $packedTools | Out-Null
    foreach ($name in 'ffmpeg.exe', 'ffprobe.exe', 'yt-dlp.exe') {
        $src = Join-Path $tools $name
        if (Test-Path $src) { Copy-Item $src $packedTools -Force } else { Write-Warning "Không có tools\$name nên bản cài không kèm công cụ này." }
    }
    $native = Join-Path $tools 'native_tts'
    if (Test-Path $native) {
        robocopy $native (Join-Path $packedTools 'native_tts') /E /XF *.gguf | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'Chép native_tts thất bại.' }
    }

    # Runtime audio.cpp v0.9.2: bản AVX cho Supertonic/Qwen3-TTS/CosyVoice3/Irodori/Chatterbox/Breeze, bản Vulkan cho OmniVoice trên iGPU; model .gguf tải riêng.
    foreach ($name in 'audiocpp_v0.9.2_cpu', 'audiocpp_v0.9.2_vulkan') {
        $runtime = Join-Path $tools $name
        if (Test-Path $runtime) {
            robocopy $runtime (Join-Path $packedTools $name) /E /XF *.gguf *.zip | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "Chép $name thất bại." }
        }
    }

    $llama = Join-Path $tools 'llama_cpp'
    if (Test-Path $llama) {
        robocopy $llama (Join-Path $packedTools 'llama_cpp') /E /XF *.gguf *.zip | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'Chép llama_cpp thất bại.' }
    }

    $worker = Join-Path $root 'ai_worker'
    if (Test-Path $worker) {
        robocopy $worker (Join-Path $publish 'ai_worker') /E /XD .venv __pycache__ models tests | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'Chép ai_worker thất bại.' }
    }
    if (-not (Test-Path (Join-Path $publish 'ai_worker\requirements.lock'))) { throw 'Thiếu AI worker trong bộ cài.' }
    '{"automaticSetup":true}' | Set-Content -LiteralPath (Join-Path $publish 'setup-required.json') -Encoding UTF8

    dotnet tool run vpk -- pack --packId SuperMOOO --packVersion $Version --packDir $publish --runtime win-x64 --mainExe SuperMOOO.App.exe --packTitle 'SuperMOOO' --outputDir $Output
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack thất bại.' }
    Write-Host "Setup: $(Join-Path (Resolve-Path $Output) 'SuperMOOO-win-Setup.exe')"
}
finally {
    Pop-Location
}
