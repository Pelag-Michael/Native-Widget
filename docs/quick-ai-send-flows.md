# Ba luồng gửi Quick AI

## Nội dung mỗi lượt

| Luồng | Phần gửi cho AI |
|---|---|
| Send thường / Ctrl+→ | Toàn bộ chữ paste, ô nhập thêm và ảnh đang chờ |
| Quicker / Ctrl+← | Thêm chữ vừa copy vào nháp rồi gửi phần nháp đang chờ |
| Screenshot to AI / Ctrl+, | Chỉ ảnh vừa chụp + prompt screenshot đã cài |

Cả ba đều nhận preset đang chọn nếu bật Prompt presets. System prompt được gửi riêng
qua cấu hình Codex, không bị thay bằng preset. Prompt screenshot không áp dụng cho
Send thường hoặc quick copy.

## Prompt và session

- System prompt: hướng dẫn chung của session, có ưu tiên cao hơn preset trong nội dung lượt gửi.
- Preset: hướng dẫn định dạng riêng cho lượt đó, rồi tới nội dung cần xử lý.
- Quick screenshot: nội dung cần xử lý là prompt screenshot cùng ảnh mới.
- Cả ba dùng chung session cho đến khi New chat/Ctrl+↑ hoặc đổi system prompt.
- Mỗi lượt giữ bản sao preset, prompt screenshot, system prompt, model và effort tại
  thời điểm gửi. Sửa Settings sau đó không thay các lượt đã vào hàng đợi.
- Các hướng dẫn ngôn ngữ tự nhiên vẫn có thể trái nhau, ví dụ system yêu cầu tiếng Việt
  nhưng preset yêu cầu tiếng Anh. Muốn đổi hành vi đó phải chỉnh các prompt cho thống nhất.

## Hàng đợi và dọn input

Mọi lượt đi vào cùng hàng đợi FIFO, theo thứ tự thao tác. Không có ưu tiên vượt hàng
cho screenshot và không ghi đè lượt copy bằng lượt khác.

Trước khi gửi một nháp đang chờ, widget loại phần chữ/ảnh đã được trả lời thành công
bởi các lượt trước. Phần mới chưa gửi vẫn còn; lượt không còn nội dung được bỏ qua.
Điều này tránh copy A đang chạy rồi copy B gửi lại cả A+B không cần thiết.

Sau câu trả lời thành công và không rỗng:

- Send thường/quick copy chỉ xóa nội dung đã gửi; nội dung thêm sau đó được giữ.
- Quick screenshot chỉ xóa ảnh của lượt đó, giữ nháp chữ và các ảnh khác chưa gửi.
- Lỗi, Stop hoặc câu trả lời rỗng giữ input để thử lại.
- Reset/Stop hủy các lượt đang chờ; Reset xóa nháp theo hành vi hiện có.

## Các trường hợp đã kiểm tra

1. Quick screenshot giữ nguyên chữ paste, ô nhập và ảnh nháp cũ chưa gửi.
2. Copy → screenshot → copy khi AI đang bận vẫn chạy đúng thứ tự.
3. Không gửi lại chữ/ảnh đã được xử lý bởi lượt trước; không làm mất file ảnh đang chờ.
4. Đổi preset/system prompt/model trong lúc chờ không thay các lượt đã xếp hàng.
5. Lỗi, câu trả lời rỗng, Stop và Reset không làm response cũ sống lại hoặc xóa nháp sai.

Các test dùng server giao thức giả để xác định chính xác payload và thứ tự; chúng
không bảo đảm AI luôn tuân thủ mọi câu chữ trong prompt.
