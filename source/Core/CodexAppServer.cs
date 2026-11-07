using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PressRelease;

public sealed record CodexAccountInfo(string? AuthMode, string? PlanType)
{
    public bool IsChatGpt => AuthMode == "chatgpt";
    public bool IsAuthenticated => !string.IsNullOrEmpty(AuthMode);
}

// The URL and one-time code are transient UI values. Never persist or log this record.
public sealed record CodexLoginChallenge(Uri VerificationUri, string? UserCode, bool DeviceCode);

/// <summary>Account-only client for the official Codex app-server JSONL protocol.</summary>
public sealed class CodexAppServer : IAsyncDisposable
{
    readonly Func<ProcessStartInfo> startInfoFactory;
    readonly TimeSpan requestTimeout;
    readonly TimeSpan loginTimeout;
    readonly SemaphoreSlim startGate = new(1, 1);
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly SemaphoreSlim loginGate = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    readonly object sync = new();
    Process? process;
    Task? reader;
    Task? errorReader;
    Exception? transportError;
    LoginAttempt? activeLogin;
    long nextId;
    bool initialized;
    int disposed;

    public event Action<CodexAccountInfo>? AccountChanged;

    public CodexAppServer(Func<ProcessStartInfo> startInfoFactory,
        TimeSpan? requestTimeout = null, TimeSpan? loginTimeout = null)
    {
        this.startInfoFactory = startInfoFactory;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        this.loginTimeout = loginTimeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<CodexAccountInfo> ReadAccountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await RequestAsync("account/read", new { refreshToken = false }, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("account", out var account)) throw ProtocolError();
        CodexAccountInfo info;
        if (account.ValueKind == JsonValueKind.Null) info = new(null, null);
        else
        {
            var mode = StringProperty(account, "type") ?? throw ProtocolError();
            info = new(mode == "apiKey" ? "apikey" : mode, StringProperty(account, "planType"));
        }
        PublishAccount(info);
        return info;
    }

