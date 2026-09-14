using System.Globalization;
using System.IO;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

public static class TimeBudgetsService
{
    private static string FilePath => AppConfig.TokenPath("time-budgets.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string GetCurrentWeekKey()
    {
        var now = DateTime.Now;
        var year = ISOWeek.GetYear(now);
        var week = ISOWeek.GetWeekOfYear(now);
        return $"{year}-W{week:D2}";
    }

    public static string GetCurrentWeekLabel()
    {
        var now = DateTime.Today;
        var diff = (7 + (now.DayOfWeek - DayOfWeek.Monday)) % 7;
        var startOfWeek = now.AddDays(-1 * diff);
        var endOfWeek = startOfWeek.AddDays(6);
        var weekNum = ISOWeek.GetWeekOfYear(now);
        return $"Week {weekNum} · {startOfWeek:MMM d} – {endOfWeek:MMM d}";
    }

    public static TimeBudgetStore Load()
    {
        TimeBudgetStore store;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                store = JsonSerializer.Deserialize<TimeBudgetStore>(json) ?? new();
            }
            else
            {
                store = new();
            }
        }
        catch
        {
            store = new();
        }

        var currentWeek = GetCurrentWeekKey();
        var modified = false;

        foreach (var item in store.Items)
        {
            if (string.IsNullOrEmpty(item.CurrentWeekKey))
            {
                item.CurrentWeekKey = currentWeek;
                modified = true;
            }
            else if (item.CurrentWeekKey != currentWeek)
            {
                // Auto-reset elapsed time on new week
                item.CurrentWeekKey = currentWeek;
                item.LoggedSeconds = 0;
                item.IsRunning = false;
                item.LastStartTimeUtc = null;
                modified = true;
            }
            else if (item.IsRunning && item.LastStartTimeUtc.HasValue)
            {
                // Reconcile elapsed time while app was suspended/closed
                var elapsed = (DateTime.UtcNow - item.LastStartTimeUtc.Value).TotalSeconds;
                if (elapsed > 0)
                {
                    item.LoggedSeconds += elapsed;
                    item.LastStartTimeUtc = DateTime.UtcNow;
                    modified = true;
                }
            }
        }

        if (modified)
        {
            Save(store);
        }

        return store;
    }

    public static void Save(TimeBudgetStore store)
    {
        try
        {
            AppConfig.EnsureFolder();
            var json = JsonSerializer.Serialize(store, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Ignore temporary file write contention
        }
    }
}
