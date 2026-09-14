namespace NativeWidget.Models;

public sealed class TimeBudget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public double TargetHours { get; set; } = 8.0;
    public int PeriodDays { get; set; } = 7;
    public bool Repeat { get; set; } = true;
    public DateTime CycleStartDate { get; set; } = DateTime.Today;
    public double LoggedSeconds { get; set; } = 0.0;
    public bool IsRunning { get; set; }
    public DateTime? LastStartTimeUtc { get; set; }

    public DateTime CycleEndDate => (CycleStartDate == default ? DateTime.Today : CycleStartDate)
        .AddDays(PeriodDays > 0 ? PeriodDays : 7);

    public int DaysRemaining => Math.Max(0, (int)Math.Ceiling((CycleEndDate - DateTime.Now).TotalDays));

    public double ProgressFraction => TargetHours > 0
        ? Math.Clamp(LoggedSeconds / (TargetHours * 3600.0), 0.0, 1.0)
        : 0.0;

    public int ProgressPercent => (int)Math.Round(ProgressFraction * 100.0);

    public string FormattedLoggedTime
    {
        get
        {
            var span = TimeSpan.FromSeconds(LoggedSeconds);
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            var seconds = span.Seconds;
            return IsRunning
                ? $"{hours}h {minutes:D2}m {seconds:D2}s"
                : $"{hours}h {minutes:D2}m";
        }
    }

    public string FormattedTargetTime => TargetHours % 1 == 0
        ? $"{(int)TargetHours}h"
        : $"{TargetHours:0.#}h";

    public string FormattedPeriodDescription
    {
        get
        {
            var dur = PeriodDays switch
            {
                1 => "1d",
                7 => "7d",
                14 => "14d",
                30 => "30d",
                _ => $"{PeriodDays}d"
            };

            var repeatText = Repeat ? "↻" : "⇥";
            var remaining = DaysRemaining <= 0 ? "ends today" : $"{DaysRemaining}d left";
            return $"{dur} cycle {repeatText} · {remaining}";
        }
    }
}

public sealed class TimeBudgetStore
{
    public List<TimeBudget> Items { get; set; } = new();
    public string ActivePanel { get; set; } = "timer"; // "timer" or "budgets"
}
