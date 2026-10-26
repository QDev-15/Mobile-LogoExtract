# LogoExtract — kế hoạch app tách logo trên quần áo (100% client, không server)

## 0. Quyết định phạm vi (2026-10-26)

Sau khi test thật Depth Anything V2 (Small + Large) trên 4 ảnh áo nhăn thật (`samples/`, xem `outputs/`):
cả 2 bản đều **không** bắt được nếp nhăn thật của vải — chỉ thấy dáng áo to (ngực phồng, tay cong), bản Large
còn tự "bịa" ra 1 cục lồi giả tại vị trí logo con chó (nhầm hoạ tiết in có đổ bóng với độ lồi thật). Kết luận:
unwarp tự động bằng depth model chung chung **không khả thi** với công cụ hiện có, và các hướng mạnh hơn
(model fine-tune riêng cho vải, AI inpainting) đều cần model nặng → bắt buộc server.

**Owner quyết định: app chạy 100% client, không server.** Hệ quả trực tiếp:
- **Tách logo khỏi áo/nền**: vẫn là cam kết chính, phải làm tốt, làm trên máy.
- **Méo do nhăn**: KHÔNG tự động sửa bằng AI. Thay vào đó cung cấp **công cụ chỉnh tay** cho người dùng tự làm
  phẳng (giống tinh thần "kéo 4 góc" mà DocScanner đã làm cho giấy, nhưng tổng quát hơn: lưới nhiều điểm).
- **Vector hoá**: vẫn làm, nhưng chọn thuật toán thuần (không phải AI nặng) để chạy được trên điện thoại.
- **Quản lý theo lô (batch)**: thêm hẳn 1 tầng quản lý — màn hình danh sách (items view) các logo đã tách,
  xử lý nhiều ảnh cùng lúc, giống kiến trúc `DocumentStore`/`HomeViewModel` của DocScanner.

---

## 1. Pipeline tổng quan (đã cắt bỏ mọi bước cần server)

```
Nhập ảnh (chụp / thư viện, 1 hoặc nhiều ảnh cùng lúc)
    │
    ▼
1. Tách logo khỏi nền/áo (on-device, bán tự động + tay sửa)
    │
    ▼
2. [Tuỳ chọn người dùng] Công cụ làm phẳng thủ công (lưới biến dạng + liquify)
    │   -- có gợi ý tự động nếu người dùng cung cấp ảnh logo gốc tham chiếu (feature matching,
    │      KHÔNG phải AI, thuật toán cổ điển nên vẫn chạy nhẹ trên máy)
    ▼
3. Chỉnh màu thủ công (độ sáng/tương phản/cân bằng trắng bằng tay)
    │
    ▼
4. Vector hoá (thuật toán trace thuần, không AI)
    │
    ▼
5. Lưu vào thư viện (items view) -- xem lại, sửa tiếp, xuất, theo lô
```

---

## 2. Chi tiết từng tính năng

### 2.1 Nhập ảnh
- Chụp bằng camera trong app (tái dùng kinh nghiệm CameraX từ DocScanner) hoặc chọn nhiều ảnh từ thư viện cùng lúc.
- Chụp hàng loạt: cho phép chụp liên tiếp nhiều áo/nhiều logo trong 1 phiên, mỗi ảnh vào hàng đợi xử lý riêng
  (mô hình giống `BackgroundImporter`/`PageIngestQueue` đã có ở DocScanner — xử lý nền, không chặn UI).

### 2.2 Tách logo khỏi nền/áo — **cam kết chính, đầu tư kỹ nhất**
- **Model on-device**: MobileSAM hoặc U2-Net bản rút gọn (ONNX, vài chục MB), chạy qua
  `Microsoft.ML.OnnxRuntime` (có binding .NET chính thức, dùng được trong MAUI Android).
- **Luồng bán tự động**: chạm 1 điểm trên logo → model tự đề xuất vùng chọn → người dùng **sửa tay** bằng
  cọ thêm/bớt (giống "quick selection" của Photoshop) nếu model chọn sai mép.
- **Fallback thủ công hoàn toàn**: nếu ảnh quá khó (model đoán sai hẳn), cho vẽ tay bằng lasso/cọ — không bao
  giờ để người dùng bế tắc.
- Đầu ra: ảnh RGBA (logo, nền trong suốt).

