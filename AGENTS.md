# SuperMOOO — quy tắc cho AI/agent làm việc trong repo

Đọc hết file này trước khi sửa code. Các quy tắc dưới đây do chủ dự án đặt ra, ưu tiên hơn thói quen mặc định của bạn.

## Quy tắc bắt buộc
- **Không viết test case.** Không tạo file/project test, không thêm `[Fact]`/`[Theory]`, không sửa test khi đổi code. Kiểm tra bằng cách build project bị ảnh hưởng. Test cũ fail vì công thức đổi thì báo chủ dự án, đừng tự sửa.
- **Không thêm dependency** (NuGet, npm, Python) khi chưa hỏi.
- **Hỏi trước khi làm việc khó đảo ngược:** xoá file/thư mục, xoá model, migration trên DB thật, gửi dữ liệu ra ngoài. Repo không dùng git, xoá là mất.
- **Không tự tạo tài liệu** (README, *.md, báo cáo) nếu không được yêu cầu.
- **Không nhập/dùng API key** người dùng dán trong chat. Key nhập qua Cài đặt của app (DPAPI).
- **Không kill tiến trình mình không tự khởi động.** Không đụng cổng 18089 / `server.json` của server TTS mà app đang chạy.
- Trả lời người dùng bằng tiếng Việt, ngắn gọn. Chuỗi UI tiếng Việt.

## Cách viết code
- Viết giống code xung quanh (naming, idiom, mật độ comment). Tìm helper có sẵn trước khi viết mới.
- Thay đổi tối thiểu đúng phạm vi. Không refactor, đổi format hay đổi tên ngoài yêu cầu.
- Comment chỉ giải thích "tại sao".
- Stack: .NET 10 WPF, WPF-UI 4.3 (FluentWindow, Snackbar, ContentDialog), CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), DI qua attribute `[Service]`, Serilog, LibVLCSharp.

## Build
App thường đang chạy và khoá `bin`, nên build ra thư mục tạm rồi xoá bằng đường dẫn cụ thể:
```
dotnet build SuperMOOO.slnx --artifacts-path C:/Users/Admin/AppData/Local/Temp/aivs_build -v q 2>&1 | grep -iE "error|warning\(s\)|succeeded"
rm -rf C:/Users/Admin/AppData/Local/Temp/aivs_build
```
Đổi `SuperMOOO.slnx` thành project cụ thể khi chỉ sửa một project. Yêu cầu: 0 lỗi, không thêm cảnh báo mới.

## Cấu trúc
- `src/SuperMOOO.Core`: model, abstraction, `AppPaths`, `PerformancePlanner`.
- `src/SuperMOOO.Infrastructure`: job queue, process runner, logging, `RuntimeResources` (đo CPU/RAM, điều tiết).
- `src/SuperMOOO.AI`: TTS (`Tts/`), ASR (`Asr/`), dịch (`Translation/`), LLM (`Llm/`), AI worker Python (`Worker/`).
- `src/SuperMOOO.Media`: ffmpeg, render, phụ đề ASS.
- `src/SuperMOOO.Workflows`: pipeline step (Localize, TTS Studio, Timeline…).
- `src/SuperMOOO.App`: WPF. Mỗi màn ở `Features/Sxx/` (S01–S17, danh sách trong `Shell/ScreenRegistry.cs`).
- `tools/`: ffmpeg, yt-dlp, `native_tts` (audio.cpp cũ: VieNeu, OmniVoice dự phòng CPU), `audiocpp_v0.9.2_cpu` (Supertonic, Qwen3-TTS, CosyVoice3, Irodori, Chatterbox, Breeze), `audiocpp_v0.9.2_vulkan` (OmniVoice trên iGPU), `llama_cpp` (Hy-MT2).

