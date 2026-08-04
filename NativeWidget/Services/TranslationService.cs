using System.Net.Http;
using System.Text.Json;

namespace NativeWidget.Services;

public sealed record TranslationResult(string SourceText, string TranslatedText,
    string SourceLanguage, string TargetLanguage);

// Provider-specific behavior lives only here. The widget and popup depend on this single
// function, so replacing the free endpoint with Cloud Translation, DeepL, or an LLM does
// not ripple through input capture, OCR, or the UI.
public static class TranslationService
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 2;

    public static async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage,
        string targetLanguage, CancellationToken cancellationToken = default)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("Không có nội dung để dịch.", nameof(text));
        if (text.Length > 5000) text = text[..5000];

        var source = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage;
        var target = string.IsNullOrWhiteSpace(targetLanguage) ? "vi" : targetLanguage;
        using var response = await SendWithRetryAsync(text, source, target, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;

        var translated = string.Concat(root[0].EnumerateArray()
            .Where(segment => segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0)
            .Select(segment => segment[0].ValueKind == JsonValueKind.String ? segment[0].GetString() : null));
        var detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
            ? root[2].GetString() ?? source : source;
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("Dịch vụ không trả về bản dịch.");

        return new TranslationResult(text, translated.Trim(), detected, target);
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(string text, string source,
        string target, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client"] = "gtx",
                    ["sl"] = source,
                    ["tl"] = target,
                    ["dt"] = "t",
                    ["q"] = text,
                });
                var response = await Http.PostAsync(
                    "https://translate.googleapis.com/translate_a/single", form, timeout.Token);
                if (attempt < MaxAttempts && IsTransient(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(350, cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == MaxAttempts)
                    throw new TimeoutException("Dịch vụ phản hồi quá chậm. Hãy thử lại.");
                await Task.Delay(350, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(350, cancellationToken);
            }
        }

        throw new HttpRequestException("Không thể kết nối dịch vụ dịch. Hãy thử lại.");
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode) =>
        statusCode is System.Net.HttpStatusCode.RequestTimeout or
            System.Net.HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
}
