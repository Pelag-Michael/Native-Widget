using System.Globalization;
using System.IO;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

internal static class RoutineStore
{
    internal static string Folder => AppConfig.TokenPath("routine");
    internal static string FilePath => Path.Combine(Folder, "routine.json");
    internal static readonly DayOfWeek[] Days = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    internal static RoutineData Load()
    {
        RoutineData data;
        var imported=false;
        if (File.Exists(FilePath)) data = JsonSerializer.Deserialize<RoutineData>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Invalid routine data. Restore routine.json.bak.");
        else if (File.Exists(Path.Combine(Folder, "import-source.json"))) {data = Import(File.ReadAllText(Path.Combine(Folder, "import-source.json")));imported=true;}
        else data = new RoutineData();
        foreach (var day in Days) data.Week.TryAdd(day, new());
        UpgradeReminders(data);
        data.Scale = double.IsFinite(data.Scale) ? Math.Clamp(data.Scale, .35, 1.4) : 1;
        data.Dim = double.IsFinite(data.Dim) ? Math.Clamp(data.Dim, 0, .9) : .6;
        Validate(data);
        if(imported)Save(data); // Persist stable block/task IDs after one-time migration.
        return data;
    }
    internal static void Save(RoutineData data)
    {
        Validate(data);
        AtomicFile.WriteText(FilePath, JsonSerializer.Serialize(data, Options));
    }
    internal static void UpgradeReminders(RoutineData data) => data.Reminders ??= new()
    {new RoutineReminder { Id="original-daily",Time=data.ReminderTime,Enabled=data.ReminderEnabled,LastShownDate=data.LastShownDate }};
    internal static List<RoutineTask> EditTaskLines(List<RoutineTask> previous,string text)
    {
        var lines=text.Split('\n').Select(t=>t.Trim()).Where(t=>t.Length>0).ToArray();
        var result=new RoutineTask?[lines.Length];var used=new HashSet<string>();
        for(var i=0;i<lines.Length;i++)
        {var match=previous.FirstOrDefault(t=>t.Text==lines[i]&&!used.Contains(t.Id));if(match!=null){result[i]=new RoutineTask{Id=match.Id,Text=lines[i]};used.Add(match.Id);}}
        for(var i=0;i<lines.Length;i++)if(result[i]==null)
        {var id=i<previous.Count&&!used.Contains(previous[i].Id)?previous[i].Id:Guid.NewGuid().ToString("N");used.Add(id);result[i]=new RoutineTask{Id=id,Text=lines[i]};}
        return result.Select(t=>t!).ToList();
    }
    internal static RoutineData Import(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Week", out _))
        {
            var native = JsonSerializer.Deserialize<RoutineData>(json) ?? throw new InvalidDataException("Invalid routine file.");
            foreach (var day in Days) native.Week.TryAdd(day, new());
            Validate(native); return native;
        }
        var data = new RoutineData(); var keys = new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
        for (var i = 0; i < Days.Length; i++)
        {
            var blocks = new List<RoutineBlock>(); data.Week[Days[i]] = blocks;
            if (!doc.RootElement.TryGetProperty(keys[i], out var day)) continue;
            foreach (var b in day.EnumerateArray())
            {
                var block = new RoutineBlock { Start = b.GetProperty("s").GetString()!, End = b.GetProperty("e").GetString()!, Title = b.GetProperty("t").GetString()!, Category = b.GetProperty("c").GetString()! };
                if(block.Category=="swinburne")block.Category="study";
                if (b.TryGetProperty("tasks", out var tasks))
                    foreach (var t in tasks.EnumerateArray()) block.Tasks.Add(new RoutineTask { Text = t.ValueKind == JsonValueKind.String ? t.GetString()! : t.GetProperty("text").GetString()! });
                blocks.Add(block);
            }
        }
        Validate(data); return data;
    }
    internal static int Minute(string time)
    {
        if (time == "24:00") return 1440;
        return TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.Hour * 60 + parsed.Minute : throw new InvalidDataException("Use a time in HH:mm format.");
    }
    internal static void Validate(RoutineData data)
    {
        if (data.Week == null || data.Cards == null || data.Completed == null) throw new InvalidDataException("Invalid routine file.");
        if (Minute(data.ReminderTime) >= 1440) throw new InvalidDataException("Reminder time must be before midnight.");
        if(data.Reminders!=null)foreach(var reminder in data.Reminders)
        {
            if(Minute(reminder.Time)>=1440)throw new InvalidDataException("Reminder time must be before midnight.");
            if(!reminder.WholeDay&&reminder.Enabled&&reminder.BlockIds.Count==0&&reminder.TaskIds.Count==0)throw new InvalidDataException("Select at least one block or task for a selected-task reminder.");
        }
        foreach (var blocks in data.Week.Values)
        {
            var sorted = blocks.OrderBy(b => Minute(b.Start)).ToArray();
            for (var i = 0; i < sorted.Length; i++)
            {
                var b = sorted[i];
                if (Minute(b.Start) >= Minute(b.End)) throw new InvalidDataException("End time must be after start time.");
                if (i > 0 && Minute(sorted[i - 1].End) > Minute(b.Start)) throw new InvalidDataException("Time blocks must not overlap.");
                if (b.Tasks == null || string.IsNullOrWhiteSpace(b.Title)) throw new InvalidDataException("Each block needs a title.");
            }
        }
    }
    internal static string CopyImage(string source)
    {
        var folder = Path.Combine(Folder, "images"); Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension(source));
        File.Copy(source, target); return Path.GetFileName(target);
    }
    internal static string ImagePath(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file) throw new InvalidDataException("Invalid image filename.");
        return Path.Combine(Folder, "images", file);
    }
}