## Quy ước khi thêm/sửa màn
- Mỗi màn có hướng dẫn `src/SuperMOOO.App/Help/Sxx.txt` (nút F1). Thêm màn hoặc đổi tên nút thì cập nhật file này, giữ cấu trúc: dòng 1 là tên màn, rồi "Dùng để:", "Các bước:" (đánh số), "Mẹo:" (gạch đầu dòng), có thể thêm "Lưu ý / lỗi thường gặp:".
- Tác vụ chạy hiển thị ngay trên màn đó (`ScreenJobsPanel`), không bắt người dùng sang màn Tác vụ.
- Hành động lớn hiện toast. Lỗi hiện qua `IDialogService.Error` (tự ghi log).
- Dropdown có ô tìm kiếm. Nhãn hiển thị dùng `Labels.Term` / converter `Label` cho khoá kỹ thuật.

## Model AI local
Model nằm ở `%LOCALAPPDATA%\SuperMOOO.Data\models` (đổi được qua `Paths:DataRoot` trong `appsettings.json`). Đã benchmark trên i5-11400 (CPU) và chỉ giữ:

| Nhiệm vụ | Model | Runtime / thiết bị | Ghi chú |
|---|---|---|---|
| Giọng tiếng Việt | VieNeu v3 turbo | `native_tts` (cũ), CPU, cổng native (18089) | RTF ~0,24, chỉ đọc vi (server từ chối "english") |
| Giọng ko (+en/zh dự phòng) | Qwen3-TTS 12Hz 1.7B CustomVoice q8_0 | `audiocpp_v0.9.2_cpu`, CPU, cổng native+1 | RTF ~1,9, CER 0 ko/en/zh; 9 giọng dựng sẵn (`voice_id`), không cần mẫu; RAM ~6 GB; Vulkan chậm hơn (3,75) |
| Giọng en | Chatterbox q8_0 | `audiocpp_v0.9.2_cpu`, CPU, cổng native+1 | RTF ~4,8, CER 0, giống giọng neo rất sát (cos 0,98–0,99); bản GGUF không có giọng dựng sẵn → bắt buộc giọng mẫu (neo OmniVoice/clone); RAM ~2,6 GB; Vulkan chậm hơn (5,8). Chatterbox Turbo (giọng dựng sẵn) RTF 3,9 nhưng không clone → không dùng
| Giọng zh-TW | Breeze TTS 2 q8_0 | `audiocpp_v0.9.2_cpu`, CPU, cổng native+1 | RTF ~4,8, CER 0 (CosyVoice3 cùng câu phồn thể: RTF 6,0, CER 1,4%); bắt buộc giọng mẫu + transcript; RAM ~9,5 GB; Vulkan chậm hơn (7,7)
| Giọng zh | CosyVoice3 q8_0 | `audiocpp_v0.9.2_cpu`, CPU, cổng native+1 | RTF ~5,1, CER 0; bắt buộc giọng mẫu (giọng neo OmniVoice hoặc mẫu clone) |
| Giọng ja | Irodori-TTS v4 small q8_0 | `audiocpp_v0.9.2_cpu`, CPU, cổng native+1 | RTF ~6,5 (20 bước); bắt buộc giọng mẫu; Vulkan hỏng → chỉ CPU |
| Giọng ja/ko/en nhanh | Supertonic-3 | `audiocpp_v0.9.2_cpu`, CPU, cổng native+1 | RTF ~0,25–0,3; không có zh, không clone |
| Giọng ngôn ngữ khác, clone, tạo giọng neo | OmniVoice | `audiocpp_v0.9.2_vulkan`, iGPU, cổng native+2 | RTF ~11–16 (nhanh hơn CPU ~1,6 lần); thiếu runtime Vulkan hoặc Vulkan lỗi thì về runtime cũ trên CPU |
| Nhận diện giọng nói | Whisper turbo (faster-whisper) | worker Python | WER 1,9%, RTF 0,45 |
| Tách giọng gốc, mặc định "Chất lượng cao" (S01, S06 "Tách giọng gốc") | Mel-Band RoFormer vocals (Kim, MIT) `.ckpt` | worker Python + PyTorch 2.14.1 CPU (port riêng `services/roformer.py`, không einops/librosa), CPU | RTF ~4,1–4,9 (đoạn 8 s, chồng 50 %), RAM đỉnh ~2,1 GB (mmap checkpoint, không tăng theo độ dài); mix tổng hợp: SDR nền 14,6 dB (Voc_FT 12,5), sai lệch nhạc vùng không lời -54 dB (Voc_FT -42) |
| Tách giọng gốc "Nhanh" + dự phòng khi thiếu torch/RoFormer | UVR-MDX-NET-Voc_FT (MDX-Net) ONNX | worker Python (onnxruntime + numpy), CPU | RTF ~0,62, RAM đỉnh ~1,3 GB, SDR nền 12,5 dB; Whisper không còn nhận ra lời gốc trên nền; Kim_Vocal_2 / Inst_HQ_3/4/5 ngang nhau nhưng giữ nhạc kém hơn hoặc rò lời hơn; overlap 0,5 + denoise chậm ~2,9 lần, không hơn |
| Dịch local | Hy-MT2 1.8B Q4_K_M qua llama-server | ~0,6–0,9 s/câu, gửi kèm 3 câu trước làm ngữ cảnh |

