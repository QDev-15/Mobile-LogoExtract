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

## Lỗi đã biết: `AndroidLinkMode=Full` làm bản Release lỗi ngầm — không crash, không log

Phát hiện 2026-10-28, qua test A/B Debug vs Release nhiều lần trên máy thật (Samsung Note10+). Đây là lớp lỗi
**rất dễ nhầm sang lỗi logic khác** vì không có exception, không có dòng log nào báo — nên note riêng ở đây,
không chỉ trong log theo ngày bên dưới, để lần sau gặp lại còn nhớ nghi ngờ đúng chỗ ngay từ đầu.

**`AndroidLinkMode` là gì**: cấu hình MSBuild (trong `LogoExtract.csproj`, PropertyGroup `Release`) điều khiển mức
R8 "cắt tỉa" (trim/shrink) code khi build bản Release cho Android — xoá code mà công cụ phân tích tĩnh cho là
"không ai gọi tới", để giảm dung lượng APK. 3 mức:
- `None` — không cắt gì, APK to nhất, an toàn nhất.
- `SdkOnly` — chỉ cắt code trong thư viện/SDK .NET for Android (BCL, binding assemblies), KHÔNG đụng code của
  chính app. Cân bằng tốt giữa dung lượng và an toàn — **đang dùng mức này**.
- `Full` — cắt cả code của app lẫn thư viện, dựa trên "cái gì không thấy gọi trực tiếp thì xoá".

**Vì sao `Full` gây lỗi ở app này cụ thể**: trình cắt tỉa chỉ thấy các lời gọi TRỰC TIẾP trong IL. Bất cứ thứ gì
được gọi gián tiếp — qua reflection, qua binding JNI (ONNX Runtime, SkiaSharp đều thuộc loại này) — nó không "thấy"
đang được dùng nên có thể bị xoá nhầm, dù code C# gốc hoàn toàn bình thường và biên dịch sạch. Hậu quả: KHÔNG
crash, KHÔNG log lỗi (bản thân việc log cũng có thể bị ảnh hưởng) — chỉ đơn giản một phần hành vi âm thầm không
chạy đúng nữa.

**2 triệu chứng đã xác nhận do `Full` gây ra** (cùng 1 nguyên nhân, 2 biểu hiện khác nhau):
1. Tách nền cực chậm/treo vô thời hạn trên bản Release (ONNX Runtime bị ảnh hưởng) — xác nhận bằng A/B test:
   Debug ~3s, Release+`Full` nhiều phút/treo hẳn, Release+`None` lại ~3s như Debug.
2. Mở 1 ảnh đã tách nền xong từ Home → màn Tách nền hiện đen xì, không ảnh, không dialog lỗi, không dòng log nào
   (`adb logcat` lọc theo tag app hoàn toàn im lặng trên bản `Full`, nhưng có log bình thường ngay khi đổi sang
   `SdkOnly`) — nghi do SkiaSharp (hoặc chính cơ chế logging) bị cắt nhầm tương tự.

**Đã sửa**: `LogoExtract.csproj` → `AndroidLinkMode` đổi từ `Full` sang `SdkOnly`. Dung lượng Release lớn hơn
`Full` một chút nhưng không còn rủi ro cắt nhầm code của chính app hay của ONNX Runtime/SkiaSharp.

**Nếu gặp lại**: bất kỳ lỗi nào có đặc điểm "chỉ xảy ra ở bản Release, Debug thì bình thường, không crash, không
log" — nghi `AndroidLinkMode` trước tiên. Kiểm tra nhanh không cần sửa file: build thử với
`-p:AndroidLinkMode=None` rồi so sánh hành vi với bản hiện tại.

**Đã xác nhận trên máy thật (2026-10-28)**: đổi sang `SdkOnly`, build+cài lại Release, owner tự mở lại đúng item
từng bị đen màn hình — hiển thị ảnh đúng. Coi `SdkOnly` là mức mặc định cho Release từ nay, không quay lại `Full`
trừ khi có lý do rõ ràng và test lại kỹ như lần này.

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

---

## Đợt 2026-11-12 (sáng, trước khi giao agent xây UI): review kỹ toàn bộ codebase + sửa các lỗi/lệch thấy được

Review theo yêu cầu owner ("hãy review nó xem còn gì cần hoàn thành nữa không") bằng cách build/test thật, không
chỉ đọc lại CLAUDE.md cũ (bản cũ hoá ra đã lỗi thời khá nhiều so với code thật, xem các mục dưới).

