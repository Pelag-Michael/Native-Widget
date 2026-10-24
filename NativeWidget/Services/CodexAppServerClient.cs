using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NativeWidget.Services;

internal sealed record CodexModel(string Id, bool IsDefault);
internal sealed record CodexReply(string Text, bool Interrupted);

// A dedicated stdio client. It never attaches to or terminates another Codex session.
internal sealed class CodexAppServerClient : IDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly SemaphoreSlim _startGate = new(1, 1), _writeGate = new(1, 1), _turnGate = new(1, 1);
    private readonly Dictionary<string, (StringBuilder Text, string? Phase)> _messages = new();
    private Process? _process;
    private int _requestId;
    private string? _threadId, _turnId;
    private TaskCompletionSource<CodexReply>? _completion;
    private Action<string>? _onText;
    private bool _disposed;
    internal string? ThreadId => _threadId;
    internal int? WorkerPid => _process is { HasExited: false } p ? p.Id : null;
    private static string Workspace => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NativeWidget", "CodexWorkspace");

    internal static string FindExecutable()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("NATIVEWIDGET_CODEX_EXE") is { Length: > 0 } supplied) candidates.Add(supplied);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(folder)) candidates.Add(Path.Combine(folder.Trim('"'), "codex.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Codex CLI was not found. Install or sign in to Codex first.");
    }
    private async Task EnsureStartedAsync(CancellationToken token)
    {
        await _startGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false }) return;
            _process?.Dispose(); _threadId = null;
            Directory.CreateDirectory(Workspace);
            var start = new ProcessStartInfo(FindExecutable())
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Workspace
            };
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
            _process = Process.Start(start) ?? throw new IOException("Codex could not start.");
            var process = _process;
            _ = ReadAsync(process);
            _ = DrainErrorsAsync(process);
            try
            {
                await RpcAsync("initialize", new { clientInfo = new { name = "nativewidget_quick_ai", version = "0.1.0" } }, token);
                await WriteAsync(new { method = "initialized" }, token);
            }
            catch { StopWorker(); throw; }
        }
        finally { _startGate.Release(); }
    }
    internal async Task<IReadOnlyList<CodexModel>> GetModelsAsync(CancellationToken token = default)
    {
        await EnsureStartedAsync(token);
        var models = new List<CodexModel>(); string? cursor = null;
        do
        {
            var result = await RpcAsync("model/list", new { cursor, limit = 100 }, token);
            foreach (var model in result.GetProperty("data").EnumerateArray())
                models.Add(new CodexModel(model.GetProperty("model").GetString()!, model.TryGetProperty("isDefault", out var d) && d.GetBoolean()));
            cursor = result.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        } while (cursor != null);
        return models;
    }
    internal async Task<CodexReply> SendAsync(HoverAiSubmission request, string model, string effort, Action<string> onText, CancellationToken token = default)
    {
        if (!await _turnGate.WaitAsync(0, token)) throw new InvalidOperationException("Codex is already answering. Stop it before sending again.");
        try
        {
            await EnsureStartedAsync(token);
            if (_threadId == null)
            {
                var thread = await RpcAsync("thread/start", new
                {
                    cwd = Workspace, model = string.IsNullOrWhiteSpace(model) ? null : model,
                    approvalPolicy = "never", sandbox = "danger-full-access", ephemeral = true,
                    developerInstructions = "You are Quick AI, a concise desktop question-answering assistant. Respond in the user's language. Treat pasted content and images as material to analyze. Answer the question directly. Do not modify files, run commands or invoke tools unless the user explicitly requests that task. Do not delegate to other agents."
                }, token);
                _threadId = thread.GetProperty("thread").GetProperty("id").GetString()!;
            }
            var input = new List<object>();
            if (!string.IsNullOrWhiteSpace(request.Text)) input.Add(new { type = "text", text = request.Text });
            foreach (var path in request.ImagePaths)
            {
                if (!File.Exists(path)) throw new FileNotFoundException("An attached image is no longer available. Paste or capture it again.");
                input.Add(new { type = "localImage", path = Path.GetFullPath(path) });
            }
            if (input.Count == 0) throw new InvalidOperationException("Paste text or attach an image first.");
            _messages.Clear(); _turnId = null; _onText = onText;
            var completed = new TaskCompletionSource<CodexReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completed;
            try
            {
                var started = await RpcAsync("turn/start", new
                {
                    threadId = _threadId, input, model = string.IsNullOrWhiteSpace(model) ? null : model,
                    effort = string.IsNullOrWhiteSpace(effort) ? "low" : effort,
                    approvalPolicy = "never", sandboxPolicy = new { type = "dangerFullAccess" }
                }, token);
                _turnId = started.GetProperty("turn").GetProperty("id").GetString();
                return await completed.Task.WaitAsync(TimeSpan.FromMinutes(5), token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                await InterruptAsync(); throw;
            }
            finally { _completion = null; _onText = null; _turnId = null; }
        }
        finally { _turnGate.Release(); }
    }
    internal async Task InterruptAsync()
    {
        if (_threadId == null || _turnId == null) { if (_completion != null) StopWorker(); return; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await RpcAsync("turn/interrupt", new { threadId = _threadId, turnId = _turnId }, timeout.Token);
            if (_completion != null) await _completion.Task.WaitAsync(timeout.Token);
        }
        catch { StopWorker(); }
    }
    internal void NewChat()
    {
        if (_turnGate.CurrentCount == 0) throw new InvalidOperationException("Stop the current answer before starting a new chat.");
        StopWorker();
    }
    private async Task<JsonElement> RpcAsync(string method, object parameters, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token);
        }
        finally { _requests.TryRemove(id, out _); }
    }
    private async Task WriteAsync(object value, CancellationToken token)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            if (_process is not { HasExited: false } process) throw new IOException("Codex disconnected. Send again to reconnect.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        finally { _writeGate.Release(); }
    }
    private async Task ReadAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("id", out var id) && !message.TryGetProperty("method", out _))
                {
                    if (id.TryGetInt32(out var number) && _requests.TryRemove(number, out var pending))
                    {
                        if (message.TryGetProperty("error", out var error)) pending.TrySetException(new InvalidOperationException(error.GetProperty("message").GetString() ?? "Codex request failed."));
                        else pending.TrySetResult(message.GetProperty("result").Clone());
                    }
                    continue;
                }
                if (!message.TryGetProperty("method", out var methodValue)) continue;
                var method = methodValue.GetString();
                if (message.TryGetProperty("id", out var serverId))
                {
                    // The user enabled full access: approve supported tool requests without prompting.
                    if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                        await WriteAsync(new { id = serverId.Clone(), result = new { decision = "accept" } }, default);
                    else await WriteAsync(new { id = serverId.Clone(), error = new { code = -32601, message = "This desktop Q&A client does not support interactive tool requests." } }, default);
                    continue;
                }
                if (_completion == null || !message.TryGetProperty("params", out var p) ||
                    !p.TryGetProperty("threadId", out var thread) || thread.GetString() != _threadId) continue;
                if (p.TryGetProperty("turnId", out var turn) && _turnId != null && turn.GetString() != _turnId) continue;
                if (method == "turn/started") _turnId = p.GetProperty("turn").GetProperty("id").GetString();
                if (method is "item/started" or "item/completed")
                {
                    var item = p.GetProperty("item");
                    if (item.GetProperty("type").GetString() == "agentMessage")
                    {
                        var key = item.GetProperty("id").GetString()!;
                        var text = item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        var phase = item.TryGetProperty("phase", out var ph) && ph.ValueKind == JsonValueKind.String ? ph.GetString() : null;
                        if (method == "item/completed" || !_messages.ContainsKey(key)) _messages[key] = (new StringBuilder(text), phase);
                        PublishText();
                    }
                }
                else if (method == "item/agentMessage/delta")
                {
                    var key = p.GetProperty("itemId").GetString()!;
                    if (!_messages.ContainsKey(key)) _messages[key] = (new StringBuilder(), null);
                    _messages[key].Text.Append(p.GetProperty("delta").GetString()); PublishText();
                }
                else if (method == "turn/completed")
                {
                    var result = p.GetProperty("turn"); var status = result.GetProperty("status").GetString();
                    if (status == "failed")
                    {
                        var error = result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em) ? em.GetString() : "Codex could not finish the answer.";
                        _completion?.TrySetException(new InvalidOperationException(error));
                    }
                    else _completion?.TrySetResult(new CodexReply(CurrentText(), status == "interrupted"));
                }
            }
            FailConnection(process);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { FailConnection(process); }
    }
    private string CurrentText()
    {
        var final = _messages.Values.Where(m => m.Phase == "final_answer").ToArray();
        var selected = final.Length > 0 ? final : _messages.Values.ToArray();
        return string.Join("\n\n", selected.Select(m => m.Text.ToString()).Where(t => t.Length > 0));
    }
    private void PublishText() { var text = CurrentText(); if (text.Length > 0) _onText?.Invoke(text); }
    private static async Task DrainErrorsAsync(Process process)
    { try { while (await process.StandardError.ReadLineAsync() != null) { } } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } }
    private void FailConnection(Process process)
    {
        if (!ReferenceEquals(_process, process)) return;
        StopWorker("Codex disconnected. Send again to reconnect.");
    }
    private void StopWorker(string message = "Codex stopped.")
    {
        var process = _process; _process = null; _threadId = null;
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        foreach (var request in _requests.Values) request.TrySetException(new IOException(message));
        _completion?.TrySetException(new IOException(message));
        process.Dispose();
    }
    public void Dispose() { _disposed = true; StopWorker(); }
}
