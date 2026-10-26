# CLAUDE.md

## Quy tắc vàng (BẮT BUỘC) — kế thừa từ DocScanner/PdfReader

- **Trong lúc owner đi ngủ, Claude tự quyết mọi việc, KHÔNG hỏi owner bất cứ điều gì** — kể cả thêm/xoá/đổi tên
  file, chọn thư viện, chọn thuật toán, đổi kiến trúc. Gặp chỗ phải chọn: tự chọn phương án tốt nhất, ghi lý do
  vào commit/CLAUDE.md, làm tiếp.
- **Owner kiểm tra kết quả vào buổi sáng.** Trước đó mọi thứ phải: build được, có CLAUDE.md cập nhật, có tóm tắt
  rõ ràng những gì đã làm/chưa làm/cần owner test tay.
- App dùng để bán thương mại: mọi thư viện phải có license cho phép dùng thương mại (MIT/BSD/Apache-2.0...).
- Không tự ý commit code.

## Bối cảnh dự án

Ý tưởng: app tách logo in trên quần áo (ảnh có áo/người mẫu) thành PNG nền trong suốt + vector, chạy **100% trên
máy, không server** (quyết định của owner 2026-10-26, sau khi test depth-estimation AI cho thấy không khả thi/
không cần thiết cho v1 — xem lịch sử bên dưới). Package `btk.logoextract`, tên hiển thị "Logo Extract".

Thư mục `LogoExtract-prototype/` (nay nằm trong repo này, đã thêm vào git 2026-10-27) là nơi prototype Python/ONNX
ban đầu, `samples/` có 4 ảnh áo thật owner cung cấp để test — vẫn còn dùng được cho test sau này. Bản `.venv/` của
prototype (môi trường Python ~1.2GB) không commit vào git (xem `.gitignore`) và bản sibling gốc ngoài repo đã bị
xoá (2026-10-27, owner yêu cầu, trùng lặp với bản trong repo) — `requirements.txt` mới thêm (chưa pin version,
suy ra từ `import` trong 2 script) để owner tự `pip install -r requirements.txt` tạo lại venv nếu cần chạy lại.

Kế hoạch chi tiết đầy đủ (pipeline, tính năng, CÓ THỂ/KHÔNG THỂ làm, lộ trình): `LogoExtract-prototype/PLAN.md`.

---

## Đợt 2026-10-27 (đêm, tự động — owner yêu cầu "code xong test kỹ, sáng mai kiểm tra")

### Đã làm, đã TEST THẬT trên môi trường Android (máy ảo, không phải chỉ build)

Owner yêu cầu rõ: thiết kế UI chi tiết (đã làm bằng agent, xem link artifact owner đã có), đặt **giao diện sáng
làm mặc định**, thêm **ads** như 2 app kia, rồi **code thật + test thật kỹ**. Không có máy thật kết nối giờ này
(ban đêm) nên đã tự tạo 1 máy ảo Android (`logoextract_test`, Pixel 7 API 36) để test thay thế — khuyến nghị
owner vẫn nên tự test lại trên máy thật sáng mai, máy ảo không thay thế hoàn toàn được.

**Kiến trúc đã dựng** (solution `LogoExtract.slnx` tại `Mobile-LogoExtract/`):
- `LogoExtract.Core` (net9.0, thuần .NET, test được trên PC): `Models.cs` (LogoItem/BatchGroup/ItemState/WarpGrid/
  ColorAdjust), `ItemStore.cs` (lưu trữ item theo lô, 1 folder/item giống `DocumentStore` của DocScanner — **đã
  áp dụng ngay bài học rút ra từ lỗi race condition của DocumentStore.Delete đợt trước**: xoá khoá/cache SAU khi
  xoá thư mục xong, không phải trước), `Segmentation/` (ISegmentationService + U2NetSegmentationService chạy ONNX
  Runtime), `Warp/MeshWarp.cs` (áp dụng lưới biến dạng, **thuật toán có sẵn nhưng CHƯA có UI kéo-thả** — xem mục
  "Chưa làm" bên dưới), `Imaging/ColorAdjustFilter.cs`, `Vectorize/ContourTracer.cs` (trace viền silhouette +
  Douglas-Peucker + xuất SVG).
