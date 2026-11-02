# Plan: Live game tương tác TikTok — "Chùa Online · Thắp Hương Cầu An" (màn S24)

## 1. Mục tiêu
Một màn mới trong SuperMOOO cho phép phát live TikTok dạng **game tương tác 24/7**: người xem vào phòng, bình luận, thả tim, tặng quà thì trên màn hình có nhân vật mang avatar + tên của họ đi vào chính điện, lạy Phật, dâng hương, thả hoa sen; giọng đọc AI cảm ơn và đọc lời cầu an. Không cần người ngồi trước camera.

## 2. Chọn chủ đề: Chùa Online · Thắp Hương Cầu An
Lý do chọn (so với đua xe/thả cá/nuôi thú):
- **Tệp người xem rộng nhất ở VN**: mọi lứa tuổi, đặc biệt 30–60 tuổi (nhóm tặng quà mạnh, ít bị game khác cạnh tranh). Ảnh mẫu cho thấy ~2,9K người xem cùng lúc.
- **Tham gia không tốn sức**: chỉ cần gõ "Nam mô A Di Đà Phật" là có nhân vật của mình quỳ lạy → tỷ lệ bình luận rất cao → thuật toán TikTok đẩy live.
- **Có lịch cao điểm cố định**: mùng 1, rằm, Vu Lan, Phật Đản, Tết, Thượng/Hạ nguyên → dễ tổ chức sự kiện.
- **Tận dụng thế mạnh sẵn có của app**: giọng VieNeu đọc tiếng Việt nhanh (RTF ~0,24), LLM local viết lời cầu an, tạo ảnh AI cho nền.

Rủi ro & cách giảm:
- Chính sách TikTok về tôn giáo + gạ quà: **không hứa "tặng quà được phù hộ"**, dùng từ "tùy hỷ"; mọi nghi thức chính (vào chùa, lạy, cầu an) đều miễn phí bằng bình luận/tim, quà chỉ thêm hiệu ứng.
- Nội dung "lặp, không nguyên bản": cảnh thay đổi theo giờ (sáng/chiều/tối), sự kiện theo ngày âm lịch, giọng đọc tên người xem thật.
- Engine thiết kế theo **gói chủ đề** (theme pack) để sau này thêm chủ đề khác (đua thuyền rồng, thả đèn hoa đăng) mà không sửa code lõi.

## 3. Chọn công nghệ (dựa trên src hiện tại)

| Thành phần | Đề xuất "người ta bảo" | Chọn cho SuperMOOO | Lý do |
|---|---|---|---|
| App điều khiển | WinForms | **WPF .NET 10 hiện có, thêm màn S24** | Đã có DI, settings, TTS, LLM, log, toast, Help F1 |
| Server overlay | Node live-server | **`HttpListener` + WebSocket có sẵn trong .NET** (theo mẫu `src/SuperMOOO.Workflows/Live/BroadcastServer.cs`) | Không cần Node, không thêm package; S20 đã chạy ổn mô hình này |
| Nhận sự kiện TikTok | TikFinity | **TikFinity WebSocket (`ws://localhost:21213`) qua `ClientWebSocket`** | TikFinity lo phần ký/kết nối TikTok (phần hay vỡ nhất), mình chỉ đọc JSON. Thiết kế `ILiveEventSource` để sau thêm nguồn khác |
| Vẽ cảnh | Canvas/Three.js | **PixiJS v8 (WebGL 2D), nhúng file `pixi.min.js` vào resource** | Cảnh là 2.5D nhiều lớp + hàng trăm sprite + hạt; Pixi nhanh hơn Canvas2D nhiều lần, nhẹ hơn Three.js, chạy tốt trên iGPU UHD 730. Không cần npm/bundler |
| Cửa sổ phát | Trình duyệt | **Microsoft Edge chế độ app** (`msedge --app=… --window-size=1080,1920`), app tự mở | Có sẵn trên Windows 11, cửa sổ không viền để LIVE Studio bắt. Không cần thêm WebView2 |
| Phát lên TikTok | TikTok LIVE Studio | **TikTok LIVE Studio (Window capture)**; OBS + Browser Source nếu tài khoản có stream key | Giữ nguyên |
| Camera ảo | ManyCam | **Bỏ** | LIVE Studio/OBS bắt cửa sổ trực tiếp; nếu cần camera ảo thì OBS có Virtual Camera miễn phí |
| Giọng đọc | – | **`TtsService` (VieNeu) có sẵn** | Đọc cảm ơn/cầu an tiếng Việt local, không tốn phí |
| Lời cầu an | – | **LLM local đóng gói (Qwen3 GGUF, như S20)** + mẫu câu dựng sẵn | Câu ngắn, có cache; LLM chậm/lỗi thì dùng mẫu |
| Ảnh nền | – | **`IImageProvider` có sẵn** + ffmpeg tạo video lặp | Tạo nền theo giờ/sự kiện |
| Lưu dữ liệu | – | **SQLite + Dapper có sẵn** | Sổ công đức, thống kê phiên |

