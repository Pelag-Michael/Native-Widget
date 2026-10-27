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
                if (Environment.GetEnvironmentVariable("NATIVEWIDGET_CODEX_TEST_TRACE") is { Length: > 0 } trace)
                {
                    File.AppendAllText(trace, text + "\n");
                    File.AppendAllText(trace + ".jsonl", JsonSerializer.Serialize(new { text, instructions,
                        model = p.GetProperty("model").GetString(), effort = p.GetProperty("effort").GetString(),
                        images = input.EnumerateArray().Count(x => x.GetProperty("type").GetString() == "localImage") }) + "\n");
                }
                Reply(new { turn = new { id = turn } }); Event("turn/started", new { threadId = thread, turn = new { id = turn } });
                if (text == "wait") { pendingTurn = turn; continue; }
                if (text == "slow") Thread.Sleep(500);
                if (text == "empty") { Event("turn/completed", new { threadId = thread, turn = new { id = turn, status = "completed" } }); continue; }
                if (text == "crash") return;
                if (text == "fail")
                { Event("turn/completed", new { threadId = thread, turn = new { id = turn, status = "failed", error = new { message = "fixture failure" } } }); continue; }
                var images = input.EnumerateArray().Where(x => x.GetProperty("type").GetString() == "localImage").ToArray();
                if (images.Any(x => !File.Exists(x.GetProperty("path").GetString()))) throw new Exception("Missing image");
                Event("item/started", new { threadId = thread, turnId = turn, item = new { type = "agentMessage", id = "answer", phase = "final_answer", text = "" } });
                Event("item/agentMessage/delta", new { threadId = thread, turnId = turn, itemId = "answer", delta = "Hello " });
                var answer = text == "check prompt" ? instructions : text.StartsWith("shot-") ? text : "Hello " + (images.Length > 0 ? "image" : "text");
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
        var beforeTrace = Environment.GetEnvironmentVariable("NATIVEWIDGET_CODEX_TEST_TRACE");
        var data = Path.Combine(Path.GetTempPath(), "nativewidget-codex-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_CODEX_EXE", Path.Combine(AppContext.BaseDirectory, "NativeWidget.RoundTripTests.exe"));
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", data);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", Guid.NewGuid().ToString("N"));
        var tracePath = Path.Combine(data, "trace.txt"); Environment.SetEnvironmentVariable("NATIVEWIDGET_CODEX_TEST_TRACE", tracePath);
        try
        {
            var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var panel = new HoverAiWindow(new NativeWidget.Models.AppConfig()); panel.EnableCodexBackend(); panel.Show();
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(panel.Dispatcher));
            var modelPicker = (System.Windows.Controls.ComboBox)panel.FindName("CodexModelPicker");
            ((System.Windows.Controls.Expander)panel.FindName("CodexSection")).IsExpanded = true;
            PumpUntil(() => modelPicker.Items.Count > 0, "Automatic model list");
            Require(modelPicker.Items.Cast<string>().Contains("fixture-model"), "Opening Codex settings loads models without pressing Load models");
            modelPicker.IsDropDownOpen = true; modelPicker.SelectedItem = "fixture-model"; modelPicker.IsDropDownOpen = false;
            PumpUntil(() => { var saved = new NativeWidget.Models.AppConfig(); HoverAiAppearanceStore.LoadInto(saved); return saved.HoverAiCodexModel == "fixture-model"; }, "Model selection saved");
            var selectedModelConfig = new NativeWidget.Models.AppConfig(); HoverAiAppearanceStore.LoadInto(selectedModelConfig);
            Require(selectedModelConfig.HoverAiCodexModel == "fixture-model", "Closing the model dropdown saves the chosen model immediately");
            modelPicker.IsDropDownOpen = true; modelPicker.Text = ""; modelPicker.IsDropDownOpen = false;
            ((System.Windows.Controls.Expander)panel.FindName("CodexSection")).IsExpanded = false;
            var originalSystemPrompt = ((System.Windows.Controls.TextBox)panel.FindName("SystemPromptInput")).Text;
            ((System.Windows.Controls.Button)panel.FindName("PromptPresetNewButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            ((System.Windows.Controls.TextBox)panel.FindName("PromptPresetNameInput")).Text = "Fixture format";
            ((System.Windows.Controls.TextBox)panel.FindName("PromptPresetTextInput")).Text = "fixture-preset-rule: use a list";
            ((System.Windows.Controls.Button)panel.FindName("PromptPresetSaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var promptEnabled = (System.Windows.Controls.CheckBox)panel.FindName("PromptPresetsEnabledCheck");
            promptEnabled.IsChecked = true; promptEnabled.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.CheckBox.ClickEvent));
            var promptConfig = new NativeWidget.Models.AppConfig(); HoverAiAppearanceStore.LoadInto(promptConfig);
            Require(promptConfig.HoverAiPromptPresetsEnabled && promptConfig.HoverAiPromptPresets.Single(p => p.Id == promptConfig.HoverAiSelectedPromptPresetId).Prompt == "fixture-preset-rule: use a list", "Prompt library and selection persist separately");
            panel.ReceivePaste("question");
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);
            panel.ReceiveScreenshot(bitmap);
            var firstImages = panel.ScreenshotPaths.ToArray();
            ((System.Windows.Controls.Button)panel.FindName("SendButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !panel.IsCodexBusy, "UI first response");
            Require(((System.Windows.Controls.TextBlock)panel.FindName("AnswerText")).Text == "Hello image" && ((System.Windows.Controls.Button)panel.FindName("CopyResponseButton")).IsEnabled, "Send wires response and enables copy");
            Require(File.ReadAllText(tracePath).Split("fixture-preset-rule: use a list").Length == 2
                && ((System.Windows.Controls.TextBox)panel.FindName("SystemPromptInput")).Text == originalSystemPrompt, "Selected preset reaches Codex without modifying system prompt");
            promptEnabled.IsChecked = false; promptEnabled.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.CheckBox.ClickEvent));
            Require(!panel.HasInput && firstImages.All(path => !File.Exists(path)), "Success clears old text and screenshot files while keeping response");
            panel.ReceivePaste("slow"); panel.SubmitQuestion();
            panel.ReceivePaste("next question"); panel.ReceiveScreenshot(bitmap);
            ((System.Windows.Controls.TextBox)panel.FindName("QuestionInput")).Text = "new follow-up";
            var nextImages = panel.ScreenshotPaths.ToArray();
            PumpUntil(() => !panel.IsCodexBusy, "New draft during answer");
            Require(panel.PastedItemCount == 1 && nextImages.All(File.Exists)
                && ((System.Windows.Controls.TextBox)panel.FindName("QuestionInput")).Text == "new follow-up", "Input added while answering survives completion");
            panel.SubmitQuestion(); PumpUntil(() => !panel.IsCodexBusy, "Next draft response");
            Require(!panel.HasInput && nextImages.All(path => !File.Exists(path)), "Next draft clears after its own successful answer");
            panel.ReceivePaste("slow"); panel.ReceiveScreenshot(bitmap); panel.SubmitQuestion();
            panel.ReceivePaste("queued follow-up"); panel.SubmitQuestion();
            PumpUntil(() => !panel.IsCodexBusy, "Queued manual request sharing images");
            Require(!panel.HasInput && ((System.Windows.Controls.TextBlock)panel.FindName("AnswerText")).Text == "Hello text", "Queued follow-up must not resend already answered text or images");
            panel.ReceivePaste("fail"); panel.SubmitQuestion(); PumpUntil(() => !panel.IsCodexBusy, "Failed request");
            Require(panel.HasInput, "Failed answer preserves input for retry");
            ((System.Windows.Controls.Button)panel.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            panel.ReceivePaste("empty"); panel.SubmitQuestion(); PumpUntil(() => !panel.IsCodexBusy, "Empty response");
            Require(panel.HasInput, "Empty response must preserve input for retry");
            ((System.Windows.Controls.Button)panel.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            panel.SubmitQuestion(); Require(!panel.IsCodexBusy, "Duplicate Send must not start another turn");
            ((System.Windows.Controls.Button)panel.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(!panel.HasInput && !((System.Windows.Controls.Button)panel.FindName("CopyResponseButton")).IsEnabled, "Reset clears response and draft");
            panel.ReceivePaste("wait"); panel.SubmitQuestion();
            Require(panel.IsCodexBusy && (string)((System.Windows.Controls.Button)panel.FindName("SendButton")).Content == "\u25A0", "Send becomes Stop while running");
            ((System.Windows.Controls.Button)panel.FindName("SendButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !panel.IsCodexBusy, "Stop response");
            Require(panel.HasInput, "Stopped answer preserves input");
            ((System.Windows.Controls.Button)panel.FindName("NewChatButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => ((System.Windows.Controls.TextBlock)panel.FindName("CodexStatus")).Text == "New chat ready", "New chat");
            var shotMode = (System.Windows.Controls.CheckBox)panel.FindName("ScreenshotAutoSendCheck");
            shotMode.IsChecked = true; shotMode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.CheckBox.ClickEvent));
            foreach (var prompt in new[] { "shot-one", "shot-two", "shot-three" })
            {
                ((System.Windows.Controls.TextBox)panel.FindName("ScreenshotPromptInput")).Text = prompt;
                panel.ReceiveScreenshot(bitmap, fromCapture: true);
            }
            PumpUntil(() => !panel.IsCodexBusy && ((System.Windows.Controls.TextBlock)panel.FindName("AnswerText")).Text == "shot-three", "Screenshot queue");
            var shotRequests = File.ReadAllLines(tracePath).Where(text => text.StartsWith("shot-")).ToArray();
            Require(shotRequests.SequenceEqual(new[] { "shot-one", "shot-two", "shot-three" }), "Rapid captures must send every screenshot in order");
            Require(!panel.HasInput && ((System.Windows.Controls.TextBox)panel.FindName("ScreenshotPromptInput")).Text == "shot-three"
                && !string.IsNullOrWhiteSpace(((System.Windows.Controls.TextBox)panel.FindName("SystemPromptInput")).Text), "Quick screenshots clear draft and retain configured prompts");
            panel.ReceivePaste("unsent draft");
            ((System.Windows.Controls.TextBox)panel.FindName("QuestionInput")).Text = "unsent typed";
            panel.ReceiveScreenshot(bitmap); var unsentImage = panel.ScreenshotPaths.Single();
            ((System.Windows.Controls.TextBox)panel.FindName("ScreenshotPromptInput")).Text = "shot-isolated";
            panel.ReceiveScreenshot(bitmap, fromCapture: true);
            PumpUntil(() => !panel.IsCodexBusy, "Screenshot preserving other draft input");
            Require(panel.PastedItemCount == 1 && ((System.Windows.Controls.TextBox)panel.FindName("QuestionInput")).Text == "unsent typed"
                && panel.ScreenshotPaths.SequenceEqual(new[] { unsentImage }) && File.Exists(unsentImage),
                "Quick screenshot must clear only its own image, preserving unrelated unsent text, typed input and images");
            ((System.Windows.Controls.Button)panel.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var detailOffset = File.ReadAllLines(tracePath + ".jsonl").Length;
            panel.ReceivePaste("slow"); panel.SubmitQuestion();
            var quickCopy = (System.Windows.Controls.CheckBox)panel.FindName("QuickerCheck");
            quickCopy.IsChecked = true; quickCopy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.CheckBox.ClickEvent));
            panel.ReceiveQuickCopy("copy-first");
            ((System.Windows.Controls.TextBox)panel.FindName("SystemPromptInput")).Text = "system-A";
            ((System.Windows.Controls.ComboBox)panel.FindName("CodexModelPicker")).Text = "model-A";
            ((System.Windows.Controls.ComboBox)panel.FindName("CodexEffortPicker")).SelectedIndex = 2;
            ((System.Windows.Controls.TextBox)panel.FindName("PromptPresetTextInput")).Text = "preset-A";
            promptEnabled.IsChecked = true; promptEnabled.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.CheckBox.ClickEvent));
            ((System.Windows.Controls.TextBox)panel.FindName("ScreenshotPromptInput")).Text = "shot-mixed";
            panel.ReceiveScreenshot(bitmap, fromCapture: true);
            ((System.Windows.Controls.TextBox)panel.FindName("SystemPromptInput")).Text = "system-B";
            ((System.Windows.Controls.ComboBox)panel.FindName("CodexModelPicker")).Text = "model-B";
            ((System.Windows.Controls.ComboBox)panel.FindName("CodexEffortPicker")).SelectedIndex = 3;
            ((System.Windows.Controls.TextBox)panel.FindName("PromptPresetTextInput")).Text = "preset-B";
            panel.ReceiveQuickCopy("copy-last");
            ((System.Windows.Controls.TextBox)panel.FindName("SystemPromptInput")).Text = "system-unsent";
            ((System.Windows.Controls.ComboBox)panel.FindName("CodexModelPicker")).Text = "model-unsent";
            ((System.Windows.Controls.ComboBox)panel.FindName("CodexEffortPicker")).SelectedIndex = 0;
            ((System.Windows.Controls.TextBox)panel.FindName("PromptPresetTextInput")).Text = "preset-unsent";
            PumpUntil(() => !panel.IsCodexBusy, "Mixed quick modes queue");
            var mixed = File.ReadAllLines(tracePath + ".jsonl").Skip(detailOffset)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Require(mixed.Length == 4 && mixed[0].GetProperty("text").GetString() == "slow"
                && mixed[1].GetProperty("text").GetString() == "copy-first"
                && mixed[2].GetProperty("text").GetString()!.Contains("shot-mixed")
                && mixed[3].GetProperty("text").GetString()!.Contains("copy-last"), "Mixed copy and screenshot requests must run FIFO without replacing turns");
            Require(mixed[2].GetProperty("text").GetString()!.Contains("preset-A")
                && mixed[2].GetProperty("instructions").GetString()!.Contains("system-A")
                && mixed[2].GetProperty("model").GetString() == "model-A"
                && mixed[2].GetProperty("effort").GetString() == "high"
                && mixed[3].GetProperty("text").GetString()!.Contains("preset-B")
                && mixed[3].GetProperty("instructions").GetString()!.Contains("system-B")
                && mixed[3].GetProperty("model").GetString() == "model-B"
                && mixed[3].GetProperty("effort").GetString() == "xhigh", "Queued turns retain their preset, system prompt, model and effort snapshots");
            Require(!mixed[3].GetProperty("text").GetString()!.Contains("copy-first")
                && mixed.Select(item => item.GetProperty("images").GetInt32()).SequenceEqual(new[] { 0, 0, 1, 0 }) && !panel.HasInput,
                "Mixed queues must not resend already answered draft text or screenshot attachments");
            promptEnabled.IsChecked = false; promptEnabled.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.CheckBox.ClickEvent));
            ((System.Windows.Controls.TextBox)panel.FindName("PromptPresetTextInput")).Text = "fixture-preset-rule: use a list";
            ((System.Windows.Controls.TextBox)panel.FindName("SystemPromptInput")).Text = originalSystemPrompt;
            ((System.Windows.Controls.ComboBox)panel.FindName("CodexModelPicker")).Text = "";
            quickCopy.IsChecked = false; quickCopy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.CheckBox.ClickEvent));
            shotMode.IsChecked = false; shotMode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.CheckBox.ClickEvent));
            ((System.Windows.Controls.Button)panel.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            panel.ReceivePaste("wait"); panel.ReceiveScreenshot(bitmap); var images = panel.ScreenshotPaths.ToArray(); panel.SubmitQuestion();
            ((System.Windows.Controls.Button)panel.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !panel.IsCodexBusy, "Reset in-flight request");
            Require(!panel.HasInput && images.All(path => !File.Exists(path)) && ((System.Windows.Controls.TextBlock)panel.FindName("AnswerText")).Text == "", "Reset cancels and prevents stale response from reappearing");
            var sending = Path.Combine(Path.GetTempPath(), "NativeWidget", "QuickAI", "Sending");
            Require(!Directory.Exists(sending) || !Directory.EnumerateFiles(sending).Any(), "Pinned image copies must be released after cancellation");
            panel.Close();
            var reopened = new HoverAiWindow(new NativeWidget.Models.AppConfig());
            Require(((NativeWidget.Models.HoverAiPromptPreset)((System.Windows.Controls.ComboBox)reopened.FindName("PromptPresetPicker")).SelectedItem).Name == "Fixture format"
                && ((System.Windows.Controls.TextBox)reopened.FindName("PromptPresetTextInput")).Text == "fixture-preset-rule: use a list",
                "Reopening restores the selected saved prompt after Reset and New chat");
            reopened.Close(); app.Shutdown();
            Console.WriteLine("PASS Codex UI: success clears draft and temp images, preserves response/prompts/new input/queued images; failures and Stop retain drafts; rapid screenshots, New chat and Reset.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVEWIDGET_CODEX_EXE", beforeExe);
            Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR", beforeData);
            Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX", beforeInstance);
            Environment.SetEnvironmentVariable("NATIVEWIDGET_CODEX_TEST_TRACE", beforeTrace);
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
