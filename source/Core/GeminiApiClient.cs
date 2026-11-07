using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PressRelease;

public sealed class GeminiApiException(string message, bool keyRejected = false) : Exception(message)
{
    public bool KeyRejected { get; } = keyRejected;
}

public sealed class GeminiApiClient(HttpClient http)
{
    public const string Model = "gemini-2.5-flash";
    const string ModelUrl = "https://generativelanguage.googleapis.com/v1beta/models/" + Model;
    const int MaxResponseBytes = 4 * 1024 * 1024;
    const int MaxErrorBytes = 32 * 1024;

    internal static GeminiApiClient Shared { get; } = new(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan });

    public async Task CheckAsync(string? apiKey, CancellationToken cancel)
    {
        using var response = await SendAsync(apiKey, prompt: null, cancel).ConfigureAwait(false);
        var methods = Get(response.RootElement, "supportedGenerationMethods");
        if (methods.ValueKind != JsonValueKind.Array || !methods.EnumerateArray().Any(method => Text(method) == "generateContent"))
            throw new GeminiApiException("이 프로젝트에서 Gemini 2.5 Flash의 텍스트 생성을 사용할 수 없습니다.");
    }

    public async Task<string> CompleteAsync(string? apiKey, string prompt, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Gemini에 보낼 텍스트를 입력해 주세요.", nameof(prompt));
        using var response = await SendAsync(apiKey, prompt, cancel).ConfigureAwait(false);
        var root = response.RootElement;
        var blockReason = Text(Get(Get(root, "promptFeedback"), "blockReason"));
        if (!string.IsNullOrEmpty(blockReason) && blockReason != "BLOCK_REASON_UNSPECIFIED")
            throw new GeminiApiException("Gemini의 콘텐츠 정책에 따라 요청이 차단됐습니다. 입력 내용을 확인해 주세요.");

        var candidates = Get(root, "candidates");
        if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            throw new GeminiApiException("Gemini API가 텍스트 응답을 반환하지 않았습니다.");

        var candidate = candidates[0];
        switch (Text(Get(candidate, "finishReason")))
        {
            case "STOP": break;
            case "MAX_TOKENS": throw new GeminiApiException("출력 한도에 도달해 Gemini 응답이 잘렸습니다. 입력을 줄이고 다시 시도해 주세요.");
            case "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "MODEL_ARMOR":
                throw new GeminiApiException("Gemini의 콘텐츠 정책에 따라 응답이 차단됐습니다. 입력 내용을 확인해 주세요.");
            default: throw new GeminiApiException("Gemini API가 텍스트 생성을 완료하지 못했습니다.");
        }

        var parts = Get(Get(candidate, "content"), "parts");
        if (parts.ValueKind != JsonValueKind.Array)
            throw new GeminiApiException("Gemini API가 텍스트 응답을 반환하지 않았습니다.");

        var output = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (Get(part, "thought").ValueKind != JsonValueKind.True) output.Append(Text(Get(part, "text")));
        }
        var text = output.ToString();
        if (string.IsNullOrWhiteSpace(text)) throw new GeminiApiException("Gemini API가 텍스트 응답을 반환하지 않았습니다.");
        return text;
    }

    async Task<JsonDocument> SendAsync(string? apiKey, string? prompt, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var key = apiKey?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new GeminiApiException("AI 연결에서 Gemini API 키를 입력해 주세요.", keyRejected: true);
        if (key.Length > 1024 || key.Any(character => character < '!' || character > '~'))
            throw new GeminiApiException("Gemini API 키 형식을 확인해 주세요.", keyRejected: true);

        using var request = prompt == null
            ? new HttpRequestMessage(HttpMethod.Get, ModelUrl)
            : new HttpRequestMessage(HttpMethod.Post, ModelUrl + ":generateContent")
            {
                Content = JsonContent.Create(new
                {
                    contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
                    generationConfig = new { maxOutputTokens = 8192 }
                })
            };
        request.Headers.Add("x-goog-api-key", key);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromSeconds(prompt == null ? 20 : 120));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await CreateFailureAsync(response, limit.Token).ConfigureAwait(false);
            return await ReadJsonAsync(response.Content, MaxResponseBytes, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("Gemini API 응답 시간이 초과됐습니다. 네트워크 상태를 확인한 뒤 다시 시도해 주세요.");
        }
        catch (HttpRequestException)
        {
            throw new GeminiApiException("Gemini API 요청을 완료하지 못했습니다. 네트워크 또는 응답 크기를 확인해 주세요.");
        }
        catch (IOException)
        {
            throw new GeminiApiException("Gemini API 응답을 읽는 중 연결이 끊겼습니다.");
        }
        catch (JsonException)
        {
            throw new GeminiApiException("Gemini API 응답 형식을 확인할 수 없습니다.");
        }
    }

    static async Task<GeminiApiException> CreateFailureAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        // Only a known reason code is inspected. Server text, URLs, and headers are never passed into errors.
        if (response.StatusCode == HttpStatusCode.BadRequest && await HasKeyErrorAsync(response.Content, cancel).ConfigureAwait(false))
            return new("Gemini API 키가 거부됐습니다. Google AI Studio에서 키를 확인해 주세요.", keyRejected: true);

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new("Gemini API 키가 거부됐습니다. Google AI Studio에서 키를 확인해 주세요.", keyRejected: true),
            HttpStatusCode.Forbidden => new("Gemini API 접근이 거부됐습니다. API 키의 제한과 프로젝트 권한을 확인해 주세요.", keyRejected: true),
            HttpStatusCode.TooManyRequests => new("Gemini API 사용 한도에 도달했습니다. Google AI Studio에서 한도를 확인해 주세요."),
            HttpStatusCode.NotFound => new("이 프로젝트에서 Gemini 2.5 Flash 모델을 찾을 수 없습니다."),
            HttpStatusCode.BadRequest => new("Gemini API 요청이 거부됐습니다. API 키, 프로젝트 설정 및 이용 가능 지역을 확인해 주세요."),
            _ when (int)response.StatusCode >= 500 => new("Gemini API 서비스에 일시적인 오류가 발생했습니다. 잠시 후 다시 시도해 주세요."),
            _ => new("Gemini API가 요청을 처리하지 못했습니다.")
        };
    }

    static async Task<bool> HasKeyErrorAsync(HttpContent content, CancellationToken cancel)
    {
        try
        {
            using var document = await ReadJsonAsync(content, MaxErrorBytes, cancel).ConfigureAwait(false);
            var details = Get(Get(document.RootElement, "error"), "details");
            return details.ValueKind == JsonValueKind.Array && details.EnumerateArray().Any(detail =>
                Text(Get(detail, "@type")) == "type.googleapis.com/google.rpc.ErrorInfo" &&
                Text(Get(detail, "reason"))?.StartsWith("API_KEY_", StringComparison.Ordinal) == true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { return false; }
    }

    static async Task<JsonDocument> ReadJsonAsync(HttpContent content, int maxBytes, CancellationToken cancel)
    {
        await content.LoadIntoBufferAsync(maxBytes, cancel).ConfigureAwait(false);
        await using var stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancel).ConfigureAwait(false);
    }

    static JsonElement Get(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) ? property : default;

    static string? Text(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
