# Đề xuất định dạng câu trả lời Quick AI

Đây là bản thiết kế, chưa đổi system prompt hay cách gửi yêu cầu hiện tại.

## Các dạng cần nhận diện

Canvas New Quizzes có chọn một, chọn nhiều, đúng/sai, điền chỗ trống, ghép cặp,
phân loại, sắp xếp, số/công thức, hotspot, tự luận và tải file. Điền chỗ trống có
thể là nhập chữ, dropdown hoặc kéo thả từ. Các câu có thể dùng chung một đoạn
văn/ảnh nguồn. Classic và New Quizzes có khác biệt.

Nguồn: [hướng dẫn sinh viên Instructure](https://community.instructure.com/en/kb/articles/661355-how-do-i-answer-each-type-of-question-in-new-quizzes),
[danh sách loại câu hỏi trong API](https://developerdocs.instructure.com/services/canvas/resources/new_quiz_items).

## Một mẫu nhất quán

Mỗi câu dùng **số câu → loại → kết quả**; thêm một dòng giải thích khi cần.
Đây là nhãn hiển thị trực tiếp, không phải mã màu hoặc nhịp nháy cần học thuộc.

| Dạng | Ví dụ hiển thị |
|---|---|
| Chọn một | **Câu 4 · Chọn một:** B — nội dung lựa chọn |
| Chọn nhiều | **Câu 5 · Chọn 2:** A — nội dung; D — nội dung |
| Đúng/sai | **Câu 6:** Sai — giải thích ngắn |
| Điền một/nhiều ô | **Câu 7:** Ô 1 = `term`; Ô 2 = `another term` |
| Chọn từ trong đoạn | **Câu 8:** `exact phrase` — đoạn 2, sau “…context…” |
| Dropdown/word bank | **Câu 9:** Ô 1 → `exact option`; Ô 2 → `exact option` |
| Ghép cặp | **Câu 10:** tên mục → tên đáp án; mỗi cặp một dòng |
| Sắp xếp | **Câu 11:** tên bước 3 → tên bước 1 → tên bước 2 |
| Phân loại | **Câu 12:** Nhóm X: các mục; Nhóm Y: các mục |
| Số/công thức | **Câu 13:** `12.5 m/s` — giữ đơn vị và độ làm tròn |
| Hotspot | **Câu 14:** mô tả bộ phận/vùng trên ảnh; cần đủ ảnh để xác định |
| Tự luận | **Câu 15:** câu trả lời hoặc dàn ý ngắn, theo giới hạn từ |

## Những điểm tránh nhầm

- Luôn kèm nội dung lựa chọn cạnh A/B/C. Nếu ảnh không có nhãn chữ, dùng
  “Lựa chọn 2 — nội dung”, không tự đặt A/B/C rồi coi đó là nhãn thật.
- Phân biệt “chọn một”, “chọn tất cả” và “chọn đúng N”; không đoán loại khi ảnh bị cắt.
- Trích đúng từ trong bài cho dạng điền từ/chọn từ; không diễn giải thành từ đồng nghĩa.
- Kiểm tra các từ phủ định như **KHÔNG**, **NGOẠI TRỪ** và các điều kiện/số liệu.
- Ảnh thiếu lựa chọn, đoạn đọc hoặc chi tiết hình: hiện **Thiếu dữ kiện** cùng phần
  cần bổ sung. Không đưa phần trăm tự tin do AI tự ước lượng như một xác suất đã kiểm chứng.
- Nhiều ảnh: gắn số câu, hoặc “Ảnh 1 / Ảnh 2” nếu chưa thấy số câu. Không trộn
  các lựa chọn của hai câu khác nhau. Câu cùng một đoạn đọc phải giữ ngữ cảnh đó.
- Khi đang stream: kết quả còn là bản nháp. Chỉ đánh dấu hoàn tất sau khi backend
  báo lượt trả lời đã xong; không dùng một ký tự đang stream làm kết quả cuối.

## Cách triển khai tiếp

Thêm tùy chọn “Định dạng câu hỏi” riêng, không ghi đè system prompt đang lưu.
AI trả cấu trúc gồm `question_id`, `image_index`, `type`, `status`, `answers`,
`reason` và `missing_context`; UI render thành mẫu trên. Dữ liệu không đúng cấu trúc
thì giữ response Markdown hiện tại. Dot tiếp tục chỉ báo trạng thái input/chụp ảnh,
tránh dùng chung làm tín hiệu đáp án gây nhầm.