- **`LogoExtract.Core.Tests` không compile được**: `SegmentationSmokeTests.cs`/`VectorizeSmokeTests.cs` vẫn gọi
  `new U2NetSegmentationService(modelPath)` 1 tham số, nhưng constructor đã đổi sang bắt buộc thêm
  `SegmentationProvider` (đợt 2026-10-29, setting CPU/NNAPI) mà không ai cập nhật 2 test này theo — nghĩa là
  không ai chạy test tự động được từ lúc đó. Sửa: thêm `SegmentationProvider.Cpu` vào cả 2 lời gọi.
- **Thêm test thật cho 3 phần Core chưa từng có test riêng** (trước đó chỉ có smoke-test cho Segmentation/
  Vectorize): `ItemStoreTests.cs` (Create/MarkSegmented/ResetToImported/Delete-idempotent/List-order/Update-lỗi
  khi id không tồn tại), `MeshWarpTests.cs` (identity grid = không đổi gì; displacement đều = dịch ảnh đúng số
  pixel tính toán được), `ColorAdjustFilterTests.cs` (None = giữ nguyên pixel; contrast=-1 dùng đúng tính chất
  `Math.Tan(0)=0` để test brightness/contrast tách biệt mà không bị sai số dấu phẩy động của `tan(pi/4)` làm
  nhiễu; saturation=-1 ra đúng luma; `ComputeWhiteBalance` đúng công thức). 22 test mới, tất cả pass.
- **Dọn rác**: xoá 2 file `hs_err_pid21484.log`/`replay_pid21484.log` (JVM crash dump do OOM lúc build đêm
  28/10, đã gitignore nên không ảnh hưởng git, chỉ là rác trên đĩa).
- **Privacy Policy (cả `Logo Extract Privacy Policy.html` và `Views/PrivacyPolicyPage.xaml`) khai SAI sự thật**:
  cả 2 bản đều ghi "ứng dụng không có tính năng mua trong ứng dụng, không dùng SDK thanh toán nào" — nhưng
  `LicenseService.cs` (thêm từ 2026-10-29) dùng thật `Plugin.InAppBilling`/Google Play Billing cho nút mua Pro
  ("Gỡ quảng cáo") đã có đầy đủ trong `SettingsPage.xaml`. Đây là sai lệch chính sách thật (Google Play yêu cầu
  Privacy Policy khai đúng SDK/dữ liệu thu thập) — sửa cả 2 file: thêm dòng Google Play Billing vào bảng/khối bên
  thứ ba, cập nhật ngày "Cập nhật lần cuối" thành 12/11/2026.
- **Comment "PLACEHOLDER/chưa đăng ký app" trong `LevelPlayConfig.cs`/`RealAdsConfig.cs`/`AdsMode.cs` đã lỗi
  thời**: owner xác nhận AppKey + ad unit ID production đều đã là THẬT trên dashboard LevelPlay (không còn
  placeholder như comment cũ mô tả) — chỉ sửa lại comment cho khớp thực tế, không đổi giá trị/logic.
- **Phát hiện, CHƯA sửa (hỏi owner, owner chọn "bỏ qua/để sau")**: app không có cơ chế xin đồng ý quảng cáo
  (GDPR/consent) cho người dùng EU/UK — LevelPlay/ironSource khuyến nghị bước này cho traffic EEA/UK, nhưng đây
  là điểm thiếu CHUNG với cả DocScanner/PdfReader (copy y nguyên code ads service), không phải riêng app này.
  Owner đã được giải thích rủi ro (không phải lỗi build-breaking, không chặn ads hoàn toàn nếu thiếu — chỉ là
  rủi ro tuân thủ chính sách, ảnh hưởng tới % người dùng EU/UK trong tổng doanh thu, nhiều khả năng rất nhỏ với
  app tiếng Việt thị trường chính là VN) — **để sau, áp dụng đồng bộ cho cả 3 app nếu/khi owner quyết định làm**.
- Sau các sửa trên, giao 1 agent con (mobile-app-designer) xây tiếp 2 UI còn thiếu lớn nhất — xem mục ngay dưới.

---

## Đợt 2026-11-12: công cụ "Làm phẳng" (lưới kéo-thả) + cân bằng trắng bằng mẫu màu

