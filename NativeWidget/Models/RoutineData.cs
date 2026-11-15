namespace NativeWidget.Models;

public sealed class RoutineData
{
    public Dictionary<DayOfWeek, List<RoutineBlock>> Week { get; set; } = new();
    public Dictionary<string, bool> Completed { get; set; } = new();
    public List<RoutineCard> Cards { get; set; } = new();
    public double Scale { get; set; } = 1;
    public double Dim { get; set; } = .6;
    public bool ReminderEnabled { get; set; }
    public string ReminderTime { get; set; } = "06:45";
    public string LastShownDate { get; set; } = "";
    public List<RoutineReminder>? Reminders { get; set; }
    public int LayoutSeed { get; set; } = 1;
}
public sealed class RoutineReminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Time { get; set; } = "06:45";
    public bool Enabled { get; set; } = true;
    public bool WholeDay { get; set; } = true;
    public List<string> BlockIds { get; set; } = new();
    public List<string> TaskIds { get; set; } = new();
    public string LastShownDate { get; set; } = "";
}
public sealed class RoutineBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "10:00";
    public string Title { get; set; } = "New block";
    public string Category { get; set; } = "work";
    public List<RoutineTask> Tasks { get; set; } = new();
}
public sealed class RoutineTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
}
public sealed class RoutineCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public string ImageFile { get; set; } = "";
    public string Caption { get; set; } = "";
    public string FrameColor { get; set; } = "#F3F0E8";
    public string CaptionColor { get; set; } = "#252A25";
    public string CaptionFont { get; set; } = "Georgia";
}