**Phụ thuộc mới cần chủ dự án duyệt:** chỉ 1 file `pixi.min.js` (PixiJS v8, MIT) nhúng làm resource. Ngoài app cần cài: TikFinity (miễn phí, tính năng WebSocket có ở bản free) và TikTok LIVE Studio.

## 4. Kiến trúc

```
TikTok LIVE ──► TikFinity (PC) ──ws:21213──► TikFinityEventSource (C#)
                                                   │ LiveEvent (chuẩn hoá)
                                                   ▼
                                  LiveGameEngine (C#: luật, hàng đợi, xếp hạng)
                         ┌─────────────────┼──────────────────┐
                         ▼                 ▼                  ▼
                  TTS/LLM queue     SQLite (sổ công đức)   LiveGameServer :18121
                  (VieNeu, Qwen3)                           │ HTTP + WebSocket /ws
                         │ audio url                        ▼
                         └──────────────────────► Trang overlay PixiJS (Edge app 1080×1920)
                                                            │ Window capture
                                                            ▼
                                                  TikTok LIVE Studio / OBS ──► TikTok
```

Nguyên tắc: **toàn bộ luật game ở C#**, trang web chỉ vẽ theo lệnh nhận qua WebSocket → dễ đổi chủ đề, dễ khôi phục khi trình duyệt tải lại (server gửi lại snapshot trạng thái).

## 5. Luồng game (chủ đề Chùa)

| Hành động người xem | Hiệu ứng trên màn | Giọng đọc |
|---|---|---|
| Vào phòng (member) | Nhân vật áo lam mang avatar + tên bước vào cổng, đi lên chính điện, quỳ ở hàng trống | Gộp: "Chào mừng A, B và 5 bạn vào chùa" (tối đa 1 câu/20 s) |
| Bình luận bất kỳ | Bong bóng chữ trên đầu nhân vật (lọc từ cấm, link, số điện thoại) | – |
| Bình luận có "nam mô", "a di đà", "cầu…" | Nhân vật vái lạy, hào quang nhẹ; tăng bộ đếm "Niệm Phật hôm nay" | Câu cầu an ngắn (LLM/mẫu) cho lời "cầu …", tối đa N câu/phút |
| Thả tim | Hoa sen nhỏ bay lên từ nhân vật; tim tích luỹ làm sáng dần đèn lưu ly | – |
| Follow / Share | Nhân vật đổi áo vàng (Phật tử thân), pháo sáng nhỏ | "Cảm ơn A đã theo dõi" (gộp) |
| Quà 1 xu | Dâng hương: nhân vật cắm nhang vào lư hương, khói bốc | "A tùy hỷ dâng hương" |
| Quà 5 xu | Lễ Phật: lạy 3 lạy, chuông ngân | Có tên |
| Quà 10–99 xu | Hoa sen lớn rơi khắp chính điện | Có tên + câu cầu an |
| Quà ≥100 xu | Sự kiện lớn: tượng Phật toả hào quang, mưa hoa sen vàng 10 s, tên hiện giữa màn | Câu cầu an riêng |
| Combo quà (x2, x3…) | Chỉ xử lý khi `repeatEnd`, hiệu ứng nhân theo số lượng (giới hạn) | 1 câu |

HUD cố định:
- Dưới cùng: bảng "nghi thức" theo mức xu (Dâng hương · Lễ Phật · Hoa sen · Đại lễ).
- Góc phải: "Tấm lòng thành" top 3 phiên; nút xem "Sổ công đức" tuần.
- Trên: dải chữ chạy (ngày âm lịch, sự kiện hôm nay), bộ đếm "Đã thắp X nén hương".
- Cảnh đổi theo giờ thật: sáng (nắng xiên), chiều, tối (đèn lồng, nến); mùng 1/rằm có trang trí riêng.

