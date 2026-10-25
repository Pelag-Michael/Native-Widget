using System.IO;
using System.Windows;
using System.Windows.Controls;
using NativeWidget.Services;

namespace NativeWidget;

public partial class HoverAiWindow
{
    private CodexAppServerClient? _codex;
    private CancellationTokenSource? _codexCancellation;
    private Task? _codexTask;
    private bool _codexEnabled, _codexClosing, _changingChat;
    private int _codexGeneration;
    private HoverAiSubmission? _queuedSubmission;
    private readonly Queue<HoverAiSubmission> _queuedScreenshots = new();
    internal bool IsCodexBusy => _codexCancellation != null;
    internal Task? CodexTaskForTests => _codexTask;

    private void InitializeCodexControls()
    {
        SystemPromptInput.Text = _config.HoverAiSystemPrompt;
        CodexModelPicker.Text = _config.HoverAiCodexModel;
        foreach (var item in CodexEffortPicker.Items.OfType<ComboBoxItem>())
            if ((string)item.Content == _config.HoverAiCodexEffort) CodexEffortPicker.SelectedItem = item;
        CodexModelPicker.LostKeyboardFocus += (_, _) => SaveCodexSelection();
        CodexEffortPicker.SelectionChanged += (_, _) => { if (_ready) SaveCodexSelection(); };
    }
    private void SystemPrompt_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _config.HoverAiSystemPrompt = SystemPromptInput.Text;
        _saveTimer.Stop(); _saveTimer.Start();
    }
    internal void EnableCodexBackend()
    {
        if (_codexEnabled) return;
        _codexEnabled = true;
        SubmissionRequested += request =>
        {
            if (_changingChat || _codexClosing) return;
            if (_codexCancellation != null) { QueueSubmission(request); return; }
            _codexTask = RunCodexAsync(request);
        };
    }
    private void SaveCodexSelection()
    {
        _config.HoverAiSystemPrompt = SystemPromptInput.Text;
        _config.HoverAiCodexModel = CodexModelPicker.Text.Trim();
        _config.HoverAiCodexEffort = (CodexEffortPicker.SelectedItem as ComboBoxItem)?.Content as string ?? "low";
        if (_ready) SavePreferences();
    }
    private async void LoadModels_Click(object sender, RoutedEventArgs e)
    {
        var generation = _codexGeneration;
        LoadModelsButton.IsEnabled = false;
        try
        {
            var selected = CodexModelPicker.Text;
            var models = await (_codex ??= new CodexAppServerClient()).GetModelsAsync();
            if (_codexClosing || generation != _codexGeneration) return;
            CodexModelPicker.ItemsSource = models.Select(model => model.Id).ToArray();
            CodexModelPicker.Text = selected;
            CodexStatus.Text = "Ready · " + (models.FirstOrDefault(m => m.IsDefault)?.Id ?? "Codex");
        }
        catch (Exception ex) { if (!_codexClosing && generation == _codexGeneration) CodexStatus.Text = ex.Message; }
        finally { if (!_codexClosing) LoadModelsButton.IsEnabled = true; }
    }
    private void QueueSubmission(HoverAiSubmission request)
    {
        if (request.QueueIndividually) _queuedScreenshots.Enqueue(request); else _queuedSubmission = request;
        CodexStatus.Text = "Next input queued";
    }
    private async Task RunCodexAsync(HoverAiSubmission request)
    {
        if (_codexCancellation != null) { QueueSubmission(request); return; }
        SaveCodexSelection();
        var generation = ++_codexGeneration;
        using var cancellation = new CancellationTokenSource();
        _codexCancellation = cancellation;
        var pinned = new List<string>();
        var buffer = "";
        var finished = false;
        SendButton.Content = "\u25A0";
        CodexStatus.Text = "Connecting...";
        _responseText = ""; CopyResponseButton.IsEnabled = false;
        AnswerText.Text = "Thinking..."; ResponseLabel.Text = "Codex"; _showingResponse = true;
        RefreshFloatingPanels();
        try
        {
            // Snapshot attachments so Reset cannot delete images while Codex reads them.
            var runFolder = Path.Combine(Path.GetTempPath(), "NativeWidget", "QuickAI", "Sending");
            if (request.ImagePaths.Count > 0) Directory.CreateDirectory(runFolder);
            foreach (var source in request.ImagePaths)
            {
                var target = Path.Combine(runFolder, Guid.NewGuid().ToString("N") + ".png");
                File.Copy(source, target); pinned.Add(target);
            }
            void ReportProgress(string text) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (finished || _codexClosing || generation != _codexGeneration) return;
                buffer = text; _responseText = text; AnswerText.Text = text;
                CopyResponseButton.IsEnabled = text.Length > 0; CodexStatus.Text = "Answering...";
                RefreshFloatingPanels();
            }));
            var reply = await (_codex ??= new CodexAppServerClient()).SendAsync(
                new HoverAiSubmission(request.Text, pinned), _config.HoverAiCodexModel, _config.HoverAiCodexEffort,
                ReportProgress, cancellation.Token, _config.HoverAiSystemPrompt);
            if (generation != _codexGeneration || _codexClosing) return;
            finished = true;
            _responseText = reply.Text; AnswerText.Text = reply.Text.Length > 0 ? reply.Text : "No answer returned.";
            CopyResponseButton.IsEnabled = reply.Text.Length > 0;
            CodexStatus.Text = reply.Interrupted ? "Stopped" : "Ready";
            if (!reply.Interrupted)
            {
                ClearCompletedDraft(request);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == _codexGeneration && !_codexClosing)
            { CodexStatus.Text = "Stopped"; AnswerText.Text = buffer.Length > 0 ? buffer : "Stopped."; }
        }
        catch (Exception ex)
        {
            if (generation == _codexGeneration && !_codexClosing)
            { CodexStatus.Text = ex.Message; AnswerText.Text = buffer.Length > 0 ? buffer + "\n\n" + ex.Message : ex.Message; }
        }
        finally
        {
            finished = true;
            foreach (var path in pinned) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (ReferenceEquals(_codexCancellation, cancellation)) _codexCancellation = null;
            if (!_codexClosing)
            {
                SendButton.Content = "\u2191";
                RefreshFloatingPanels();
                var queued = _queuedScreenshots.Count > 0 ? _queuedScreenshots.Dequeue() : _queuedSubmission;
                if (_queuedScreenshots.Count == 0 && ReferenceEquals(queued, _queuedSubmission)) _queuedSubmission = null;
                if (queued != null && !_changingChat) _codexTask = RunCodexAsync(queued);
            }
        }
    }
    private void StopCodex()
    {
        _queuedSubmission = null; _queuedScreenshots.Clear(); _codexCancellation?.Cancel(); CodexStatus.Text = "Stopping...";
    }
    private void ResetCodexDraft()
    {
        ++_codexGeneration; _queuedSubmission = null; _queuedScreenshots.Clear();
        _codexCancellation?.Cancel();
        CodexStatus.Text = "Input cleared";
    }
    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _changingChat = true; NewChatButton.IsEnabled = false;
        try
        {
            Clear_Click(sender, e);
            if (_codexTask != null) await _codexTask;
            _codex?.NewChat(); CodexStatus.Text = "New chat ready";
        }
        catch (Exception ex) { CodexStatus.Text = ex.Message; }
        finally { _changingChat = false; NewChatButton.IsEnabled = true; }
    }
    private void CloseCodexBackend()
    {
        _codexClosing = true; ++_codexGeneration; _queuedSubmission = null; _queuedScreenshots.Clear();
        _codexCancellation?.Cancel(); _codex?.Dispose();
    }
}
