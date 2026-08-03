using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NativeWidget.Models;

namespace NativeWidget.Services;

public class GoogleTokens
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
}

public class CalendarEvent
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Start { get; set; } = "";
    public bool AllDay { get; set; }
    public string Link { get; set; } = "";
    public string Description { get; set; } = "";
}

public static class GoogleCalendarService
{
    private const int Port = 42813;
    private static readonly string RedirectUri = $"http://127.0.0.1:{Port}/callback";
    // Requests Tasks scope too so a single Connect covers both widgets - Google Tasks
    // shares this same token file (see GoogleTasksService). calendar.events (not the
    // broader calendar.readonly-only) is needed to create/update events, not just view them.
    private const string Scope = "https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/tasks";
    private static readonly HttpClient Http = new();

    private static string TokenFile => AppConfig.TokenPath("google-token.json");

    public static bool IsConnected() => File.Exists(TokenFile);

    public static void Disconnect()
    {
        if (File.Exists(TokenFile)) File.Delete(TokenFile);
    }

    public static async Task ConnectAsync(AppConfig cfg)
    {
        var pkce = OAuthHelper.MakePkce();
        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" +
            $"client_id={Uri.EscapeDataString(cfg.GoogleClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&response_type=code" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            "&access_type=offline&prompt=consent" +
            $"&code_challenge={pkce.Challenge}&code_challenge_method=S256";

        var code = await OAuthHelper.WaitForAuthCodeAsync(authUrl, Port);

        var form = new Dictionary<string, string>
        {
            ["client_id"] = cfg.GoogleClientId,
            ["client_secret"] = cfg.GoogleClientSecret,
            ["code"] = code,
            ["code_verifier"] = pkce.Verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = RedirectUri,
        };

        var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        var tokens = new GoogleTokens
        {
            AccessToken = json.GetProperty("access_token").GetString()!,
            RefreshToken = json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + json.GetProperty("expires_in").GetInt64() * 1000,
        };
        AppConfig.EnsureFolder();
        File.WriteAllText(TokenFile, JsonSerializer.Serialize(tokens));
    }

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

    /// allDay=true creates a whole-day event (Google wants a plain date, no time/timezone);
    /// otherwise `start` is treated as local time and the event runs 1 hour.
    /// recurrenceFreq, when given ("DAILY"/"WEEKLY"/"MONTHLY"), repeats the event forever -
    /// matches what the class-schedule events use (RRULE:FREQ=WEEKLY with no UNTIL/COUNT).
    public static async Task CreateEventAsync(AppConfig cfg, string title, DateTime start, bool allDay,
        string? recurrenceFreq = null, string? description = null)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return;

        string startField, endField;
        object startVal, endVal;
        if (allDay)
        {
            startField = endField = "date";
            startVal = start.ToString("yyyy-MM-dd");
            endVal = start.AddDays(1).ToString("yyyy-MM-dd");
        }
        else
        {
            var startOffset = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Local));
            startField = endField = "dateTime";
            startVal = startOffset.ToString("yyyy-MM-ddTHH:mm:sszzz");
            endVal = startOffset.AddHours(1).ToString("yyyy-MM-ddTHH:mm:sszzz");
        }

        var bodyDict = new Dictionary<string, object>
        {
            ["summary"] = title,
            ["start"] = new Dictionary<string, object> { [startField] = startVal },
            ["end"] = new Dictionary<string, object> { [endField] = endVal },
        };
        if (recurrenceFreq != null)
            bodyDict["recurrence"] = new[] { $"RRULE:FREQ={recurrenceFreq}" };
        if (!string.IsNullOrWhiteSpace(description)) bodyDict["description"] = description.Trim();

        var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/calendar/v3/calendars/primary/events");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(bodyDict);
        var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync();
            var detail = text;
            try
            {
                var err = JsonSerializer.Deserialize<JsonElement>(text);
                if (err.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                    detail = m.GetString() ?? text;
            }
            catch { }
            throw new HttpRequestException($"Google Calendar {(int)res.StatusCode}: {detail}");
        }
    }

    public static async Task DeleteEventAsync(AppConfig cfg, string eventId)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return;
        var req = new HttpRequestMessage(HttpMethod.Delete, $"https://www.googleapis.com/calendar/v3/calendars/primary/events/{eventId}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        await Http.SendAsync(req);
    }

    public static async Task<List<CalendarEvent>> GetUpcomingEventsAsync(AppConfig cfg)
    {
        var token = await GetValidAccessTokenAsync(cfg);
        if (token == null) return new List<CalendarEvent>();

        var now = Uri.EscapeDataString(DateTime.UtcNow.ToString("o"));
        var max = Uri.EscapeDataString(DateTime.UtcNow.AddDays(14).ToString("o"));
        var url = "https://www.googleapis.com/calendar/v3/calendars/primary/events" +
                  $"?timeMin={now}&timeMax={max}&maxResults=100&orderBy=startTime&singleEvents=true";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var res = await Http.SendAsync(req);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        var events = new List<CalendarEvent>();
        if (json.TryGetProperty("items", out var items))
        {
            foreach (var e in items.EnumerateArray())
            {
                var start = e.GetProperty("start");
                var hasDateTime = start.TryGetProperty("dateTime", out var dt);
                events.Add(new CalendarEvent
                {
                    Id = e.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
                    Title = e.TryGetProperty("summary", out var s) ? s.GetString() ?? "(Không tiêu đề)" : "(Không tiêu đề)",
                    Start = hasDateTime ? dt.GetString()! : start.GetProperty("date").GetString()!,
                    AllDay = !hasDateTime,
                    Link = e.TryGetProperty("htmlLink", out var l) ? l.GetString() ?? "" : "",
                    Description = e.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
                });
            }
        }
        return events;
    }
}