Giới hạn hiệu năng: tối đa ~300 nhân vật hiển thị đầy đủ; vượt quá thì người cũ thu nhỏ thành chấm đầu (như ảnh mẫu) rồi rời đi sau 10 phút không tương tác. Hàng đợi hiệu ứng có trần để không nghẽn khi bão quà.

## 6. Các giai đoạn triển khai

### GĐ0 — Chuẩn bị (chủ dự án)
- Duyệt dùng `pixi.min.js` (PixiJS v8).
- Cài TikFinity, bật WebSocket; cài TikTok LIVE Studio.
- Bật TikFinity khi đang live thử để ghi log JSON thô (xác minh tên trường thực tế trước GĐ1).

### GĐ1 — Nhận sự kiện (`SuperMOOO.Workflows/LiveGame/`)
- `Core/Models/LiveGame.cs`: `LiveEvent` (Kind: Chat/Gift/Like/Follow/Share/Member/ViewerCount; UserId, Nickname, AvatarUrl, Text, GiftId, GiftName, Coins, RepeatCount, RepeatEnd, LikeCount, At), `LiveGameSettings` (section `livegame`: Port 18121, TikFinityUrl, ThemeId, VoiceId, giới hạn câu/phút, danh sách từ cấm, bảng mức quà).
- `ILiveEventSource` + `TikFinityEventSource`: `ClientWebSocket`, tự kết nối lại (backoff 1→30 s), parse JSON → `LiveEvent`, bỏ trùng theo msgId, ghi log thô khi bật chế độ chẩn đoán.
- `SimulatedEventSource`: bơm sự kiện giả (nút trong S24 + chế độ "bão quà") để dựng/kiểm tra cảnh khi chưa live.

### GĐ2 — Engine luật (`LiveGameEngine`)
- Trạng thái: danh sách người xem (vị trí hàng quỳ, trạng thái áo, lần tương tác cuối), bộ đếm, xếp hạng phiên.
- Luật đọc từ theme pack (`theme.json`: mức quà → hành động, từ khoá → hành động, câu mẫu giọng đọc).
- Gộp sự kiện (debounce chào mừng/cảm ơn), rate-limit theo người và toàn cục, trần hàng đợi hiệu ứng.
- Lọc nội dung: từ cấm, link, số điện thoại, bình luận quá dài.
- Lưu SQLite: `livegame_session`, `livegame_donor` (sổ công đức theo ngày/tuần), migration theo cách các bảng `live_*` đang làm.

### GĐ3 — Server overlay (`LiveGameServer`)
- Theo mẫu `BroadcastServer`: chỉ nghe 127.0.0.1, chặn host lạ, header bảo mật.
- Route: `/` (trang game), `/static/*` (js, theme assets nhúng resource hoặc từ thư mục theme), `/ws` (WebSocket đẩy lệnh vẽ + snapshot khi kết nối), `/media/*` (audio TTS, video nền), `/avatar?u=` (proxy + cache ảnh đại diện, chỉ cho phép domain CDN TikTok, giới hạn kích thước).
- Cổng 18121 (tránh 18089–18091 TTS, 18120 S20, 8765 OAuth).

### GĐ4 — Trang overlay PixiJS (`LiveGame/Web/`)
- Lớp vẽ: video nền lặp → bàn thờ/lư hương (khói bằng particle) → nhân vật (sprite sheet: đi, quỳ, lạy, cắm nhang; avatar tròn trên đầu) → bong bóng chữ → hiệu ứng (hoa sen rơi, hào quang, pháo sáng) → HUD.
- Khung 1080×1920 cố định, scale theo cửa sổ; 30 fps mặc định (60 fps tuỳ chọn).
- Phát audio TTS theo hàng đợi trong trang (LIVE Studio thu âm hệ thống).
- Tự kết nối lại WebSocket, nhận snapshot để vẽ lại khi reload.

### GĐ5 — Giọng đọc & lời cầu an
- Hàng đợi TTS riêng (1 câu đang đọc + tối đa vài câu chờ, câu cũ quá 30 s thì bỏ).
- Dùng `TtsService` với giọng chọn trong S24 (mặc định VieNeu giọng ấm, chậm).
- Lời cầu an: LLM local (tái dùng cấu hình `LocalProvider` như S20), prompt ngắn có ràng buộc (≤ 25 từ, không hứa hẹn, không nhắc tiền/quà); timeout 4 s → dùng câu mẫu theo từ khoá (sức khoẻ, bình an, gia đạo, công việc, thi cử).
- Tên người xem: chuẩn hoá (bỏ emoji/ký tự lạ, cắt 20 ký tự) trước khi đọc.

