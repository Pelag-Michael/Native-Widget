using System.ComponentModel;

namespace NativeWidget.Models;

public sealed class HoverAiPromptPreset : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string _name = "Prompt mới";
    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
    }
    public string Prompt { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal static List<HoverAiPromptPreset> Defaults() => new()
    {
        new() { Id = "question-format", Name = "Theo dạng câu hỏi", Prompt = """
            Nhận diện dạng câu hỏi từ nội dung và ảnh, trả lời ngắn bằng Markdown.
            Mỗi câu ghi rõ số câu, loại và kết quả. Nếu không thấy số câu, ghi Ảnh 1/Ảnh 2 theo thứ tự đính kèm.
            - Chọn một: số câu — nhãn lựa chọn + nội dung lựa chọn.
            - Chọn nhiều: ghi rõ chọn bao nhiêu; liệt kê nhãn + nội dung của từng lựa chọn.
            - Không có nhãn A/B/C thật trong ảnh: dùng thứ tự lựa chọn và nội dung; không tự gán nhãn chữ.
            - Điền từ/dropdown: ghi từng ô và từ/cụm từ chính xác. Chọn từ trong đoạn: trích đúng từ và vị trí/ngữ cảnh ngắn.
            - Đúng/sai: ghi kết quả và lý do ngắn. Ghép cặp: mỗi cặp một dòng. Sắp xếp: dùng chuỗi mũi tên. Phân loại: nhóm → các mục.
            - Số/công thức: giữ đơn vị, dấu và độ làm tròn yêu cầu. Tự luận: trả lời theo giới hạn từ.
            Kiểm tra từ KHÔNG/NGOẠI TRỪ, phân biệt chọn một với chọn nhiều và không trộn các câu của nhiều ảnh.
            Khi thiếu ảnh, lựa chọn, đoạn đọc hoặc dữ kiện: ghi “Thiếu dữ kiện” và nói rõ cần bổ sung gì; không đoán chắc chắn.
            Không dùng mã màu hay nhịp nháy. Chỉ giải thích thêm khi cần để tránh hiểu nhầm.
            """ },
        new() { Id = "brief", Name = "Ngắn gọn", Prompt = "Trả lời thẳng ý chính bằng tiếng Việt, tối đa 1–3 câu. Dùng Markdown nếu hữu ích. Nếu thiếu dữ kiện, hỏi đúng phần còn thiếu." }
    };
}
