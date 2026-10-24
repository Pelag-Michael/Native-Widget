using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NativeWidget;
using NativeWidget.Services;

internal static class CodexBackendChecks
{
    internal static void FakeServer()
    {
        string thread = ""; string instructions = ""; string? pendingTurn = null; int count = 0;
        void Emit(object message) { Console.WriteLine(JsonSerializer.Serialize(message)); Console.Out.Flush(); }
        void Event(string method, object parameters) => Emit(new { method, @params = parameters });
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var methodValue)) continue;
            var method = methodValue.GetString();
            if (!root.TryGetProperty("id", out var id)) continue;
            var p = root.GetProperty("params"); var requestId = id.Clone();
            void Reply(object result) => Emit(new { id = requestId, result });
            if (method == "initialize") Reply(new { userAgent = "fake" });
            else if (method == "model/list") Reply(new { data = new[] { new { model = "fixture-model", isDefault = true } }, nextCursor = (string?)null });
            else if (method == "thread/start")
            {
                if (p.GetProperty("approvalPolicy").GetString() != "never" || p.GetProperty("sandbox").GetString() != "danger-full-access") throw new Exception("Full-access thread configuration missing");
                instructions = p.GetProperty("developerInstructions").GetString()!;
                thread = Guid.NewGuid().ToString(); Reply(new { thread = new { id = thread } });
            }
            else if (method == "turn/start")
            {
                if (p.GetProperty("approvalPolicy").GetString() != "never" || p.GetProperty("sandboxPolicy").GetProperty("type").GetString() != "dangerFullAccess") throw new Exception("Full-access turn configuration missing");
                var turn = "turn-" + ++count; var input = p.GetProperty("input");
                var text = input.EnumerateArray().Where(x => x.GetProperty("type").GetString() == "text").Select(x => x.GetProperty("text").GetString()).FirstOrDefault() ?? "";
                Reply(new { turn = new { id = turn } }); Event("turn/started", new { threadId = thread, turn = new { id = turn } });
                if (text == "wait") { pendingTurn = turn; continue; }
                if (text == "crash") return;
                if (text == "fail")
                { Event("turn/completed", new { threadId = thread, turn = new { id = turn, status = "failed", error = new { message = "fixture failure" } } }); continue; }
                var images = input.EnumerateArray().Where(x => x.GetProperty("type").GetString() == "localImage").ToArray();
                if (images.Any(x => !File.Exists(x.GetProperty("path").GetString()))) throw new Exception("Missing image");
                Event("item/started", new { threadId = thread, turnId = turn, item = new { type = "agentMessage", id = "answer", phase = "final_answer", text = "" } });
                Event("item/agentMessage/delta", new { threadId = thread, turnId = turn, itemId = "answer", delta = "Hello " });
                var answer = text == "check prompt" ? instructions : "Hello " + (images.Length > 0 ? "image" : "text");
                Event("item/agentMessage/delta", new { threadId = thread, turnId = turn, itemId = "answer", delta = images.Length > 0 ? "image" : "text" });
                Event("item/completed", new { threadId = thread, turnId = turn, item = new { type = "agentMessage", id = "answer", phase = "final_answer", text = answer } });
                Event("turn/completed", new { threadId = thread, turn = new { id = turn, status = "completed" } });
            }
            else if (method == "turn/interrupt")
            {
                Reply(new { }); Event("turn/completed", new { threadId = thread, turn = new { id = pendingTurn, status = "interrupted" } }); pendingTurn = null;
            }
        }
    }
    internal static async Task RunAsync()
    {
        var before = Environment.GetEnvironmentVariable("NATIVEWIDGET_CODEX_EXE");
        var image = Path.Combine(Path.GetTempPath(), "codex-fixture-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(image, new byte[] { 1 });
        Environment.SetEnvironmentVariable("NATIVEWIDGET_CODEX_EXE", Path.Combine(AppContext.BaseDirectory, "NativeWidget.RoundTripTests.exe"));
        try
        {
            using var client = new CodexAppServerClient();
            var models = await client.GetModelsAsync(); Require(models.Single().Id == "fixture-model", "Model catalog");
            var streams = new List<string>();
            var reply = await client.SendAsync(new HoverAiSubmission("question", new[] { image }), "fixture-model", "low", streams.Add);
            Require(reply.Text == "Hello image" && streams.Contains("Hello "), "Image input and streamed answer");
            var thread = client.ThreadId; var pid = client.WorkerPid;
            reply = await client.SendAsync(new HoverAiSubmission("follow-up", Array.Empty<string>()), "", "low", _ => { });
            Require(reply.Text == "Hello text" && client.ThreadId == thread && client.WorkerPid == pid, "Follow-up reuses worker and thread");
            using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            {
                try { await client.SendAsync(new HoverAiSubmission("wait", Array.Empty<string>()), "", "low", _ => { }, stop.Token); throw new Exception("Cancellation was ignored"); }
                catch (OperationCanceledException) { }
            }
            reply = await client.SendAsync(new HoverAiSubmission("after stop", Array.Empty<string>()), "", "low", _ => { });
            Require(reply.Text == "Hello text", "Stop leaves the thread usable");
            try { await client.SendAsync(new HoverAiSubmission("fail", Array.Empty<string>()), "", "low", _ => { }); throw new Exception("Failure was ignored"); }
            catch (InvalidOperationException ex) when (ex.Message == "fixture failure") { }
            try { await client.SendAsync(new HoverAiSubmission("crash", Array.Empty<string>()), "", "low", _ => { }); throw new Exception("Crash was ignored"); }
            catch (IOException) { }
            reply = await client.SendAsync(new HoverAiSubmission("recover", Array.Empty<string>()), "", "low", _ => { });
            Require(reply.Text == "Hello text" && client.ThreadId != thread, "Worker crash reconnects into a new thread");
            var previousThread = client.ThreadId;
            reply = await client.SendAsync(new HoverAiSubmission("check prompt", Array.Empty<string>()), "", "low", _ => { }, systemPrompt: "custom-fixture-rule: be brief");
            Require(reply.Text.Contains("custom-fixture-rule: be brief") && client.ThreadId != previousThread, "Custom system prompt reaches a new thread");
            client.NewChat(); Require(client.ThreadId == null && client.WorkerPid == null, "New chat releases previous session");
            Console.WriteLine("PASS Codex protocol: catalog, image attachment, incremental text, conversation reuse, interruption, failure, crash recovery and new chat.");
        }
        finally { Environment.SetEnvironmentVariable("NATIVEWIDGET_CODEX_EXE", before); File.Delete(image); }
    }
    internal static void RunUi()
    {
        var beforeExe = Environment.GetEnvironmentVariable("NATIVEWIDGET_CODEX_EXE");
        var beforeData = Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR");
        var beforeInstance = Environment.GetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX");
        var data = Path.Combine(Path.GetTempPath(), "nativewidget-codex-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_CODEX_EXE", Path.Combine(AppContext.BaseDirectory, "NativeWidget.RoundTripTests.exe"));
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", data);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", Guid.NewGuid().ToString("N"));
        try
        {
            var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var panel = new HoverAiWindow(new NativeWidget.Models.AppConfig()); panel.EnableCodexBackend(); panel.Show();
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(panel.Dispatcher));
            panel.ReceivePaste("question");
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);
            panel.ReceiveScreenshot(bitmap);
            ((System.Windows.Controls.Button)panel.FindName("SendButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !panel.IsCodexBusy, "UI first response");
            Require(((System.Windows.Controls.TextBlock)panel.FindName("AnswerText")).Text == "Hello image" && ((System.Windows.Controls.Button)panel.FindName("CopyResponseButton")).IsEnabled, "Send wires response and enables copy");
            panel.SubmitQuestion(); Require(!panel.IsCodexBusy, "Duplicate Send must not start another turn");
            ((System.Windows.Controls.Button)panel.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(!panel.HasInput && !((System.Windows.Controls.Button)panel.FindName("CopyResponseButton")).IsEnabled, "Reset clears response and draft");
            panel.ReceivePaste("wait"); panel.SubmitQuestion();
            Require(panel.IsCodexBusy && (string)((System.Windows.Controls.Button)panel.FindName("SendButton")).Content == "\u25A0", "Send becomes Stop while running");
            ((System.Windows.Controls.Button)panel.FindName("SendButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !panel.IsCodexBusy, "Stop response");
            ((System.Windows.Controls.Button)panel.FindName("NewChatButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => ((System.Windows.Controls.TextBlock)panel.FindName("CodexStatus")).Text == "New chat ready", "New chat");
            panel.ReceivePaste("wait"); panel.ReceiveScreenshot(bitmap); var images = panel.ScreenshotPaths.ToArray(); panel.SubmitQuestion();
            ((System.Windows.Controls.Button)panel.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !panel.IsCodexBusy, "Reset in-flight request");
            Require(!panel.HasInput && images.All(path => !File.Exists(path)) && ((System.Windows.Controls.TextBlock)panel.FindName("AnswerText")).Text == "", "Reset cancels and prevents stale response from reappearing");
            var sending = Path.Combine(Path.GetTempPath(), "NativeWidget", "QuickAI", "Sending");
            Require(!Directory.Exists(sending) || !Directory.EnumerateFiles(sending).Any(), "Pinned image copies must be released after cancellation");
            panel.Close(); app.Shutdown();
            Console.WriteLine("PASS Codex UI: Send, image response, Copy state, duplicate prevention, Stop, New chat and Reset during an image request.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_CODEX_EXE", beforeExe);
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", beforeData);
            Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", beforeInstance);
        }
    }
    private static void PumpUntil(Func<bool> condition, string label)
    {
        if (condition()) return;
        var frame = new System.Windows.Threading.DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow > deadline) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); Require(condition(), "Timed out: " + label);
    }

    internal static async Task RunLiveAsync()
    {
        var image = Path.Combine(Path.GetTempPath(), "codex-live-" + Guid.NewGuid().ToString("N") + ".png");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 240, 120));
            drawing.DrawText(new FormattedText("73", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 65, Brushes.White, 1), new Point(65, 20));
        }
        var bitmap = new RenderTargetBitmap(240, 120, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        using (var file = File.Create(image)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file); }
        try
        {
            using var client = new CodexAppServerClient();
            var models = await client.GetModelsAsync(); var model = models.First(m => m.IsDefault).Id;
            var chunks = 0;
            var first = await client.SendAsync(new HoverAiSubmission("Read the number in the image and identify the background color. Reply in one short sentence. Do not use tools.", new[] { image }), model, "low", _ => chunks++);
            Require(first.Text.Contains("73") && first.Text.Contains("red", StringComparison.OrdinalIgnoreCase) && chunks > 0, "Live image understanding and streaming");
            Console.WriteLine("LIVE_FIRST=" + first.Text);
            var thread = client.ThreadId;
            var second = await client.SendAsync(new HoverAiSubmission("What number did you just see? Reply with only the number. Do not use tools.", Array.Empty<string>()), model, "low", _ => { });
            Require(second.Text.Contains("73") && client.ThreadId == thread, "Live follow-up remembers image");
            Console.WriteLine("LIVE_FOLLOWUP=" + second.Text);
            Console.WriteLine("PASS live Codex: " + model + ", PNG vision, streaming and conversation memory.");
        }
        finally { File.Delete(image); }
    }
    internal static async Task RunFullAccessLiveAsync()
    {
        var target = Path.Combine(Path.GetTempPath(), "nativewidget-access-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using var client = new CodexAppServerClient();
            var models = await client.GetModelsAsync();
            var reply = await client.SendAsync(new HoverAiSubmission(
                "Use your terminal tool to create this one UTF-8 test file outside your working directory: " + JsonSerializer.Serialize(target) +
                ". Its exact content must be nativewidget-full-access-ok. Do not modify any other files. After writing it, reply with DONE.", Array.Empty<string>()),
                models.First(m => m.IsDefault).Id, "low", _ => { });
            Require(File.Exists(target) && File.ReadAllText(target).Trim() == "nativewidget-full-access-ok", "Live full-access write outside the working directory failed: " + reply.Text);
            Console.WriteLine("PASS live Codex full access: terminal created the requested test file in Temp, outside its working folder, without approval prompts.");
        }
        finally { if (File.Exists(target)) File.Delete(target); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
