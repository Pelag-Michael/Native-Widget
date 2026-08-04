using System.IO;
using System.Linq;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

public class CountdownTimer
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// Absolute wall-clock deadline (UTC, unix seconds). Storing the deadline rather than a
    /// remaining duration is what lets a timer keep counting while the app - or the whole
    /// machine - is off.
    public long EndsAtUnix { get; set; }

    /// Total configured duration, kept so the timer can be restarted with the same length.
    public long DurationSeconds { get; set; }

    /// Set once the "time is up" notification has been shown, so it only fires once.
    public bool Notified { get; set; }

    /// Hex color tag (e.g. "#4A7DFF"), or empty for the default accent color.
    public string Color { get; set; } = "";

    public DateTime EndsAt => DateTimeOffset.FromUnixTimeSeconds(EndsAtUnix).LocalDateTime;
    public TimeSpan Remaining => EndsAt - DateTime.Now;
    public bool IsExpired => Remaining <= TimeSpan.Zero;
}

public static class TimersService
{
    private static string FilePath => AppConfig.TokenPath("timers.json");

    public static List<CountdownTimer> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<List<CountdownTimer>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static void Save(List<CountdownTimer> timers)
    {
        AppConfig.EnsureFolder();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(timers));
    }

    public static CountdownTimer Add(string title, TimeSpan duration)
    {
        var timers = Load();
        var timer = new CountdownTimer
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? "Timer" : title.Trim(),
            DurationSeconds = (long)duration.TotalSeconds,
            EndsAtUnix = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeSeconds(),
            Notified = false,
        };
        timers.Add(timer);
        Save(timers);
        return timer;
    }

    public static void Delete(string id)
    {
        var timers = Load();
        timers.RemoveAll(t => t.Id == id);
        Save(timers);
    }

    public static void SetColor(string id, string hex)
    {
        var timers = Load();
        var t = timers.FirstOrDefault(x => x.Id == id);
        if (t == null) return;
        t.Color = hex;
        Save(timers);
    }

    public static void Rename(string id, string title)
    {
        var timers = Load();
        var t = timers.FirstOrDefault(x => x.Id == id);
        if (t == null) return;
        t.Title = string.IsNullOrWhiteSpace(title) ? t.Title : title.Trim();
        Save(timers);
    }

    public static void Restart(string id)
    {
        var timers = Load();
        var t = timers.FirstOrDefault(x => x.Id == id);
        if (t == null) return;
        t.EndsAtUnix = DateTimeOffset.UtcNow.AddSeconds(t.DurationSeconds).ToUnixTimeSeconds();
        t.Notified = false;
        Save(timers);
    }

    public static void MarkNotified(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        if (set.Count == 0) return;
        var timers = Load();
        foreach (var t in timers.Where(t => set.Contains(t.Id))) t.Notified = true;
        Save(timers);
    }

    /// Timers that finished but haven't been announced yet - including ones that ran out
    /// while the app was closed or the machine was powered off.
    public static List<CountdownTimer> TakeUnnotifiedExpired()
    {
        return Load().Where(t => t.IsExpired && !t.Notified).ToList();
    }

    /// Human-readable "how long ago it finished", used when catching up after a restart.
    public static string DescribeOverdue(CountdownTimer timer)
    {
        var overdue = DateTime.Now - timer.EndsAt;
        if (overdue < TimeSpan.FromMinutes(1)) return "just now";
        if (overdue.TotalHours < 1) return $"{(int)overdue.TotalMinutes} min ago";
        if (overdue.TotalDays < 1) return $"{(int)overdue.TotalHours}h {overdue.Minutes}m ago";
        return $"{(int)overdue.TotalDays}d {overdue.Hours}h ago";
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "Time's up";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        return $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}