Làm 2 việc lớn còn thiếu nhất so với PLAN.md: (1) UI cho `MeshWarp.cs` (thuật toán có sẵn, chưa có màn hình) --
mục owner tự ghi "Việc tiếp theo nên làm trước tiên" ở đợt 2026-10-27; (2) công cụ "Lấy mẫu màu" cho cân bằng
trắng trong màn Chỉnh sửa, đóng đúng TODO mà `AdjustablePreviewView.BuildColorMatrix`'s doc comment tự ghi lại.
Cả 2 phần core algorithm (`MeshWarp`, `ColorAdjustFilter.ComputeWhiteBalance`) đã có sẵn và có test trước đó --
đợt này chỉ thêm UI + wiring, không đổi public API của chúng.

### 1. Công cụ "Làm phẳng" (mesh-warp lưới kéo-thả)

- **Màn hình mới**: `LogoExtract\Views\FlattenGridView.cs` -- `SKCanvasView` tự vẽ + tự xử lý chạm, cùng kiểu với
  `MaskEditorView`/`AdjustablePreviewView` đã có (không `GraphicsView`/`PinchGestureRecognizer`, đúng lý do file
  đó đã ghi: tránh chi phí pipeline gesture của MAUI trên Android). Hiển thị lưới 5x5 điểm (từ `WarpGrid.Identity()`
  hoặc `LogoItem.Warp` cũ) đè lên bản xem trước đã warp; kéo 1 điểm cập nhật displacement của điểm đó, warp lại
  bản xem trước NGAY (không chờ nhấc tay).
- **Quyết định thiết kế quan trọng nhất (có thể không đúng 100% ý owner, cần xác nhận)**: công cụ này LUÔN warp
  từ `cutout.png` GỐC (chưa flatten), không bao giờ warp lại trên `flattened.png` cũ -- dù brief gốc đọc có thể
  hiểu là "mở công cụ thì hiện ảnh đã flatten nếu có". Lý do kỹ thuật: `WarpGrid.Displacements` là độ lệch TUYỆT
  ĐỐI so với vị trí gốc (identity), không phải delta tích luỹ -- nếu warp lại trên ảnh ĐÃ warp một lần, áp cùng
  lưới lần 2 sẽ warp ĐÚP (sai), không phải khôi phục đúng kết quả cũ. Cách đã chọn (warp từ gốc + khôi phục lưới
  đã lưu) cho kết quả HỆT như đọc lại `flattened.png` (vì cả hai đều là `MeshWarp.Apply(cutout_gốc, lưới_đã_lưu)`),
  nhưng không có rủi ro warp đúp khi người dùng kéo thêm. **Owner nên tự kiểm tra: mở lại một item đã làm phẳng,
  xác nhận lưới hiện đúng vị trí cũ và ảnh xem trước khớp với `flattened.png` đã lưu trước đó.**
- **Preview có throttle, không warp real-time 100%**: `MeshWarp.Apply` dùng `GetPixel`/`SetPixel` (không phải
  Span -- xem comment trong `MeshWarp.cs`/`FlattenGridView.cs` về lý do không viết lại thuật toán này), nên
  `FlattenGridView` tự co bản xem trước xuống tối đa 480px cạnh dài rồi chỉ warp lại bản này (không phải ảnh gốc
  12+ MP) khi kéo, và giới hạn tốc độ warp lại ở ~60ms/lần trong lúc kéo (điểm chấm vẫn theo tay mượt, chỉ ảnh bên
  dưới có thể trễ 1-2 khung hình) -- **CHƯA đo được trên máy thật trong phiên này** (xem mục Test bên dưới), nếu
  owner thấy giật, hạ `FlattenGridView.PreviewMaxDimension` (480) là việc nên thử đầu tiên.
- **Nút "Áp dụng"** chạy `MeshWarp.Apply` thật trên ảnh gốc ĐẦY ĐỦ độ phân giải, ghi `flattened.png`
  (`CutoutCompositor.FlattenedFileName`, hằng số mới), rồi `ItemStore.MarkFlattened(id, grid)` (hàm mới, cùng
  kiểu doc comment với `MarkSegmented` -- chỉ đẩy `State` TỚI, không bao giờ lùi: item đã `Vectorized` mà chỉnh
  lại lưới thì `FlattenedFile`/`Warp` vẫn cập nhật nhưng badge dashboard không tụt về "Đã làm phẳng").
