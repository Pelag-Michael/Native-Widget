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

    public static string GetSummaryText(TimeBudgetStore store)
    {
        if (store.Items.Count == 0) return "No active targets";
        var runningCount = store.Items.Count(i => i.IsRunning);
        if (runningCount > 0) return $"{runningCount} running · {store.Items.Count} total targets";
        return store.Items.Count == 1 ? "1 target" : $"{store.Items.Count} targets";
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

        var now = DateTime.Now;
        var modified = false;

        foreach (var item in store.Items)
        {
            if (item.PeriodDays <= 0)
            {
                item.PeriodDays = 7;
                modified = true;
            }

            if (item.CycleStartDate == default)
            {
                item.CycleStartDate = DateTime.Today;
                modified = true;
            }

            // Check if duration expired and repeat is on
            if (item.Repeat && now >= item.CycleEndDate)
            {
                while (now >= item.CycleEndDate)
                {
                    item.CycleStartDate = item.CycleEndDate;
                }
                item.LoggedSeconds = 0;
                item.IsRunning = false;
                item.LastStartTimeUtc = null;
                modified = true;
            }
            else if (item.IsRunning && item.LastStartTimeUtc.HasValue)
            {
                // Reconcile elapsed seconds while app was closed or asleep
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