- `LogoExtract.Core.Tests` (xUnit): test trực tiếp trên 4 ảnh mẫu thật (không phải ảnh giả), xác nhận wrapper C#
  cho kết quả khớp bản Python prototype đã test trước đó.
- `LogoExtract.AdsService.Core` + `LogoExtract.AdsService.LevelPlay`: copy nguyên từ DocScanner (bản mới nhất, đã
  có fix retry/watchdog init), đổi namespace. **AppKey + ad unit ID đều là PLACEHOLDER** (`LevelPlayConfig.cs`,
  `RealAdsConfig.cs`) — **owner cần tự tạo app LogoExtract mới trên dashboard LevelPlay** để có ID thật, không
  dùng chung được với DocScanner/PdfReader (mỗi app 1 entry riêng trên dashboard). Cho tới lúc đó ads tự động tắt
  hẳn (không ads nào được gọi), an toàn, không lỗi.
- `LogoExtract` (app MAUI Android, net10.0-android, min SDK 26): Items View (Trang chủ), Nhập ảnh (camera/thư viện
  nhiều ảnh), Tách nền (brush thêm/xoá bằng tay), Chỉnh sửa (màu + vector hoá + xuất gộp chung — xem lý do ở mục
  "Thu gọn phạm vi" bên dưới).
- Giao diện: **sáng làm mặc định** (`App.xaml.cs`: `UserAppTheme = AppTheme.Light`), màu thương hiệu mint theo
  đúng bảng màu agent thiết kế, token tối vẫn giữ nguyên trong `Colors.xaml` (dùng được nếu sau này thêm nút đổi
  giao diện).

### 3 lỗi thật tìm được qua test trên máy ảo (không phải chỉ đọc code)

1. **Model ONNX U2Net "Small" + "Large" không giải quyết được bài toán nếp nhăn** (đã biết từ trước, xem PLAN.md
   mục 0) — không phải lỗi đêm nay, chỉ nhắc lại vì đây là lý do cốt lõi app đổi hướng sang "tách là chính, méo để
   người dùng tự sửa".
2. **Mask Gray8 không vẽ được qua SKCanvas (silent no-op)**: `MaskEditorView`'s brush tool không có tác dụng gì —
   không crash, không log lỗi, chỉ đơn giản KHÔNG vẽ. Nguyên nhân: `SKBitmap` định dạng `Gray8` không hỗ trợ vẽ
   qua `SKCanvas.DrawCircle` một cách đáng tin cậy. **Sửa**: đổi toàn bộ mask sang `SKColorType.Rgba8888` (giá
   trị mask vẫn đọc qua kênh `.Red`, không đổi logic ở chỗ khác). File: `U2NetSegmentationService.cs`.
3. **Overlay xanh phủ mask dùng sai kênh (ColorFilter SrcIn dựa vào alpha, nhưng giá trị mask lại nằm ở RGB)**:
   sau khi sửa lỗi #2, brush vẫn "vô hình" vì overlay hiển thị không phản ánh đúng mask thật — toàn bộ ảnh bị tô
   cùng 1 màu đều, không phân biệt được vùng giữ/xoá. **Sửa**: dựng lại overlay bằng cách gán alpha THẬT từ giá
   trị mask (`new SKColor(40,220,150, giá_trị_mask * 110/255)`) rồi vẽ bằng alpha-blend chuẩn, không dùng
   ColorFilter blend-mode nữa. File: `MaskEditorView.cs`. **Đã xác nhận bằng mắt trên máy ảo**: brush thêm/xoá
   hoạt động đúng, thấy rõ vùng bị xoá biến mất khỏi overlay.

### Đã xác nhận chạy đúng, không crash (test tay từng bước trên máy ảo)

