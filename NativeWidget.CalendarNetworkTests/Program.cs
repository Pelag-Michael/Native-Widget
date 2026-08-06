using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using NativeWidget;
using NativeWidget.Models;
using NativeWidget.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--offline-process")
        {
            RunOfflineProcessTestAsync(args[1]).GetAwaiter().GetResult();
            return;
        }

        var originalSuffix = Environment.GetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX");
        Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", "calendar-network-tests-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        var app = new App();
        app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.Run();
        Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", originalSuffix);
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task RunOfflineProcessTestAsync(string executablePath)
    {
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Staged NativeWidget executable was not found.", executablePath);

        var dataDirectory = Path.Combine(Path.GetTempPath(), "nativewidget-offline-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            },
        };
        process.StartInfo.Environment["NATIVEWIDGET_DATA_DIR"] = dataDirectory;
        process.StartInfo.Environment["NATIVEWIDGET_INSTANCE_SUFFIX"] = "offline-process-" + Guid.NewGuid().ToString("N");
        process.StartInfo.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
        process.StartInfo.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
        process.StartInfo.Environment["ALL_PROXY"] = "http://127.0.0.1:1";
        process.StartInfo.Environment["NO_PROXY"] = "";

        try
        {
            File.WriteAllText(Path.Combine(dataDirectory, "config.json"), JsonSerializer.Serialize(new AppConfig
            {
                GoogleClientId = "offline-test-client",
                GoogleClientSecret = "offline-test-secret",
                RestoreWindowSessionEnabled = true,
            }));
            File.WriteAllText(Path.Combine(dataDirectory, "google-token.json"), JsonSerializer.Serialize(new GoogleTokens
            {
                AccessToken = "expired-test-access",
                RefreshToken = "offline-test-refresh",
                ExpiresAt = 0,
            }));
            File.WriteAllText(Path.Combine(dataDirectory, "window-session.json"), JsonSerializer.Serialize(
                new WindowSessionSnapshot
                {
                    Windows =
                    {
                        new WindowSessionEntry
                        {
                            Key = "Calendar", Kind = "Calendar", IsOpen = true,
                            Left = 120, Top = 80, Width = 300, Height = 420,
                        },
                    },
                }));

            if (!process.Start()) throw new InvalidOperationException("Could not start the staged NativeWidget executable.");
            await Task.Delay(TimeSpan.FromSeconds(12));
            if (process.HasExited)
                throw new InvalidOperationException($"NativeWidget exited during forced-offline startup (exit code {process.ExitCode}).");
            Console.WriteLine($"OFFLINE_PROCESS_PASS=NativeWidget remained alive for 12 seconds (PID {process.Id})");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
            }
            process.Dispose();
            try { Directory.Delete(dataDirectory, recursive: true); } catch { }
        }
    }

    private static async Task RunAsync()
    {
        var originalDataDirectory = Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR");
        var testDataDirectory = Path.Combine(Path.GetTempPath(), "nativewidget-calendar-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDataDirectory);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", testDataDirectory);
        try
        {
            await TestNetworkExceptionAsync("DNS failure", 11001);
            await TestNetworkExceptionAsync("connection reset", 10054);
            await TestTimeoutAsync();
            await TestTransientStatusAsync(HttpStatusCode.RequestTimeout);
            await TestTransientStatusAsync(HttpStatusCode.TooManyRequests);
            await TestTransientStatusAsync(HttpStatusCode.InternalServerError);
            await TestInvalidAuthenticationAsync();
            await TestInvalidAuthenticationPayloadAsync();
            await TestLaterSuccessAsync();
            await TestCalendarWindowStateAsync();
            Console.WriteLine("PASS Google Calendar transient-network and CalendarWindow state tests (11 scenarios)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", originalDataDirectory);
            try { Directory.Delete(testDataDirectory, recursive: true); } catch { }
        }
    }

    private static async Task TestNetworkExceptionAsync(string name, int socketErrorCode)
    {
        WriteExpiredTokens();
        var handler = new ScriptedHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(name, new SocketException(socketErrorCode))));
        var client = CreateClient(handler);
        await ExpectAsync<GoogleCalendarTransientException>(() =>
            client.GetUpcomingEventsAsync(new AppConfig(), CancellationToken.None), name);
        Assert(handler.CallCount == 3, $"{name} should use three bounded attempts.");
    }

    private static async Task TestTimeoutAsync()
    {
        WriteExpiredTokens();
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var client = CreateClient(handler, TimeSpan.FromMilliseconds(15));
        await ExpectAsync<GoogleCalendarTransientException>(() =>
            client.GetUpcomingEventsAsync(new AppConfig(), CancellationToken.None), "request timeout");
        Assert(handler.CallCount == 3, "Timeout should use three bounded attempts.");
    }

    private static async Task TestTransientStatusAsync(HttpStatusCode statusCode)
    {
        WriteExpiredTokens();
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));
        var client = CreateClient(handler);
        await ExpectAsync<GoogleCalendarTransientException>(() =>
            client.GetUpcomingEventsAsync(new AppConfig(), CancellationToken.None), $"HTTP {(int)statusCode}");
        Assert(handler.CallCount == 3, $"HTTP {(int)statusCode} should use three bounded attempts.");
    }

    private static async Task TestInvalidAuthenticationAsync()
    {
        WriteExpiredTokens();
        var tokenBefore = File.ReadAllText(GoogleCalendarService.TokenFile);
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = Json("""{"error":"invalid_grant"}"""),
        }));
        var client = CreateClient(handler);
        await ExpectAsync<GoogleCalendarAuthenticationException>(() =>
            client.GetUpcomingEventsAsync(new AppConfig(), CancellationToken.None), "invalid authentication");
        Assert(handler.CallCount == 1, "Permanent authentication errors must not retry.");
        Assert(File.ReadAllText(GoogleCalendarService.TokenFile) == tokenBefore,
            "Invalid authentication must not erase or rewrite the saved token.");
    }

    private static async Task TestInvalidAuthenticationPayloadAsync()
    {
        WriteExpiredTokens();
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json("""{"access_token":null,"expires_in":"invalid"}"""),
        }));
        await ExpectAsync<GoogleCalendarAuthenticationException>(() =>
            CreateClient(handler).GetUpcomingEventsAsync(new AppConfig(), CancellationToken.None),
            "invalid authentication payload");
        Assert(handler.CallCount == 1, "Invalid authentication payloads must not retry indefinitely.");
    }

    private static async Task TestLaterSuccessAsync()
    {
        WriteExpiredTokens();
        var handler = new ScriptedHandler(
            (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("temporary DNS failure")),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json("""{"access_token":"new-access","expires_in":3600}"""),
            }),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json("""{"items":[{"id":"event-1","summary":"Recovered","start":{"dateTime":"2026-08-06T09:00:00+10:00"}}]}"""),
            }));
        var events = await CreateClient(handler).GetUpcomingEventsAsync(new AppConfig(), CancellationToken.None);
        Assert(events.Count == 1 && events[0].Title == "Recovered",
            "A request succeeding after a transient failure should return its events.");
        Assert(handler.CallCount == 3, "Recovery scenario should retry only the failed token request.");
    }

    private static async Task TestCalendarWindowStateAsync()
    {
        var source = new FakeCalendarEventSource();
        var window = new CalendarWindow(new AppConfig(), source);
        try
        {
            source.Loader = (_, _) => Task.FromResult(new List<CalendarEvent>
            {
                new()
                {
                    Id = "visible-event", Title = "Keep me", Start = DateTimeOffset.Now.ToString("o"),
                },
            });
            await window.RefreshEventsAsync();
            Assert(window.DisplayedEventCountForTests == 1, "Successful refresh should render the event.");

            source.Loader = (_, _) => Task.FromException<List<CalendarEvent>>(
                new GoogleCalendarTransientException("offline"));
            await window.RefreshEventsAsync();
            Assert(window.DisplayedEventCountForTests == 1,
                "Transient refresh failure must preserve already displayed events.");
            Assert(!window.IsBusyForTests, "Busy state must be restored after a transient failure.");
            Assert(window.LoadingVisibilityForTests == Visibility.Collapsed,
                "Loading UI must be hidden after a transient failure.");
            Assert(window.StatusForTests.StartsWith("Offline", StringComparison.Ordinal),
                "Transient failure should expose a concise offline status.");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.ResetConcurrency();
            source.Loader = async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new List<CalendarEvent>();
            };
            var first = window.RefreshEventsAsync();
            await entered.Task;
            var second = window.RefreshEventsAsync();
            await second;
            Assert(source.CallCount == 1 && source.MaxActiveCalls == 1,
                "Concurrent refresh triggers must share the single active refresh slot.");
            release.TrySetResult();
            await first;
            Assert(!window.IsBusyForTests && window.LoadingVisibilityForTests == Visibility.Collapsed,
                "Busy and loading state must be restored after the active request finishes.");
        }
        finally
        {
            window.StopForTests();
        }
    }

    private static GoogleCalendarApiClient CreateClient(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            timeout ?? TimeSpan.FromSeconds(1),
            (_, cancellationToken) => Task.Delay(TimeSpan.Zero, cancellationToken),
            _ => 0);

    private static void WriteExpiredTokens()
    {
        AppConfig.EnsureFolder();
        File.WriteAllText(GoogleCalendarService.TokenFile, JsonSerializer.Serialize(new GoogleTokens
        {
            AccessToken = "expired-access",
            RefreshToken = "test-refresh-token",
            ExpiresAt = 0,
        }));
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static async Task ExpectAsync<TException>(Func<Task> action, string name) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"{name} did not throw {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _repeat;
    public int CallCount { get; private set; }

    public ScriptedHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses)
    {
        _responses = new Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(responses);
        if (responses.Length == 1) _repeat = responses[0];
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        var response = _responses.Count > 0 ? _responses.Dequeue() : _repeat;
        return response?.Invoke(request, cancellationToken)
               ?? throw new InvalidOperationException("No scripted HTTP response remains.");
    }
}

internal sealed class FakeCalendarEventSource : ICalendarEventSource
{
    private int _activeCalls;
    public bool IsConnected => true;
    public int CallCount { get; private set; }
    public int MaxActiveCalls { get; private set; }
    public Func<AppConfig, CancellationToken, Task<List<CalendarEvent>>> Loader { get; set; } =
        (_, _) => Task.FromResult(new List<CalendarEvent>());

    public async Task<List<CalendarEvent>> GetUpcomingEventsAsync(AppConfig config,
        CancellationToken cancellationToken)
    {
        CallCount++;
        var active = Interlocked.Increment(ref _activeCalls);
        MaxActiveCalls = Math.Max(MaxActiveCalls, active);
        try
        {
            return await Loader(config, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    public void ResetConcurrency()
    {
        CallCount = 0;
        MaxActiveCalls = 0;
        _activeCalls = 0;
    }
}