### 2.3 Công cụ làm phẳng thủ công (thay cho AI tự động)
- **Lưới biến dạng (mesh warp)**: phủ 1 lưới N×M điểm lên logo đã tách, người dùng kéo từng điểm/nhóm điểm để
  nắn thẳng theo mắt — toán học y hệt loại bài `PerspectiveWarp`/kéo 4 góc đã có ở DocScanner, chỉ tổng quát
  hoá từ 4 điểm lên lưới nhiều điểm (nội suy song tuyến hoặc thin-plate-spline giữa các điểm lưới).
- **Liquify cục bộ**: công cụ "đẩy/kéo" điểm ảnh cục bộ cho các chỗ méo nhỏ lẻ không hợp lưới — kỹ thuật phổ
  biến trong app chỉnh ảnh, chi phí tính toán thấp, chạy mượt trên điện thoại.
- **Gợi ý tự động (tuỳ chọn, không bắt buộc)**: nếu người dùng có sẵn 1 ảnh logo gốc sạch (không in trên áo),
  dùng feature matching cổ điển (ORB/SIFT — **không phải model AI**, thuật toán toán học thuần, cực nhẹ) để so
  khớp điểm đặc trưng giữa bản gốc và bản chụp trên áo, tự tính gợi ý vị trí ban đầu cho lưới — người dùng vẫn
  tinh chỉnh tay sau đó. Đây là lý do hướng "có ảnh tham chiếu" đáng làm sớm: rẻ, chạy được client, đỡ việc tay.
- **Không có** gì tự động "đoán" phần méo nặng/che khuất — nếu quá nhăn, người dùng tự quyết định chấp nhận
  hoặc chụp lại ảnh khác (áo phẳng hơn).

### 2.4 Chỉnh màu thủ công
- Thanh trượt sáng/tối, tương phản, cân bằng trắng (giống `ResultPage` của DocScanner đã có cho ảnh scan).
- Công cụ "chọn điểm trắng/điểm trung tính": chạm vào 1 điểm biết là màu trung tính, tự tính lại cân bằng màu
  toàn ảnh theo điểm đó — đơn giản, hiệu quả, không cần model AI.
- Hiển thị song song mã màu RGB/Hex đã lấy mẫu, để người dùng tự đối chiếu Pantone thủ công nếu cần in lại.

### 2.5 Vector hoá
- Thuật toán trace thuần (không AI): marching squares tìm đường viền + Douglas-Peucker đơn giản hoá đường +
  gom theo vùng màu phẳng (dạng Potrace/vtracer). Có thể viết thuần C# (giống phong cách `PerspectiveWarp`/
  `PngWriter` tự viết đã có ở DocScanner.Shared) hoặc build 1 thư viện native nhỏ rồi P/Invoke.
- Thanh trượt "mức đơn giản hoá": ít điểm neo (mượt, gọn) <-> nhiều điểm neo (bám sát chi tiết).
- Xem trước vector đè lên ảnh gốc để người dùng tự đánh giá độ khớp trước khi lưu.
- (Tương lai, không phải v1): trình sửa node vector mini, cho kéo sửa từng điểm neo.

### 2.6 Quản lý theo lô — Items View
Kiến trúc tương tự `DocumentStore`/`HomeViewModel` của DocScanner:
- **Màn hình chính**: danh sách các "item" (mỗi item = 1 logo đã/đang xử lý), hiện thumbnail + trạng thái
  (Đang tách nền / Chờ làm phẳng / Đã vector hoá / Hoàn tất).
- **Xử lý hàng loạt**: nhập 10 ảnh cùng lúc → vào hàng đợi nền, tách nền tự động cho cả lô trước, người dùng
  duyệt lại từng cái sau (không phải ngồi chờ từng ảnh một).
- **Thư mục/nhóm**: gom theo lô nhập (vd "Áo lớp A3", "Nhập 26/10/2026"), giống `DocumentStore.Folders`.
- **Chọn nhiều, thao tác hàng loạt**: xuất hàng loạt, xoá hàng loạt, áp cùng 1 mức chỉnh màu cho cả lô (nếu
  cùng điều kiện ánh sáng).
- **Mỗi item**: xem lại ảnh gốc, ảnh đã tách, lưới đã chỉnh, vector cuối — có thể quay lại sửa bất kỳ bước nào.

### 2.7 Xuất file
- PNG nền trong suốt (ảnh raster đã làm phẳng + chỉnh màu).
- SVG / PDF (vector).
- Xuất hàng loạt theo lô đã chọn, đặt tên file tự động theo tên item.

---

## 3. CÓ THỂ làm / KHÔNG THỂ làm (nói thẳng, không tô hồng)