Đã loại (đừng tải lại nếu không được yêu cầu): VoxCPM2, Kokoro, Piper, F5-TTS (cả bản tiếng Việt), Qwen3-ASR, CrisperWhisper, UVR Kim_Vocal_2 / Inst_HQ_3 / Inst_HQ_4 / Inst_HQ_5 (tách giọng).

Ưu tiên model theo ngôn ngữ (`TtsLanguageRouter`, lấy model đầu tiên đã cài; S16 → TTS local đổi được ưu tiên 1, lưu ở `tts.native.LanguagePriority`). Giọng VieNeu (mặc định) đọc ngôn ngữ khác thì chuyển theo bảng, giữ giới tính; giọng người dùng chọn rõ của model khác được giữ, chỉ chuyển khi model đó không đọc được ngôn ngữ. Model lỗi lúc chạy thì báo lỗi, không đổi model giữa job (tránh video lẫn giọng):

| Ngôn ngữ | 1 | 2 | 3 |
|---|---|---|---|
| vi | VieNeu | OmniVoice | |
| ja | Irodori | OmniVoice | Supertonic |
| ko | Qwen3-TTS | OmniVoice | Supertonic |
| zh-TW | Breeze TTS 2 | CosyVoice3 | Qwen3-TTS → OmniVoice |
| zh / yue | CosyVoice3 | Qwen3-TTS | OmniVoice |
| en | Chatterbox | Qwen3-TTS | OmniVoice → Supertonic |
| khác | OmniVoice | Edge (nếu bật) | |

