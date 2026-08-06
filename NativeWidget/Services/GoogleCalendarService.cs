using System.IO;
using System.Net;
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

public sealed class GoogleCalendarTransientException : Exception
{
    public GoogleCalendarTransientException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public sealed class GoogleCalendarAuthenticationException : Exception
{
    public GoogleCalendarAuthenticationException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public sealed class GoogleCalendarRequestException : Exception
{
    public GoogleCalendarRequestException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public static class GoogleCalendarService
{
    private static readonly GoogleCalendarApiClient Client = new();

    internal static string TokenFile => AppConfig.TokenPath("google-token.json");

    public static bool IsConnected() => File.Exists(TokenFile);

    public static void Disconnect()
    {
        if (File.Exists(TokenFile)) File.Delete(TokenFile);
    }

    public static Task ConnectAsync(AppConfig cfg, CancellationToken cancellationToken = default) =>
        Client.ConnectAsync(cfg, cancellationToken);

    public static Task CreateEventAsync(AppConfig cfg, string title, DateTime start, bool allDay,
        string? recurrenceFreq = null, string? description = null,
        CancellationToken cancellationToken = default) =>
        Client.CreateEventAsync(cfg, title, start, allDay, recurrenceFreq, description, cancellationToken);

    public static Task DeleteEventAsync(AppConfig cfg, string eventId,
        CancellationToken cancellationToken = default) =>
        Client.DeleteEventAsync(cfg, eventId, cancellationToken);

    public static Task<List<CalendarEvent>> GetUpcomingEventsAsync(AppConfig cfg,
        CancellationToken cancellationToken = default) =>
        Client.GetUpcomingEventsAsync(cfg, cancellationToken);
}

internal sealed class GoogleCalendarApiClient
{
    private const int Port = 42813;
    private static readonly string RedirectUri = $"http://127.0.0.1:{Port}/callback";
    private const string Scope = "https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/tasks";
    private const int MaxAttempts = 3;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<int, int> _jitterMilliseconds;

    public GoogleCalendarApiClient(HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<int, int>? jitterMilliseconds = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        _delayAsync = delayAsync ?? Task.Delay;
        _jitterMilliseconds = jitterMilliseconds ?? Random.Shared.Next;
    }

    public async Task ConnectAsync(AppConfig cfg, CancellationToken cancellationToken)
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
        cancellationToken.ThrowIfCancellationRequested();

        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = cfg.GoogleClientId,
                ["client_secret"] = cfg.GoogleClientSecret,
                ["code"] = code,
                ["code_verifier"] = pkce.Verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = RedirectUri,
            });
            return request;
        }, cancellationToken);
        ThrowForFailure(response, "Google sign-in", authenticationExchange: true);

        var json = await ReadJsonAsync(response, cancellationToken, authenticationResponse: true);
        if (!json.TryGetProperty("access_token", out var accessToken) || accessToken.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessToken.GetString()) ||
            !json.TryGetProperty("expires_in", out var expiresIn) || !expiresIn.TryGetInt64(out var expiresInSeconds))
            throw new GoogleCalendarAuthenticationException("Google sign-in returned invalid credentials. Reconnect Google Calendar.");

        var tokens = new GoogleTokens
        {
            AccessToken = accessToken.GetString()!,
            RefreshToken = json.TryGetProperty("refresh_token", out var refreshToken) &&
                           refreshToken.ValueKind == JsonValueKind.String ? refreshToken.GetString() : null,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresInSeconds * 1000,
        };
        AppConfig.EnsureFolder();
        File.WriteAllText(GoogleCalendarService.TokenFile, JsonSerializer.Serialize(tokens));
    }

    private async Task<string?> GetValidAccessTokenAsync(AppConfig cfg, CancellationToken cancellationToken)
    {
        if (!File.Exists(GoogleCalendarService.TokenFile)) return null;

        GoogleTokens tokens;
        try
        {
            tokens = JsonSerializer.Deserialize<GoogleTokens>(File.ReadAllText(GoogleCalendarService.TokenFile))
                     ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new GoogleCalendarAuthenticationException("Saved Google credentials are invalid. Reconnect Google Calendar.", ex);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now < tokens.ExpiresAt - 60000 && !string.IsNullOrWhiteSpace(tokens.AccessToken))
            return tokens.AccessToken;
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new GoogleCalendarAuthenticationException("Google authorization expired. Reconnect Google Calendar.");

        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = cfg.GoogleClientId,
                ["client_secret"] = cfg.GoogleClientSecret,
                ["refresh_token"] = tokens.RefreshToken!,
                ["grant_type"] = "refresh_token",
            });
            return request;
        }, cancellationToken);
        ThrowForFailure(response, "Google authorization", authenticationExchange: true);

        var json = await ReadJsonAsync(response, cancellationToken, authenticationResponse: true);
        if (!json.TryGetProperty("access_token", out var accessToken) || accessToken.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessToken.GetString()) ||
            !json.TryGetProperty("expires_in", out var expiresIn) || !expiresIn.TryGetInt64(out var expiresInSeconds))
            throw new GoogleCalendarAuthenticationException("Google authorization expired. Reconnect Google Calendar.");

        tokens.AccessToken = accessToken.GetString()!;
        tokens.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresInSeconds * 1000;
        File.WriteAllText(GoogleCalendarService.TokenFile, JsonSerializer.Serialize(tokens));
        return tokens.AccessToken;
    }

    public async Task CreateEventAsync(AppConfig cfg, string title, DateTime start, bool allDay,
        string? recurrenceFreq, string? description, CancellationToken cancellationToken)
    {
        var token = await GetValidAccessTokenAsync(cfg, cancellationToken);
        if (token == null) return;

        string startField;
        object startValue;
        object endValue;
        if (allDay)
        {
            startField = "date";
            startValue = start.ToString("yyyy-MM-dd");
            endValue = start.AddDays(1).ToString("yyyy-MM-dd");
        }
        else
        {
            startField = "dateTime";
            var startOffset = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Local));
            startValue = startOffset.ToString("yyyy-MM-ddTHH:mm:sszzz");
            endValue = startOffset.AddHours(1).ToString("yyyy-MM-ddTHH:mm:sszzz");
        }

        var body = new Dictionary<string, object>
        {
            ["summary"] = title,
            ["start"] = new Dictionary<string, object> { [startField] = startValue },
            ["end"] = new Dictionary<string, object> { [startField] = endValue },
        };
        if (recurrenceFreq != null) body["recurrence"] = new[] { $"RRULE:FREQ={recurrenceFreq}" };
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description.Trim();

        // Event insertion is not retried automatically: a dropped response after Google
        // accepted the POST could otherwise create a duplicate event.
        using var response = await SendWithRetryAsync(() => Authed(HttpMethod.Post,
            "https://www.googleapis.com/calendar/v3/calendars/primary/events", token, body),
            cancellationToken, maxAttempts: 1);
        ThrowForFailure(response, "Create calendar event");
    }

    public async Task DeleteEventAsync(AppConfig cfg, string eventId, CancellationToken cancellationToken)
    {
        var token = await GetValidAccessTokenAsync(cfg, cancellationToken);
        if (token == null) return;
        using var response = await SendWithRetryAsync(() => Authed(HttpMethod.Delete,
            $"https://www.googleapis.com/calendar/v3/calendars/primary/events/{Uri.EscapeDataString(eventId)}", token),
            cancellationToken);
        ThrowForFailure(response, "Delete calendar event");
    }

    public async Task<List<CalendarEvent>> GetUpcomingEventsAsync(AppConfig cfg, CancellationToken cancellationToken)
    {
        var token = await GetValidAccessTokenAsync(cfg, cancellationToken);
        if (token == null) return new List<CalendarEvent>();

        var now = Uri.EscapeDataString(DateTime.UtcNow.ToString("o"));
        var max = Uri.EscapeDataString(DateTime.UtcNow.AddDays(14).ToString("o"));
        var url = "https://www.googleapis.com/calendar/v3/calendars/primary/events" +
                  $"?timeMin={now}&timeMax={max}&maxResults=100&orderBy=startTime&singleEvents=true";

        using var response = await SendWithRetryAsync(() => Authed(HttpMethod.Get, url, token), cancellationToken);
        ThrowForFailure(response, "Refresh Google Calendar");
        var json = await ReadJsonAsync(response, cancellationToken, authenticationResponse: false);

        var events = new List<CalendarEvent>();
        if (!json.TryGetProperty("items", out var items)) return events;
        if (items.ValueKind != JsonValueKind.Array)
            throw new GoogleCalendarRequestException("Google Calendar returned an invalid response.");

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object) continue;
            var hasDateTime = start.TryGetProperty("dateTime", out var dateTime);
            var startText = hasDateTime
                ? dateTime.GetString()
                : start.TryGetProperty("date", out var date) ? date.GetString() : null;
            if (string.IsNullOrWhiteSpace(startText)) continue;
            events.Add(new CalendarEvent
            {
                Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Title = item.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "(No title)" : "(No title)",
                Start = startText,
                AllDay = !hasDateTime,
                Link = item.TryGetProperty("htmlLink", out var link) ? link.GetString() ?? "" : "",
                Description = item.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
            });
        }
        return events;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken, int maxAttempts = MaxAttempts)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            try
            {
                using var request = requestFactory();
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!IsTransient(response.StatusCode)) return response;

                if (attempt == maxAttempts)
                {
                    var statusCode = response.StatusCode;
                    response.Dispose();
                    throw new GoogleCalendarTransientException(
                        $"Google Calendar is temporarily unavailable (HTTP {(int)statusCode}).");
                }

                response.Dispose();
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == maxAttempts)
                    throw new GoogleCalendarTransientException("Google Calendar timed out. Try again when the connection is available.");
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == maxAttempts)
                    throw new GoogleCalendarTransientException("Google Calendar is offline. Try again when the connection is available.", ex);
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
        }

        throw new GoogleCalendarTransientException("Google Calendar is temporarily unavailable.");
    }

    private Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var exponentialMilliseconds = 250 * (1 << Math.Min(attempt - 1, 3));
        var jitter = _jitterMilliseconds(125);
        return _delayAsync(TimeSpan.FromMilliseconds(exponentialMilliseconds + jitter), cancellationToken);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static void ThrowForFailure(HttpResponseMessage response, string operation,
        bool authenticationExchange = false)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            authenticationExchange && response.StatusCode == HttpStatusCode.BadRequest)
            throw new GoogleCalendarAuthenticationException("Google authorization is invalid or expired. Reconnect Google Calendar.");

        throw new GoogleCalendarRequestException($"{operation} failed (HTTP {(int)response.StatusCode}).");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response,
        CancellationToken cancellationToken, bool authenticationResponse)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            if (authenticationResponse)
                throw new GoogleCalendarAuthenticationException("Google returned an invalid authorization response. Reconnect Google Calendar.", ex);
            throw new GoogleCalendarRequestException("Google Calendar returned an invalid response.", ex);
        }
    }
}
