using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NativeWidget.Services;

public sealed record TranslationMeaningGroup(string PartOfSpeech, IReadOnlyList<string> Meanings);

public sealed record TranslationResult(string SourceText, string TranslatedText,
    string SourceLanguage, string TargetLanguage,
    IReadOnlyList<TranslationMeaningGroup>? MeaningGroups = null,
    IReadOnlyList<string>? Examples = null);

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

        return new TranslationResult(text, translated.Trim(), detected, target,
            ParseMeaningGroups(root), ParseExamples(root));
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
                using var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client", "gtx"),
                    new KeyValuePair<string, string>("sl", source),
                    new KeyValuePair<string, string>("tl", target),
                    new KeyValuePair<string, string>("dt", "t"),
                    new KeyValuePair<string, string>("dt", "bd"),
                    new KeyValuePair<string, string>("dt", "ex"),
                    new KeyValuePair<string, string>("q", text),
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

    private static IReadOnlyList<TranslationMeaningGroup> ParseMeaningGroups(JsonElement root)
    {
        if (root.GetArrayLength() < 2 || root[1].ValueKind != JsonValueKind.Array)
            return Array.Empty<TranslationMeaningGroup>();

        var groups = new List<TranslationMeaningGroup>();
        foreach (var group in root[1].EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Array || group.GetArrayLength() < 2 ||
                group[0].ValueKind != JsonValueKind.String || group[1].ValueKind != JsonValueKind.Array) continue;
            var meanings = group[1].EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => (value.GetString() ?? "").Normalize(NormalizationForm.FormC).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12).ToList();
            if (meanings.Count > 0)
                groups.Add(new TranslationMeaningGroup(group[0].GetString() ?? "", meanings));
            if (groups.Count == 4) break;
        }
        return groups;
    }

    private static IReadOnlyList<string> ParseExamples(JsonElement root)
    {
        if (root.GetArrayLength() < 14 || root[13].ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var examples = new List<string>();
        CollectExamples(root[13], examples);
        return examples.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
    }

    private static void CollectExamples(JsonElement element, List<string> examples)
    {
        if (examples.Count >= 5 || element.ValueKind != JsonValueKind.Array) return;
        if (element.GetArrayLength() > 0 && element[0].ValueKind == JsonValueKind.String)
        {
            var sentence = (element[0].GetString() ?? "")
                .Replace("<b>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("</b>", "", StringComparison.OrdinalIgnoreCase)
                .Normalize(NormalizationForm.FormC).Trim();
            if (sentence.Length > 0) examples.Add(sentence);
            return;
        }
        foreach (var child in element.EnumerateArray()) CollectExamples(child, examples);
    }
}
