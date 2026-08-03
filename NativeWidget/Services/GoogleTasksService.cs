using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

public class GoogleTaskList
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

public class GoogleTaskItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
    public string? ParentId { get; set; }
    public string Description { get; set; } = "";

    /// Google Tasks only stores a date, never a time of day - the countdown this powers
    /// is always in whole days, never hours/minutes.
    public DateTime? Due { get; set; }
}

// Shares google-token.json with GoogleCalendarService - both are granted in the same
// OAuth consent (see GoogleCalendarService.Scope), so no separate Connect flow is needed
// here as long as Calendar has been connected at least once.
public static class GoogleTasksService
{
    private static readonly HttpClient Http = new();
    private static string TokenFile => AppConfig.TokenPath("google-token.json");

    public static bool IsConnected() => File.Exists(TokenFile);

    private static async Task<string?> GetValidAccessTokenAsync(AppConfig cfg)
    {
        if (!File.Exists(TokenFile)) return null;
        var tokens = JsonSerializer.Deserialize<GoogleTokens>(File.ReadAllText(TokenFile))!;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now < tokens.ExpiresAt - 60000) return tokens.AccessToken;
        if (tokens.RefreshToken == null) return null;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = cfg.GoogleClientId,
            ["client_secret"] = cfg.GoogleClientSecret,
            ["refresh_token"] = tokens.RefreshToken,
            ["grant_type"] = "refresh_token",
        };
        var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        if (!json.TryGetProperty("access_token", out var at)) return null;

        tokens.AccessToken = at.GetString()!;
        tokens.ExpiresAt = now + json.GetProperty("expires_in").GetInt64() * 1000;
        File.WriteAllText(TokenFile, JsonSerializer.Serialize(tokens));
        return tokens.AccessToken;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            req.Content = JsonContent.Create(body);
        return req;
    }

    /// Google returns its errors as a normal JSON body, so a failed call used to just look
    /// like a response with no "items" - i.e. an empty widget with no explanation. This
    /// turns the HTTP failure into a real exception the UI can show (most commonly
    /// "insufficient authentication scopes", which means the saved token predates the
    /// Tasks scope being added and the user needs to reconnect).
    private static async Task ThrowIfFailedAsync(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync();
        var detail = text;
        try
        {
            var err = JsonSerializer.Deserialize<JsonElement>(text);
            if (err.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                detail = m.GetString() ?? text;
        }
        catch { }
        throw new HttpRequestException($"Google Tasks {(int)res.StatusCode}: {detail}");
    }

    public static async Task<List<GoogleTaskList>> GetTaskListsAsync(AppConfig cfg)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return new List<GoogleTaskList>();

        var res = await Http.SendAsync(Authed(HttpMethod.Get, "https://www.googleapis.com/tasks/v1/users/@me/lists?maxResults=100", token));
        await ThrowIfFailedAsync(res);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        var lists = new List<GoogleTaskList>();
        if (json.TryGetProperty("items", out var items))
            foreach (var it in items.EnumerateArray())
                lists.Add(new GoogleTaskList { Id = it.GetProperty("id").GetString()!, Title = it.GetProperty("title").GetString() ?? "" });
        return lists;
    }

    public static async Task<List<GoogleTaskItem>> GetTasksAsync(AppConfig cfg, string listId)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return new List<GoogleTaskItem>();

        var all = new List<GoogleTaskItem>();
        string? pageToken = null;
        do
        {
            var url = $"https://www.googleapis.com/tasks/v1/lists/{listId}/tasks?showHidden=true&maxResults=100" +
                      (pageToken != null ? $"&pageToken={pageToken}" : "");
            var res = await Http.SendAsync(Authed(HttpMethod.Get, url, token));
            await ThrowIfFailedAsync(res);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("items", out var items))
                foreach (var it in items.EnumerateArray())
                    all.Add(new GoogleTaskItem
                    {
                        Id = it.GetProperty("id").GetString()!,
                        Title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Completed = it.TryGetProperty("status", out var s) && s.GetString() == "completed",
                        ParentId = it.TryGetProperty("parent", out var p) ? p.GetString() : null,
                        Description = it.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "",
                        Due = it.TryGetProperty("due", out var d) && DateTime.TryParse(d.GetString(), out var dueVal)
                            ? dueVal : null,
                    });
            pageToken = json.TryGetProperty("nextPageToken", out var pt) ? pt.GetString() : null;
        } while (pageToken != null);

        return all;
    }

    public static async Task<string?> AddTaskAsync(AppConfig cfg, string listId, string title, string? parentId = null)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return null;

        var res = await Http.SendAsync(Authed(HttpMethod.Post, $"https://www.googleapis.com/tasks/v1/lists/{listId}/tasks", token, new { title }));
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var newId = json.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

        if (newId != null && parentId != null)
            await Http.SendAsync(Authed(HttpMethod.Post, $"https://www.googleapis.com/tasks/v1/lists/{listId}/tasks/{newId}/move?parent={parentId}", token));

        return newId;
    }

    public static Task ToggleTaskAsync(AppConfig cfg, string listId, string taskId, bool completed) =>
        PatchStatusAsync(cfg, listId, taskId, completed);

    public static async Task SetDescriptionAsync(AppConfig cfg, string listId, string taskId, string description)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return;
        var res = await Http.SendAsync(Authed(HttpMethod.Patch,
            $"https://www.googleapis.com/tasks/v1/lists/{listId}/tasks/{taskId}", token,
            new { notes = description }));
        await ThrowIfFailedAsync(res);
    }

    private static async Task PatchStatusAsync(AppConfig cfg, string listId, string taskId, bool completed)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return;
        await Http.SendAsync(Authed(HttpMethod.Patch, $"https://www.googleapis.com/tasks/v1/lists/{listId}/tasks/{taskId}", token,
            new { status = completed ? "completed" : "needsAction" }));
    }

    public static async Task DeleteTaskAsync(AppConfig cfg, string listId, string taskId)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return;
        await Http.SendAsync(Authed(HttpMethod.Delete, $"https://www.googleapis.com/tasks/v1/lists/{listId}/tasks/{taskId}", token));
    }

    /// date=null clears the due date. Google Tasks stores date only, no time of day.
    public static async Task SetDueDateAsync(AppConfig cfg, string listId, string taskId, DateTime? date)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return;
        var due = date?.ToString("yyyy-MM-ddT00:00:00.000Z");
        await Http.SendAsync(Authed(HttpMethod.Patch, $"https://www.googleapis.com/tasks/v1/lists/{listId}/tasks/{taskId}", token,
            new { due }));
    }
}