- **Sửa thứ tự pipeline (PLAN.md: Tách nền -> Làm phẳng -> Chỉnh màu -> Vector hoá) ở các chỗ đọc "ảnh đang làm
  việc"**: `EditViewModel.ReloadCutoutPreview` giờ đọc `FlattenedFile ?? CutoutFile` (một chỗ sửa, tự lan ra đúng
  cho `ComputeFullResAdjustedAsync`/`SaveToGalleryAsync`/`ExportAsync`/kích thước ảnh trong `VectorizeAsync`, vì
  tất cả đều đọc qua field `_cutout` chung). `VectorizeAsync` khi đã có `FlattenedFile` thì KHÔNG đọc `mask.png`
  gốc nữa (mask đó giờ sai hình, vì logo đã bị warp) -- tự suy mask mới từ KÊNH ALPHA của `flattened.png` (hàm
  mới `ExtractAlphaAsMask`, vì `CutoutCompositor` ghi mask vào đúng alpha của cutout từ đầu, và `MeshWarp.Apply`
  warp đều cả 4 kênh RGBA cùng lúc nên alpha của ảnh đã warp CHÍNH LÀ mask đã warp đúng, không cần warp mask riêng).
- **Xoá `FlattenedFile`/`Warp` khi tách nền lại từ đầu**: `ItemStore.ResetToImported` (dùng bởi
  `ItemsViewModel.RetrySegmentationAsync`) giờ xoá luôn 2 trường này cùng lúc với `CutoutFile`/`ThumbFile` -- lưới
  cũ warp trên pixel không còn tồn tại sẽ hiện ảnh/video rác nếu không xoá. **CHƯA xoá ở `MarkCompleteAsync`**
  (nút "Đánh dấu hoàn thành" ở dashboard) vì hàm đó ghi đè `cutout.png` TRỰC TIẾP (không gọi `ResetToImported`,
  không set `CutoutFile = null`) -- đúng theo nghĩa hẹp "chỉ xoá nơi CutoutFile bị set null" trong yêu cầu, nhưng
  đây vẫn là một kẽ hở thật: nếu item đã làm phẳng rồi sau đó bấm "Đánh dấu hoàn thành", `FlattenedFile` cũ sẽ
  warp nhầm trên pixel mới (ảnh full-keep, khác hẳn mask thật). Không tự sửa thêm vì sửa `MarkSegmented` để luôn
  xoá Flatten sẽ xoá nhầm cả lúc brush-edit "Xong" bình thường (gọi `MarkSegmented` mỗi lần) -- **cờ cho owner
  quyết định hướng nào đúng hơn, chưa tự chọn.**
- **Icon mới**: `Icons.Grid` (codepoint `view_module`, copy đúng byte từ `Mobile-Pdf-reader\src\PdfReader\Views\
  Icons.cs` sau khi xác nhận 2 file `MaterialIcons-Regular.ttf` giống nhau 100% qua sha256 -- không gõ lại
  codepoint theo trí nhớ, đúng quy tắc trong mobile-app-design.md §3.2).

### 2. Cân bằng trắng bằng "Lấy mẫu màu" (eyedropper)

- `AdjustablePreviewView` thêm 3 bindable property `WhiteBalanceR/G/B` (double, mặc định 1.0) + ma trận màu mới
  `BuildWhiteBalanceMatrix` (diag(r,g,b,1), không offset) -- ghép làm filter TRONG CÙNG (áp dụng trước tiên),
  đúng thứ tự `ColorAdjustFilter.Apply`'s LUT thật đã làm (`x = v * wb` trước khi trừ/cộng contrast/brightness),
  để preview và ảnh xuất cuối cùng luôn khớp nhau ngay cả khi cả 2 công cụ (WB + Brightness/Contrast) cùng khác
  mặc định một lúc.
- Thêm `EyedropperArmed` (bool) + event `ColorPicked` (toạ độ NORMALIZED 0..1, không phải pixel) -- chạm nhanh
  trong lúc "armed" bắn event này thay vì chạy logic double-tap-reset-zoom cũ (2 cơ chế loại trừ nhau hoàn toàn
  trong lúc armed, không để chúng tranh nhau nghĩa của "1 cú chạm nhanh").
- `EditViewModel`: `WhiteBalanceR/G/B`, `IsEyedropperArmed`, `ToggleEyedropperCommand`, `ResetWhiteBalanceCommand`,
  `PickWhiteBalance(SKPoint normalized)` (map toạ độ chuẩn hoá đó lên PIXEL THẬT của `_cutout` độ phân giải đầy
  đủ -- không lấy mẫu từ bản xem trước đã co nhỏ -- rồi gọi `ColorAdjustFilter.ComputeWhiteBalance`). Nạp lại từ
  `item.ColorAdjust.WhiteBalance` trong `LoadAsync`, lưu lại trong `PersistColorAdjust` -- đúng quy tắc hiện có
  "mọi action ở toolbar dưới cùng tự lưu, không có nút Lưu riêng".
