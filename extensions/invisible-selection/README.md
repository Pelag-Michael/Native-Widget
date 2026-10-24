# Invisible Selection

Extension cho Chrome / Edge: bôi đen chữ không hiện nền xanh, giữ nguyên màu chữ. Chọn chữ và copy vẫn hoạt động bình thường, kể cả trong ô nhập và iframe thông thường.

## Cài đặt

1. Mở `chrome://extensions` (Chrome) hoặc `edge://extensions` (Edge).
2. Bật **Developer mode / Chế độ nhà phát triển**.
3. Chọn **Load unpacked / Tải tiện ích đã giải nén**, chọn chính thư mục `invisible-selection` chứa `manifest.json` này.
4. Ghim **Invisible Selection** lên thanh công cụ nếu muốn bật/tắt nhanh.

Mặc định **ON**. Bấm icon để chuyển **ON / OFF**, áp dụng ngay cho các tab đang mở. Trạng thái được giữ sau khi đóng/mở trình duyệt. Có thể tải lại trang nếu trình duyệt khôi phục tab cũ từ bộ nhớ.

Muốn dùng với file HTML trên máy: vào **Details / Chi tiết** của extension, bật **Allow access to file URLs**. Incognito cần cho phép riêng trong cùng trang.

## Phạm vi

- Chỉ thay đổi cách trình duyệt vẽ vùng chữ được chọn; không đọc nội dung chữ, không lấy clipboard, không gửi dữ liệu qua mạng.
- Không đổi phím copy và không phụ thuộc NativeWidget. Các phím tắt đang dùng trong widget vẫn do widget xử lý.
- Không áp dụng cho ứng dụng Windows, thanh địa chỉ, trang cài đặt trình duyệt, cửa hàng extension hoặc trình đọc PDF tích hợp.
- Những trang tự vẽ vùng chọn bằng canvas / lớp phủ riêng (ví dụ một số trình soạn thảo tài liệu) và nội dung bên trong shadow DOM có thể vẫn hiện màu chọn.

CSS dùng selector có độ ưu tiên cao và `!important` để ghi đè màu bôi đen thông thường của website. Xem [Chrome scripting API](https://developer.chrome.com/docs/extensions/reference/api/scripting).
