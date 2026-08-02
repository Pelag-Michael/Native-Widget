using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace NativeWidget.Services;

/// Watches for countdown timers that have run out and announces them once.
/// Runs app-wide (not tied to the Timers window being open) and also catches up on
/// timers that expired while the app was closed or the machine was powered off.
public static class TimerNotifier
{
    private static readonly DispatcherTimer Poll = new() { Interval = TimeSpan.FromSeconds(5) };

    public static void Start()
    {
        Poll.Tick += (_, _) => AnnouncePending(catchUp: false);
        Poll.Start();

        // Delay the startup sweep slightly so it appears after the UI is up.
        var startup = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        startup.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            AnnouncePending(catchUp: true);
        };
        startup.Start();
    }

    private static void AnnouncePending(bool catchUp)
    {
        var due = TimersService.TakeUnnotifiedExpired();
        if (due.Count == 0) return;

        var message = new StringBuilder();
        foreach (var timer in due)
        {
            message.Append("• ").Append(timer.Title);
            // On a catch-up sweep the timer may have finished long ago (e.g. while the
            // machine was off), so say how long has passed since it ran out.
            if (catchUp) message.Append("  —  kết thúc ").Append(TimersService.DescribeOverdue(timer));
            message.AppendLine();
        }

        TimersService.MarkNotified(due.Select(t => t.Id));

        var header = due.Count == 1 ? "Hết giờ!" : $"{due.Count} bộ đếm đã hết giờ!";
        MessageBox.Show(message.ToString().TrimEnd(), header, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