    public async Task<CodexAccountInfo> LoginAsync(bool deviceCode,
        Func<CodexLoginChallenge, Task> onChallenge, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!await loginGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("이미 ChatGPT 로그인 중입니다. 완료하거나 취소해 주세요.");
        LoginAttempt? attempt = null;
        bool startRequested = false;
        bool completed = false;
        using var loginLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        loginLimit.CancelAfter(loginTimeout);
        try
        {
            await EnsureStartedAsync(loginLimit.Token).ConfigureAwait(false);
            attempt = new();
            lock (sync) activeLogin = attempt;
            loginLimit.Token.ThrowIfCancellationRequested();
            startRequested = true;
            // Keep the request alive briefly after cancellation to recover the exact loginId.
            // If the server cannot return it, terminate only our server instead of waiting indefinitely.
            var startTask = RequestAsync("account/login/start",
                new { type = deviceCode ? "chatgptDeviceCode" : "chatgpt" }, lifetime.Token);
            JsonElement response;
            try { response = await startTask.WaitAsync(loginLimit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try
                {
                    var cancelledResponse = await startTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    lock (sync) attempt.Id = StringProperty(cancelledResponse, "loginId");
                }
                catch (Exception)
                {
                    await StopTransportAsync().ConfigureAwait(false);
                    try { await startTask.ConfigureAwait(false); } catch (Exception) { }
                }
                throw;
            }
            var loginId = StringProperty(response, "loginId");
            if (string.IsNullOrWhiteSpace(loginId)) throw ProtocolError();
            lock (sync)
            {
                attempt.Id = loginId;
                if (attempt.EarlyCompletions.TryGetValue(loginId, out var success))
                    attempt.Completion.TrySetResult(success);
                attempt.EarlyCompletions.Clear();
            }
            loginLimit.Token.ThrowIfCancellationRequested();
            var url = StringProperty(response, deviceCode ? "verificationUrl" : "authUrl");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsOfficialLoginUri(uri))
                throw new InvalidDataException("Codex가 유효한 공식 로그인 주소를 반환하지 않았습니다.");
            var code = deviceCode ? StringProperty(response, "userCode") : null;
            if (deviceCode && string.IsNullOrWhiteSpace(code)) throw ProtocolError();
            if (!attempt.Completion.Task.IsCompleted)
                await onChallenge(new(uri, code, deviceCode)).WaitAsync(loginLimit.Token).ConfigureAwait(false);
            var successResult = await attempt.Completion.Task.WaitAsync(loginLimit.Token).ConfigureAwait(false);
            completed = true;
            if (!successResult)
                throw new InvalidOperationException("ChatGPT 로그인이 완료되지 않았습니다. 공식 로그인 화면의 안내를 확인한 뒤 다시 연결해 주세요.");
            var account = await ReadAccountAsync(loginLimit.Token).ConfigureAwait(false);
            if (!account.IsChatGpt)
                throw new InvalidOperationException("ChatGPT 계정 상태가 확인되지 않았습니다. 연결 상태를 다시 확인해 주세요.");
            return account;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("ChatGPT 로그인 시간이 초과되었습니다. 다시 연결해 주세요.");
        }
        finally
        {
            if (startRequested && !completed)
                await CancelAttemptAsync(attempt?.Id).ConfigureAwait(false);
            lock (sync) if (activeLogin == attempt) activeLogin = null;
            loginGate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (!await loginGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("로그인 중에는 연결을 해제할 수 없습니다. 먼저 로그인을 취소해 주세요.");
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await RequestAsync("account/logout", null, cancellationToken).ConfigureAwait(false);
            await ReadAccountAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { loginGate.Release(); }
    }

    async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            ThrowIfFailed();
            if (initialized) return;
            var info = startInfoFactory();
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            info.StandardInputEncoding = new UTF8Encoding(false);
            info.StandardOutputEncoding = info.StandardErrorEncoding = Encoding.UTF8;
            process = Process.Start(info) ?? throw new InvalidOperationException("Codex app-server를 시작하지 못했습니다.");
            reader = ReadMessagesAsync(process);
            errorReader = DrainErrorsAsync(process);
            await RequestAsync("initialize", new
            {
                clientInfo = new { name = "press_release_writer", title = "인천교육 보도자료 작성봇", version = "0.1.1" }
            }, cancellationToken).ConfigureAwait(false);
            await WriteAsync(new { method = "initialized" }, cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        catch
        {
            await StopTransportAsync().ConfigureAwait(false);
            throw;
        }
        finally { startGate.Release(); }
    }

    async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailed();
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            ThrowIfFailed();
            await WriteAsync(new { id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(requestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Codex 계정 서비스의 응답 시간이 초과되었습니다. 연결 상태와 CLI 버전을 확인해 주세요.");
        }
        finally { pending.TryRemove(id, out _); }
    }

    async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFailed();
            if (process == null) throw new InvalidOperationException("Codex 계정 서비스가 시작되지 않았습니다.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new IOException("Codex 계정 서비스와의 연결이 종료되었습니다. 다시 연결해 주세요.");
        }
        finally { writeGate.Release(); }
    }

    async Task ReadMessagesAsync(Process child)
    {
        try
        {
            while (await child.StandardOutput.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 1_048_576) throw ProtocolError();
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object) throw ProtocolError();
                if (message.TryGetProperty("method", out var method))
                {
                    if (message.TryGetProperty("id", out var requestId))
                    {
                        // This client only manages accounts; it never approves tools, permissions, or token requests.
                        await WriteAsync(new { id = requestId.Clone(), error = new { code = -32601, message = "Unsupported method" } }, lifetime.Token).ConfigureAwait(false);
                    }
                    else if (message.TryGetProperty("params", out var parameters))
                        HandleNotification(method.GetString(), parameters);
                }
                else if (message.TryGetProperty("id", out var responseId) && responseId.TryGetInt64(out var id)
                    && pending.TryGetValue(id, out var completion))
                {
                    if (message.TryGetProperty("error", out var error))
                    {
                        var unsupported = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.TryGetInt32(out var number) && number == -32601;
                        completion.TrySetException(new InvalidOperationException(unsupported
                            ? "이 Codex 버전은 필요한 계정 기능을 지원하지 않습니다. 공식 CLI를 업데이트해 주세요."
                            : "Codex 계정 요청에 실패했습니다. 공식 로그인 안내와 계정 권한·네트워크를 확인해 주세요."));
                    }
                    else if (message.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                    else completion.TrySetException(ProtocolError());
                }
            }
            FailTransport(new IOException("Codex 계정 서비스가 종료되었습니다. 다시 연결해 주세요."));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            FailTransport(ProtocolError());
        }
    }

    async Task DrainErrorsAsync(Process child)
    {
        // Authentication diagnostics can contain login URLs and tokens. Drain without retaining them.
        var buffer = new char[2048];
        try { while (await child.StandardError.ReadAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false) != 0) { } }
        catch (Exception) when (lifetime.IsCancellationRequested || Volatile.Read(ref disposed) != 0) { }
        catch (IOException) { }
    }