### Có thể, cam kết được
- Tách logo khỏi nền/áo đáng tin cậy (có sửa tay dự phòng khi model sai).
- Công cụ chỉnh tay giúp làm phẳng méo nhẹ-vừa hiệu quả, nhanh hơn tự làm bằng Photoshop vì chuyên biệt cho
  việc này (lưới + gợi ý tự động nếu có ảnh gốc tham chiếu).
- Vector hoá tốt cho logo màu phẳng, cạnh rõ (đa số logo in lụa/in chuyển nhiệt thông thường).
- Quản lý hàng loạt mượt, không chặn UI, đúng kinh nghiệm đã có từ DocScanner.
- Chạy hoàn toàn offline, không tốn chi phí server, giữ được mô hình kinh doanh "mua 1 lần" như 2 app kia.

### Không thể / nằm ngoài phạm vi v1
- **Tự động làm phẳng nếp nhăn không cần người dùng động tay** — đã test, không khả thi với công cụ
  client-only hiện có.
- **Tự "đoán" phần logo bị che khuất hoàn toàn** (nếp gấp sâu, mất pixel thật) — cần AI sinh ảnh (inpainting),
  model quá nặng cho điện thoại, ngoài phạm vi app này. Nếu owner vẫn muốn tính năng này về sau, đó là **một
  app/dịch vụ riêng có server**, không phải bản mở rộng của app client này.
- **Logo thêu (embroidery)** — không phải hoạ tiết phẳng, không nằm trong "tách logo in phẳng" của app này,
  nên loại khỏi phạm vi hoặc ghi rõ "chỉ hỗ trợ logo in, không hỗ trợ thêu".
- **Khớp màu Pantone chính xác tuyệt đối** — không có thẻ màu chuẩn trong ảnh thì chỉ làm được "gần đúng",
  cần ghi rõ trong app để người dùng không hiểu nhầm là chính xác 100%.

---

## 4. Công nghệ đề xuất (khớp đúng stack .NET MAUI Android đã quen dùng)

| Việc | Công nghệ | Ghi chú |
|---|---|---|
| Chạy model tách nền (ONNX) | `Microsoft.ML.OnnxRuntime` | Binding .NET chính thức, chạy được Android |
| Model tách nền cụ thể | MobileSAM hoặc U2-Netp (bản rút gọn) | Cần tự convert sang ONNX nếu chưa có sẵn, test tốc độ thật trên Note 10+ |
| Lưới biến dạng / liquify | Tự viết C#, dựa trên `Homography`/`PerspectiveWarp` đã có ở `DocScanner.Shared` | Tổng quát hoá 4 điểm lên N điểm, nội suy song tuyến hoặc TPS |
| Feature matching (gợi ý lưới) | OpenCV (qua binding, vd OpenCvSharp hoặc tự bind native) hoặc tự viết ORB thuần C# | Thuật toán cổ điển, không phải model AI, nhẹ |
| Vector hoá | Viết thuần C# kiểu Potrace, hoặc build native vtracer rồi P/Invoke | Tương tự phong cách `PngWriter` tự viết đã có |
| Quản lý batch/items | Kiến trúc giống `DocumentStore`/`PageIngestQueue`/`HomeViewModel` | Tái dùng trực tiếp kinh nghiệm, không phải học lại từ đầu |

---

## 5. Lộ trình đề xuất (M0 → M...)

| Giai đoạn | Nội dung |
|---|---|
| **M0** | Prototype riêng: test model tách nền (MobileSAM/U2-Net) trên 4 ảnh mẫu đã có, đo tốc độ + chất lượng trên máy thật trước khi xây app |
| **M1** | Khung app MAUI, nhập ảnh, tách nền bán tự động + sửa tay, lưu local |
| **M2** | Items view: danh sách, thư mục, chọn nhiều, xử lý hàng loạt nền |
| **M3** | Công cụ làm phẳng thủ công (lưới + liquify), chỉnh màu tay |
| **M4** | Gợi ý tự động bằng ảnh tham chiếu (feature matching) |
| **M5** | Vector hoá + xuất file (PNG/SVG/PDF), xuất hàng loạt |
| **M6** | Hoàn thiện, test máy thật, chuẩn bị Play Console |

---

## 6. Việc tiếp theo ngay bây giờ
Lặp lại đúng cách đã làm cho depth model: **prototype riêng model tách nền (MobileSAM/U2-Net) trên 4 ảnh mẫu
đã có trong `samples/`**, kiểm tra chất lượng tách mép logo trước khi đưa vào app thật — đây là cam kết chính
của app, phải chắc chắn trước khi xây phần còn lại.