Lưu ý kỹ thuật:
- Bản audio.cpp v0.9.2 **portable** build không SIMD, chậm ~5 lần. Dùng bản `cpu` (AVX) trong `tools/audiocpp_v0.9.2_cpu`.
- Bản `audiocpp_v0.9.2_vulkan` trên iGPU Intel (UHD 730) **bắt buộc** biến môi trường `GGML_VK_DISABLE_F16=1` cho tiến trình server, thiếu thì audio hỏng.
- OmniVoice trên CPU đọc theo `voice_ref` (đường giọng neo/clone) sinh **tiếng bíp ~0,4 s đầu câu**, tiếng nói nhỏ và kém giống giọng; trên Vulkan không bíp (cosine giọng 0,95–0,98). Vì vậy OmniVoice chạy server Vulkan.
- Bảng định tuyến theo locale trước (zh-TW có dòng riêng), rồi mới theo mã đọc (zh-CN → zh).
- Giọng dựng sẵn của CosyVoice3/Irodori/Chatterbox/Breeze đọc qua giọng neo do OmniVoice tạo (dùng chung file neo với giọng OmniVoice tương ứng); clone OmniVoice cũng dùng được cho hai model này.
- Clone mẫu tiếng Việt (chọn vi, hoặc tự nhận diện mà lời mẫu có chữ Việt) thêm `<id>.vieneu_codes.txt` + `<id>.vieneu_emb.txt` cạnh audio mẫu: embedding 192 chiều do AI worker tính (RPC `AudioTools.SpeakerEmbedding`, `models\speaker\speaker_encoder.onnx` của VieNeu-TTS-v3-Turbo, fbank Kaldi qua `kaldi-native-fbank` + `soxr`, không torch), codes do `audiocpp_cli --request-option encode_reference_only=true` mã hoá từ 8 s đầu. Câu vi của clone này đọc bằng VieNeu (`cpp_vieneu_clone_<guid>`, RTF ~0,3–0,4, CER ≤ 4,5%), ngôn ngữ khác vẫn OmniVoice từ cùng mẫu. audio.cpp không tự tính embedding: clone VieNeu chỉ từ `voice_ref` đọc sai lời (CER ~0,9). Không khử nhiễu mẫu trước khi tính (đo kém giống giọng hơn). Worker/model thiếu → chỉ clone OmniVoice, ghi cảnh báo.
- Giọng dựng sẵn "Quốc tế" (`cpp_omni_international_*`) đọc tiếng Việt: OmniVoice tạo giọng neo một lần như cũ, `VieNeuEnroller` tính codes + embedding từ neo (lưu cạnh file neo), VieNeu đọc mọi câu (`cpp_vieneu_international_*`, ẩn khỏi danh sách chọn; đo: CER 0, spk-cos với neo 0,82–0,84, F0 162–172/170). `TtsService.RouteAsync` quyết định lúc warm đầu job; thiếu VieNeu/worker/model speaker thì cả job đọc OmniVoice (cảnh báo một lần), lỗi lúc tính embedding thì báo lỗi, không đổi model giữa job.
- Với v0.9.2, session option phải có tiền tố tên model (vd. `vieneu_v3_turbo.g2p_library`); viết thiếu thì server lặng lẽ bỏ qua.
- Giọng Windows (SAPI) đã bị xoá khỏi app.
- Tách giọng: bước `separate_vocals` (Localize, Reup) cache nền ở `cache\separation\<sha256 nguồn>_<tên model>.wav`. RoFormer thiếu torch/model → MDX + cảnh báo; thiếu cả hai/worker lỗi → tiếng gốc đầy đủ + cảnh báo. Nền đã tách thì ducking chỉ -5 dB (`AudioMixer.LightDuckDb`), bình thường -11 dB cố định (khoá giọng gate + khuếch đại + cắt cứng nên không phụ thuộc âm lượng giọng).
- Edge TTS là endpoint không chính thức (`readaloud/edge/v1`): cần token Sec-MS-GEC v2 (tự viết, không chép code GPL của edge-tts), User-Agent Edge, cookie muid; chỉ còn trả MP3/Opus nên phải chuyển sang WAV qua `WavPreparer`. Microsoft đổi phiên bản thì sửa `TtsEdgeSettings.SecMsGecVersion` trước khi sửa code.

## Hiệu năng
- Cấu hình theo máy ở `PerformancePlanner` (Core) + `IRuntimeResources` (Infrastructure). Luồng AI = số nhân vật lý.
- Mọi lệnh ffmpeg xin lease qua `IRuntimeResources.AcquireMediaAsync`. Lệnh ngắn tương tác (lấy 1 khung hình) phải đặt `FfmpegJob.Light = true` để không chờ sau job render dài.
- Chế độ tự động có điều tiết theo CPU/RAM (giảm job song song, giảm luồng ffmpeg, hạ ưu tiên tiến trình con, tắt server AI rảnh khi thiếu RAM). Tiến trình nặng mới phải được đăng ký qua `TrackProcess` / `ProcessSpec.Heavy`.