- `EditPage.xaml`: thêm khối "Cân bằng trắng" vào khu vực slider (không có slider thật -- chỉ có label double-tap
  reset + 1 nút chip "Lấy mẫu màu", đổi hẳn CHỮ (không chỉ màu) khi armed thành "Đang chọn điểm... (chạm vào
  ảnh)" để không chỉ dựa vào màu cho một trạng thái quan trọng (mobile-app-design.md §7). Nút cao 44dp (đủ chuẩn
  touch target) dù 3 slider cạnh nó chỉ cao 24dp theo yêu cầu riêng của owner trước đây -- nút mới này không có
  ngoại lệ đó nên giữ đúng chuẩn.
- **Cố tình KHÔNG làm** (theo đúng yêu cầu "đừng làm thừa"): không hiện ô đọc RGB/hex của điểm vừa chọn.

### Build & test

- `dotnet build LogoExtract/LogoExtract.csproj -c Debug -f net10.0-android`: **BUILD SUCCEEDED, 0 lỗi** (cảnh báo
  còn lại đều có từ trước, không phải do đợt này gây ra -- đã so khớp bằng cách lọc log theo tên file mới sửa).
- `dotnet test LogoExtract.Core.Tests`: **25/25 PASS**, gồm 4 test mới cho `ItemStore.MarkFlattened` +
  `ResetToImported` xoá Flatten (test "không lùi state khi item đã Vectorized", test "ResetToImported xoá cả
  FlattenedFile/Warp").
- **Gotcha tự bắt trong lúc làm, đúng mục §8.10 của mobile-app-design.md**: thêm XAML comment mới 2 lần dính lỗi
  `--` trong comment (MAUIG1001) -- cả hai lần sửa bằng đổi `--` thành em dash `—`, không phải lỗi logic.
- **CHƯA xác nhận trên máy thật/máy ảo thao tác kéo-thả lưới và eyedropper** -- có 1 máy thật kết nối lúc đầu
  phiên (`adb devices` thấy `RF8M81MKLTA`) nhưng mất kết nối ngay khi thử `dotnet build -t:Install` (lỗi
  `AdbException: device not found`), và `adb devices` sau đó không còn thấy máy nào; không có `emulator` CLI
  trong PATH của môi trường này để tự tạo máy ảo thay thế. **Owner cần tự cài bản Debug lên máy thật và kiểm tra
  bằng tay**:
  1. Mở 1 item đã tách nền, bấm "Làm phẳng" -- lưới 5x5 hiện đúng trên ảnh, kéo 1 điểm thấy ảnh warp theo NGAY
     (không phải chỉ điểm di chuyển), có mượt không (nếu giật, xem gợi ý giảm `PreviewMaxDimension` ở trên).
  2. "Đặt lại lưới" đưa mọi điểm về vị trí gốc. "Huỷ" bỏ hết thao tác, không ghi file. "Áp dụng" ghi
     `flattened.png`, chuyển badge dashboard sang "Đã làm phẳng", và bản xem màu (Chỉnh sửa) hiện ảnh ĐÃ flatten
     chứ không phải ảnh gốc nữa.
  3. Mở lại công cụ Làm phẳng cho CHÍNH item đó -- lưới phải hiện lại đúng vị trí đã kéo lần trước (không phải
     lưới identity), và ảnh xem trước khớp với `flattened.png` đã lưu.
  4. "Vector hoá" sau khi đã làm phẳng -- đường viền SVG phải bám đúng silhouette của ảnh ĐÃ flatten, không phải
     hình dạng gốc trước khi flatten.
  5. Màn Chỉnh sửa: bấm "Lấy mẫu màu", chạm vào một điểm ảnh biết là trắng/xám trung tính -- màu tổng thể ảnh đổi
     ngay (bớt ngả vàng/xanh...), nút tự tắt "armed" sau 1 lần chạm. Double-tap label "Cân bằng trắng" đưa về mặc
     định. Lưu ảnh/Chia sẻ/Vector hoá sau đó phải giữ đúng cân bằng trắng đã chọn (không bị mất khi không bấm nút
     lưu riêng nào).
  6. Cả 2 công cụ trên cả 2 theme sáng/tối -- màu chip/label phải đọc được rõ ràng ở cả hai.

### Phạm vi/quyết định khác cần owner biết

- Thanh công cụ dưới cùng của màn Chỉnh sửa giờ có 6 nút thay 5 (thêm "Làm phẳng" ngay sau "Chỉnh sửa", theo đúng
  thứ tự pipeline PLAN.md: Tách nền -> Làm phẳng -> Chỉnh màu -> Vector hoá) -- mỗi nút hẹp hơn một chút
  (`MinimumWidthRequest=56` x 6 = 336dp tối thiểu), chưa tự đo trên màn hình thật có bị chật/xuống dòng không.
- Không thêm pinch-zoom cho `FlattenGridView` (có cho `MaskEditorView`/`AdjustablePreviewView`) -- lưới 5x5 đủ
  thưa để kéo chính xác ở mức fit-to-screen trên ảnh test; nếu owner thấy khó kéo đúng điểm trên ảnh có logo nhỏ,
  thêm zoom là việc hợp lý cho đợt sau.
- Không thêm test cho `FlattenGridView`/`AdjustablePreviewView` (UI thuần SkiaSharp, không unit-test được như
  phần Core) -- đúng theo cách phần UI còn lại của app này đã làm từ trước (`MaskEditorView` cũng không có test).

---

## Đợt 2026-11-12 (chiều): màn "Điều khoản sử dụng & Quyền riêng tư" chặn trước khi vào app

Owner yêu cầu (dịch nguyên văn): thêm popup trước khi app khởi động, bắt buộc đồng ý điều khoản về quảng cáo thu
thập thông tin cá nhân, hoặc mua Pro. Nội dung: bản thân app không thu thập gì cả; quảng cáo CÓ THỂ thu thập
(nêu rõ); mua Pro thì không ai thu thập gì nữa; có nút mua Pro ngay; không mua Pro VÀ không tick đồng ý thì cho
thoát app. Đây là tính năng thật, không phải prototype -- đã code đầy đủ, không để dở phần nào.

### Đã làm

- **`LogoExtract\ViewModels\TermsGateViewModel.cs`** (mới): ViewModel cho màn gate. 3 lệnh public
  (`PurchaseProCommand`, `AgreeAndContinueCommand`, `DeclineAndExitCommand`) + 1 method nền
  (`RefreshInBackgroundAsync`, gọi từ `OnAppearing`, timeout 2.5s) + 2 event (`ProceedRequested`/`ExitRequested`)
  cho code-behind xử lý điều hướng/thoát app (ViewModel không tự `Window.Page = ...` được vì không có `Window`).
  `PurchaseProAsync` copy ĐÚNG pattern timeout-1-phút + dialog của `SettingsViewModel.PurchaseProAsync` (đã có
  sẵn, đã test), chỉ khác bước cuối: thay vì ở lại Settings, gọi `ProceedRequested` để vào thẳng app.
- **`LogoExtract\Views\TermsGatePage.xaml` + `.xaml.cs`** (mới): trang full-bleed, không Shell/không ad banner
  (đúng yêu cầu owner -- "ads sẽ được loại bỏ trong việc hiển thị gate", trang này dành cho người CHƯA đồng ý
  quảng cáo nên không hợp lý tự hiện ads ở đây). Nội dung đúng y nguyên chữ owner đã viết (3 đoạn + card "Nâng
  cấp Pro" nổi bật bằng nền `Secondary` tint, checkbox `Tôi đã đọc và đồng ý...`, 3 nút `Mua Pro ngay` / `Đồng ý
  & Tiếp tục` (disable tới khi tick checkbox) / `Từ chối & Thoát`). `OnBackButtonPressed` override: dismiss dialog
  đang mở trước (nếu có, cùng kiểu `EditPage` đã làm), rồi chạy ĐÚNG đường thoát app giống nút "Từ chối & Thoát"
  -- không có cách nào bấm Back mà thoát khỏi màn này mà KHÔNG đóng app.
- **`LogoExtract\App.xaml.cs`**: constructor nhận thêm `AppDialog dialog` (đã có sẵn trong DI, chỉ thêm tham số).
  `CreateWindow` giờ gọi `CreateMainPage()` (method mới) thay vì `new AppShell()` thẳng -- quyết định hiện gate
  hay vào thẳng app, đọc 2 điều kiện ĐỘC LẬP, đồng bộ, không chờ mạng: `Preferences.Default.Get("terms_accepted_v1",
  false)` (key version `_v1` -- owner xác nhận chủ đích, đổi `_v2` sau này nếu sửa nội dung gate để ép mọi người
  đồng ý lại) HOẶC `license.State.IsPro` (đọc từ cache, giống dòng `ads.AreAdsEnabled` đã có sẵn).
- **Quyết định quan trọng, brief có để ngỏ, tự chọn**: khi mua Pro THÀNH CÔNG ngay tại gate, CHỈ gọi
  `ProceedRequested` (vào thẳng app), KHÔNG set `terms_accepted_v1=true`. Lý do: cờ đó nghĩa là "đã đồng ý điều
  khoản quảng cáo", không đúng với người mua Pro (họ chưa từng tick đồng ý) -- nếu để cờ đó = true, một giao dịch
  Pro bị hoàn tiền/thu hồi sau này sẽ khiến app bỏ qua gate vĩnh viễn dù người dùng chưa từng đồng ý và giờ lại
  thấy quảng cáo. Ngược lại, nhánh "mua Pro ở Settings" không đổi gì (không liên quan gate). Nhánh
  `RefreshInBackgroundAsync` phát hiện Pro=true (mua ở máy khác) THÌ có set cờ này, đúng nguyên văn owner viết
  "persist acceptance too, for consistency".

### Build & test

- `dotnet build LogoExtract/LogoExtract.csproj -c Debug -f net10.0-android`: **BUILD SUCCEEDED, 0 lỗi** (435
  warning, toàn bộ có từ trước, không liên quan các file mới sửa -- đã lọc log theo tên file để xác nhận).
- **ĐÃ cài và test thật trên máy thật** (`RF8M81MKLTA`, `dotnet build -t:Install`), không chỉ build. Xác nhận
  bằng tay từng outcome, kèm đọc trực tiếp file `shared_prefs/btk.logoextract_preferences.xml` qua `run-as` để
  chắc chắn đúng cờ được set/không set, không chỉ nhìn giao diện:
  1. Cài mới (uninstall rồi install lại) -> gate hiện đúng, đúng chữ, đúng layout cả khi cuộn (ScrollView, nội
     dung dài hơn 1 màn hình trên máy test -- phải vuốt đủ xa mới thấy hết checkbox, xem gotcha bên dưới).
  2. Tick checkbox -> `Đồng ý & Tiếp tục` chuyển từ xám (disabled) sang viền/chữ xanh mint (enabled) ĐÚNG NGAY,
     không cần thao tác gì thêm -- xác nhận binding `IsEnabled="{Binding IsAgreed}"` hoạt động.
  3. Bấm `Đồng ý & Tiếp tục` -> vào thẳng `HomePage` (empty state "Chưa có logo nào"), và
     `terms_accepted_v1=true` được ghi đúng vào Preferences (đọc trực tiếp file xác nhận).
  4. Set thủ công `license_is_pro_cached=true` trong Preferences (giả lập Pro) + `terms_accepted_v1=false`, mở
     app -> vào THẲNG `HomePage`, KHÔNG hiện gate -- xác nhận đúng nhánh "cached Pro bỏ qua gate" trong
     `App.CreateMainPage`.
  5. `Từ chối & Thoát` (tap) -> app đóng hẳn, về home screen (`mCurrentFocus` chuyển sang launcher), tiến trình
     vẫn còn trong RAM (`pidof` vẫn ra PID) nhưng không còn Activity/UI nào -- đúng hành vi Android chuẩn của
     `FinishAffinity()` (không cần `Process.killProcess`, như brief đã dự đoán).
  6. Nút Back cứng (`KEYCODE_BACK`) lúc gate đang hiện -> CŨNG đóng app y hệt mục 5, không background app, không
     im lặng không làm gì -- xác nhận `OnBackButtonPressed` override đúng.
  - **KHÔNG test được nút "Mua Pro ngay" (tap thật)**: môi trường agent tự chặn thao tác này vì nó có thể kích
    hoạt giao dịch Google Play Billing thật ("Real-World Transactions") -- đúng, không nên bypass. Code
    `PurchaseProAsync` trong `TermsGateViewModel` là copy gần như nguyên văn `SettingsViewModel.PurchaseProAsync`
    đã được test/chạy thật ở các đợt trước, nên rủi ro thấp, nhưng **owner nên tự bấm thử "Mua Pro ngay" ở màn
    gate ít nhất 1 lần** để chắc chắn luồng mới này (proceed-vào-app thay vì ở lại Settings) không có lỗi copy-
    paste nào.

### Gotcha mới phát hiện, nên đưa vào `mobile-app-design.md` §8

- **`adb shell monkey -p <pkg> -c android.intent.category.LAUNCHER 1`** KHÔNG PHẢI cách an toàn để "chỉ mở app"
  -- tham số đếm cuối (`1`) là số **sự kiện chạm NGẪU NHIÊN** monkey sẽ bắn thêm sau khi mở app (đúng mục đích
  gốc của công cụ `monkey` là random UI testing), không phải "mở 1 lần rồi dừng". Gặp thật trong phiên test này:
  dùng lệnh trên để mở `LogoExtract` nhiều lần liên tiếp, 2 lần ngẫu nhiên trúng đúng checkbox/nút Agree (tự tick/
  tự bấm mà không ai chạm), và ít nhất 1 lần sự kiện ngẫu nhiên trúng một app KHÁC đang chạy nền trên máy
  (`com.vnyh.gp`, một game MMO không liên quan) khiến focus nhảy sang app đó, gây hiểu nhầm ban đầu tưởng app bị
  crash hoặc tưởng gate có bug ẩn. Cách mở app AN TOÀN, không bắn sự kiện nào: `adb shell am start -n
  <pkg>/<activity>` (lấy đúng tên activity qua `adb shell cmd package resolve-activity --brief <pkg>`).
- **`ScrollView` trên 1 máy thật cụ thể cần vuốt ĐỦ XA/ĐỦ CHẬM mới scroll**: một `adb shell input swipe` ngắn
  (400px/300ms) không luôn làm `ScrollView` di chuyển (không rõ do ngưỡng touch-slop của máy, hay do cách
  `input swipe` mô phỏng tốc độ vuốt) -- vuốt dài hơn/chậm hơn (1600px/500ms) mới chắc chắn cuộn. Không phải lỗi
  app (xác nhận bằng `uiautomator dump`: `CheckBox`/2 nút dưới vẫn đúng `bounds` trong cây view, chỉ đơn giản
  chưa cuộn tới). Dùng `uiautomator dump` để lấy toạ độ `bounds` chính xác, đáng tin hơn tự đo bằng mắt trên ảnh
  chụp màn hình khi cần test tap chính xác.

### LỖI DO AGENT GÂY RA, owner cần biết ngay -- mất dữ liệu test thật trên máy

Trong lúc test, agent chạy `adb uninstall btk.logoextract` rồi cài lại để test "cài mới lần đầu" cho sạch, KHÔNG
kiểm tra trước là máy đang có dữ liệu thật: màn Home lúc đó đang có **2 item thật** (`jpeg(2)` x2, 1 cái "Chờ làm
phẳng", 1 cái "Hoàn tất", chụp màn hình trong phiên test). `adb uninstall` xoá toàn bộ dữ liệu riêng của app
(`/data/data/btk.logoextract/files/items/`), xác nhận lại bằng `run-as ... ls` sau đó: thư mục `items` rỗng, mất
hẳn, không có cách khôi phục (không phải git, không có backup). Đây là lỗi quy trình của agent (đáng lẽ phải
kiểm tra `ls items` hoặc chụp màn hình Home TRƯỚC khi uninstall, giống nguyên tắc "kiểm tra trước thao tác phá
huỷ" mà lẽ ra phải áp dụng cho cả thao tác trên thiết bị, không chỉ git) -- không phải lỗi cố ý, nhưng owner cần
biết rõ: 2 item test đó (không rõ owner có cần giữ không) đã mất khi owner kiểm tra lại máy `RF8M81MKLTA` sáng
mai. Toàn bộ Preferences khác (benchmark CPU/NNAPI, segmentation override) cũng bị xoá theo, sẽ tự đo lại ở lần
mở app đầu tiên như bình thường, không cần owner làm gì thêm cho phần đó.

### Owner cần tự kiểm tra thêm

1. Bấm thử "Mua Pro ngay" ở màn gate thật (agent không bấm được, xem gotcha "Real-World Transactions" ở trên).
2. Theme tối: app vẫn force Light toàn bộ (`UserAppTheme = AppTheme.Light`, không đổi gì ở đợt này) nên màn gate
   chưa từng hiện ở Dark theme thật trên máy -- các token `AppThemeBinding Dark=...` đã khai đủ (Tertiary/
   PrimaryDark/CardBackgroundDark/Secondary fallback sang CardBackgroundDark cho card "Nâng cấp Pro") nhưng CHƯA
   được mắt người xác nhận tương phản trên máy thật, chỉ suy luận từ cùng token đã dùng đúng ở các trang khác.
3. Nếu owner có ý định đổi nội dung gate sau này (câu chữ điều khoản), nhớ bump `TermsGatePage.TermsAcceptedKey`
   từ `"terms_accepted_v1"` sang `"terms_accepted_v2"` để ép mọi người đồng ý lại -- đã ghi rõ trong comment của
   hằng số đó và trong `App.xaml.cs`'s `CreateMainPage`.