    void HandleNotification(string? method, JsonElement parameters)
    {
        if (method == "account/updated")
            PublishAccount(new(StringProperty(parameters, "authMode"), StringProperty(parameters, "planType")));
        else if (method == "account/login/completed")
        {
            var id = StringProperty(parameters, "loginId");
            if (id == null || !parameters.TryGetProperty("success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
            lock (sync)
            {
                if (activeLogin == null) return;
                if (activeLogin.Id == id) activeLogin.Completion.TrySetResult(success.GetBoolean());
                else if (activeLogin.Id == null && activeLogin.EarlyCompletions.Count < 8)
                    activeLogin.EarlyCompletions[id] = success.GetBoolean();
            }
        }
    }

    async Task CancelAttemptAsync(string? loginId)
    {
        if (loginId != null && !lifetime.IsCancellationRequested && Volatile.Read(ref transportError) == null)
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await RequestAsync("account/login/cancel", new { loginId }, limit.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception) { /* Stop only the server we own if cancellation cannot be acknowledged. */ }
        }
        await StopTransportAsync().ConfigureAwait(false);
    }

    void PublishAccount(CodexAccountInfo account)
    {
        if (AccountChanged is not { } handlers) return;
        foreach (Action<CodexAccountInfo> handler in handlers.GetInvocationList())
            try { handler(account); } catch (Exception) { /* UI callbacks must not stop the protocol reader. */ }
    }

    static string? StringProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    static bool IsOfficialLoginUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("auth0.openai.com", StringComparison.OrdinalIgnoreCase));

    static InvalidDataException ProtocolError() => new("Codex 계정 응답 형식을 읽지 못했습니다. 공식 CLI 버전과 연결을 확인해 주세요.");

    void ThrowIfFailed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref transportError) is { } error) throw error;
    }

    void FailTransport(Exception error)
    {
        Interlocked.CompareExchange(ref transportError, error, null);
        foreach (var request in pending.Values) request.TrySetException(error);
        lock (sync) activeLogin?.Completion.TrySetException(error);
    }

    async Task StopTransportAsync()
    {
        FailTransport(new IOException("Codex 계정 서비스와의 연결이 종료되었습니다. 다시 연결해 주세요."));
        var child = process;
        if (child == null) return;
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        catch (TimeoutException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await StopTransportAsync().ConfigureAwait(false);
        try { await Task.WhenAll(reader ?? Task.CompletedTask, errorReader ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { }
        process?.Dispose();
        // Gates may still have an awaiting UI operation unwinding; do not dispose them underneath it.
    }

    sealed class LoginAttempt
    {
        public string? Id;
        public readonly Dictionary<string, bool> EarlyCompletions = new();
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
