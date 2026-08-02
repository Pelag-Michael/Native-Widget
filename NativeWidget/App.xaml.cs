using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using NativeWidget.Services;

namespace NativeWidget;

public partial class App : Application
{
    private const string MutexName = "NativeWidget-SingleInstance-8f3a1c";
    private Mutex? _instanceLock;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var instanceSuffix = Environment.GetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX");
        var mutexName = string.IsNullOrWhiteSpace(instanceSuffix)
            ? MutexName : $"{MutexName}-{instanceSuffix}";
        _instanceLock = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            // Another copy is already running - surface the launcher instead of stacking
            // a second one on top of it.
            var existing = FindWindow(null, "Widgets");
            if (existing != IntPtr.Zero)
            {
                ShowWindow(existing, SW_RESTORE);
                SetForegroundWindow(existing);
            }
            Shutdown();
            return;
        }

        TimerNotifier.Start();
    }
}
