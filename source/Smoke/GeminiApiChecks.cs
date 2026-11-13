using System.Net;
using System.Text;
using System.Text.Json;
using PressRelease;

static class GeminiApiChecks
{
    const string FakeKey = "test-api-key-do-not-send";
    const string PrivateMarker = "synthetic-private-value";
    const string ModelPath = "/v1beta/models/" + GeminiApiClient.Model;

    public static async Task RunAsync(Action<bool, string> require)
    {
        using (var metadataHttp = new HttpClient(new StubHandler((request, _) =>
        {
            RequireDestination(request, require);
            require(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == ModelPath && request.Content == null,
                "Gemini API key check reads model metadata without a prompt");
            return Task.FromResult(Json(HttpStatusCode.OK, """{"supportedGenerationMethods":["generateContent"]}"""));
        })))
        {
            await new GeminiApiClient(metadataHttp).CheckAsync(FakeKey, default);
        }

        const string prompt = "합성 보도자료 원고입니다.";
        using (var generateHttp = new HttpClient(new StubHandler(async (request, cancel) =>
        {
            RequireDestination(request, require);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancel));
            var root = body.RootElement;
            var contents = root.GetProperty("contents");
            require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == ModelPath + ":generateContent" &&
                contents.GetArrayLength() == 1 && contents[0].GetProperty("role").GetString() == "user" &&
                contents[0].GetProperty("parts")[0].GetProperty("text").GetString() == prompt &&
                !root.TryGetProperty("tools", out _) && !root.GetRawText().Contains(FakeKey),
                "Gemini API generation sends plain prompt text without tools or a key in the body");
            var generation = root.GetProperty("generationConfig");
            require(GeminiApiClient.Model != "gemini-2.5-flash" && generation.GetProperty("maxOutputTokens").GetInt32() >= 16384 &&
                generation.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString() == "low",
                "Gemini API uses a model open to new keys and keeps thinking from exhausting the output limit");
            return Json(HttpStatusCode.OK, """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"hidden","thought":true},{"text":"보도자료"},{"text":" 초안"}]}}]}""");
        })))
        {
            var result = await new GeminiApiClient(generateHttp).CompleteAsync(FakeKey, prompt, default);
            require(result == "보도자료 초안", "Gemini API combines visible text and excludes thought parts");
        }

        var badKey = await GetFailureAsync(HttpStatusCode.BadRequest, $$$"""
            {"error":{"message":"{{{FakeKey}}} {{{PrivateMarker}}}","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID"}]}}
            """);
        require(badKey.KeyRejected && !badKey.ToString().Contains(FakeKey) && !badKey.ToString().Contains(PrivateMarker),
            "Gemini API recognizes invalid keys without exposing server text or credentials");

        var throttled = await GetFailureAsync(HttpStatusCode.TooManyRequests, $$$"""{"error":{"message":"{{{PrivateMarker}}}"}}""");
        require(!throttled.KeyRejected && throttled.Message.Contains("한도") && !throttled.ToString().Contains(PrivateMarker),
            "Gemini API distinguishes quota errors from authentication errors");

        var blocked = await GetFailureAsync(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"SAFETY"}}""");
        var truncated = await GetFailureAsync(HttpStatusCode.OK, """{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"불완전한 원고"}]}}]}""");
        var noText = await GetFailureAsync(HttpStatusCode.OK, """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"thought":true,"text":"hidden"}]}}]}""");
        require(blocked.Message.Contains("차단") && truncated.Message.Contains("잘렸") && noText.Message.Contains("텍스트 응답"),
            "Gemini API rejects blocked, truncated, and non-text output");

        using var cancelled = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pendingHttp = new HttpClient(new StubHandler(async (_, cancel) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancel);
            throw new InvalidOperationException("Expected cancellation");
        }));
        var operation = new GeminiApiClient(pendingHttp).CheckAsync(FakeKey, cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        var wasCancelled = false;
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { wasCancelled = true; }
        require(wasCancelled, "Gemini API propagates cancellation during a request");
    }

    static void RequireDestination(HttpRequestMessage request, Action<bool, string> require)
    {
        var uri = request.RequestUri!;
        require(uri.Scheme == "https" && uri.Host == "generativelanguage.googleapis.com" && uri.Query == "" &&
            request.Headers.GetValues("x-goog-api-key").SequenceEqual([FakeKey]), "Gemini API sends key only in the header to Google's HTTPS endpoint");
    }

    static async Task<GeminiApiException> GetFailureAsync(HttpStatusCode status, string body)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(status, body))));
        try { _ = await new GeminiApiClient(http).CompleteAsync(FakeKey, "합성 원고", default); }
        catch (GeminiApiException ex) { return ex; }
        throw new InvalidOperationException("Gemini API unexpectedly accepted an invalid response.");
    }

    static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
            => send(request, cancel);
    }
}