- Khởi động app, giao diện sáng đúng, empty state đúng chữ.
- Items View hiển thị đúng item (thumbnail, tên, chip trạng thái màu đúng theo state).
- Mở màn Tách nền: tải model ONNX từ Raw asset, chạy inference thật, hiện overlay đúng hình dạng logo.
- Brush thêm/xoá: xác nhận vẽ đúng (sau khi sửa 2 lỗi trên).
- "Xong": lưu mask.png + cutout.png + thumb.png, chuyển đúng sang màn Chỉnh sửa, ảnh cutout hiển thị đúng (nền đã
  trong suốt, thấy rõ cả vết brush edit vừa làm).
- Màn Chỉnh sửa: slider sáng/tương phản chạy mượt (preview cập nhật), "Lưu chỉnh màu" chạy đúng (dialog xác nhận).
- "Vector hoá": chạy `ContourTracer` thật trên mask thật, không crash, dialog kết quả đúng chữ.
- "Xuất/Chia sẻ": mở đúng share sheet Android hệ thống, ảnh preview hiện đúng trong sheet.

### Chưa làm / thu gọn phạm vi (nói rõ, không giấu)

- **Công cụ làm phẳng thủ công (lưới biến dạng kéo-thả + liquify) CHƯA có UI** — thuật toán `MeshWarp.cs` đã viết
  xong trong Core nhưng chưa có màn hình tương tác để người dùng kéo điểm. Đây là phần việc lớn nhất còn thiếu so
  với PLAN.md, cắt bớt vì thời gian đêm nay có hạn. **Việc tiếp theo nên làm trước tiên.**
- **Gợi ý tự động bằng ảnh tham chiếu (feature matching)** — chưa làm, phụ thuộc vào công cụ lưới ở trên.
- **Vector hoá mới ở mức v1** (đường viền silhouette 1 màu, chưa tách theo từng vùng màu) — đã nói rõ trong chính
  UI app, không phải thiếu sót giấu giếm.
- **Camera trong app**: dùng `MediaPicker.CapturePhotoAsync` đơn giản (chưa làm live-preview dò logo như CameraX
  của DocScanner) — chưa test tay được (máy ảo không có camera thật dùng được tốt), **owner cần tự test trên máy
  thật**.
- **Picker chọn ảnh từ thư viện trên máy ảo bị lỗi index chậm** (môi trường máy ảo, không phải lỗi app — ảnh mới
  push vào không hiện ngay trong picker hệ thống). Test segmentation đêm nay được thực hiện bằng cách tự đặt ảnh
  thẳng vào storage riêng của app qua `adb run-as` (cách test, không phải lỗi app) — **owner cần tự xác nhận lại
  luồng "Chọn từ thư viện" bình thường trên máy thật**, nhiều khả năng ổn vì đây chỉ là đặc thù máy ảo mới tạo.
- **Chưa cài/test trên máy thật** — chỉ test trên máy ảo đêm nay vì không có máy thật kết nối. Máy ảo không có
  camera thật, hiệu năng khác máy thật (có thể chậm hơn hoặc nhanh hơn tuỳ phần cứng) — **tốc độ ONNX inference
  thật trên điện thoại (không phải máy ảo x86_64) chưa được đo, cần owner tự cảm nhận có mượt không**.
- **Ads chưa test được** (AppKey placeholder, chưa có app LogoExtract trên dashboard LevelPlay).
- Chưa viết test tự động cho `ItemStore`, `MeshWarp`, `ColorAdjustFilter` riêng (mới có test cho Segmentation +
  Vectorize, là 2 phần rủi ro cao nhất nên ưu tiên trước).

### Việc owner cần làm

1. Tự test lại trên máy thật (cắm máy, `adb devices`, build+cài y như DocScanner).
2. Tạo app "LogoExtract" mới trên LevelPlay dashboard, lấy AppKey + ad unit ID thật, điền vào `LevelPlayConfig.cs`
   (sandbox) / `RealAdsConfig.cs` (production) đúng theo mẫu 2 file đó đã ghi chú sẵn.
3. Xác nhận luồng "Chọn từ thư viện" hoạt động bình thường trên máy thật (nghi ngờ chỉ là vấn đề máy ảo).
4. Quyết định có muốn làm tiếp công cụ lưới biến dạng (flatten) ngay hay ưu tiên việc khác trước.