### GĐ6 — Màn S24 "Live tương tác" (`Features/S24/`)
- Đăng ký trong `Shell/ScreenRegistry.cs` (nhóm Release, cạnh S20).
- Khu kết nối: trạng thái TikFinity (đã kết nối / mất kết nối), số người xem, nút Bắt đầu/Dừng.
- Khu phát: nút "Mở cửa sổ phát" (Edge app 1080×1920), copy URL cho OBS Browser Source, hướng dẫn LIVE Studio.
- Khu cấu hình: chủ đề, giọng đọc, âm lượng, bảng mức quà, từ cấm, giới hạn câu/phút, bật/tắt cầu an AI.
- Khu giám sát: log sự kiện trực tiếp, top người tặng, nút giả lập sự kiện (GĐ1).
- Toast khi kết nối/mất kết nối; lỗi qua `IDialogService.Error`; dropdown có ô tìm kiếm.
- `Help/S24.txt` đúng cấu trúc (tên màn, Dùng để, Các bước, Mẹo, Lưu ý / lỗi thường gặp).

### GĐ7 — Nội dung chủ đề Chùa (theme pack `themes/temple/`)
- Nền: 3 ảnh chính điện (sáng/chiều/tối) tạo bằng `IImageProvider` dọc 1080×1920, ffmpeg thêm chuyển động nhẹ (zoom/ánh sáng) thành video lặp 20–30 s.
- Sprite nhân vật áo lam/áo vàng: 4 hoạt cảnh (đi, quỳ, lạy, cắm nhang), xuất sprite sheet PNG + JSON atlas.
- Hiệu ứng: hoa sen (3 cỡ), nén hương, khói, hào quang, đèn lưu ly.
- Âm thanh: chuông, mõ, tiếng tụng kinh nền nhỏ (tự thu hoặc nguồn có giấy phép rõ ràng).
- `theme.json`: mức quà, từ khoá, câu mẫu, vị trí hàng quỳ, toạ độ lư hương/tượng.

### GĐ8 — Hiệu năng & vận hành 24/7
- TTS/LLM xin tài nguyên qua `IRuntimeResources`; tiến trình Edge đăng ký `TrackProcess` để điều tiết.
- Giới hạn bộ nhớ trang: pool sprite, giải phóng texture avatar khi nhân vật rời đi, cache avatar trên đĩa có hạn mức.
- Tự khởi động lại trang nếu WebSocket im lặng quá 60 s; ghi log Serilog theo phiên.
- Kiểm tra: build từng project; chạy chế độ giả lập "bão quà" 30 phút xem fps/RAM (không viết test case).

## 7. Hướng dẫn phát (đưa vào `Help/S24.txt`)
1. Mở TikFinity, đăng nhập đúng tài khoản TikTok, bật WebSocket.
2. S24 → Bắt đầu → Mở cửa sổ phát.
3. TikTok LIVE Studio → thêm nguồn Window capture → chọn cửa sổ Edge "Chùa Online"; khung dọc; bật âm thanh hệ thống.
4. Đặt tiêu đề live, chủ đề phù hợp, bấm Phát trực tiếp.
5. (Có stream key) Dùng OBS: Browser Source với URL copy từ S24, 1080×1920.

## 8. Thứ tự làm & ước lượng
| GĐ | Nội dung | Phụ thuộc |
|---|---|---|
| 0 | Duyệt PixiJS, cài TikFinity, lấy log JSON thô | – |
| 1–3 | Sự kiện, engine, server | GĐ0 |
| 4 | Overlay PixiJS (cảnh tạm bằng hình đơn giản) | GĐ3 |
| 6 | Màn S24 + Help | GĐ1–3 |
| 5 | TTS + cầu an | GĐ2 |
| 7 | Nội dung theme Chùa hoàn chỉnh | GĐ4 |
| 8 | Hiệu năng, chạy 24/7 | Tất cả |

## 9. Ngoài phạm vi (làm sau nếu cần)
- Nguồn sự kiện tự kết nối TikTok không qua TikFinity (cần Node `tiktok-live-connector` + khoá ký Euler Stream → thêm phụ thuộc, dễ vỡ).
- Chủ đề thứ hai (Đua thuyền rồng / Thả đèn hoa đăng) dùng lại engine.
- Phát đa nền tảng (Facebook/YouTube) cùng lúc.
