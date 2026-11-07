using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PressRelease;

static class AppServerChecks
{
    public static async Task RunAsync(Action<bool, string> require)
    {
        await using (var server = NewServer("normal"))
        {
            var changed = new List<CodexAccountInfo>();
            server.AccountChanged += account => { lock (changed) changed.Add(account); };
            require(!(await server.ReadAccountAsync()).IsAuthenticated, "app-server initialize and account/read");
            var concurrent = await Task.WhenAll(server.ReadAccountAsync(), server.ReadAccountAsync());
            require(concurrent.All(account => !account.IsAuthenticated), "app-server concurrent request identifiers");
            var account = await server.LoginAsync(false, _ => Task.CompletedTask);
            require(account.IsChatGpt && account.PlanType == "plus", "app-server browser login completion and account refresh");
            await server.LogoutAsync();
            require(!(await server.ReadAccountAsync()).IsAuthenticated, "app-server logout state");
            lock (changed) require(changed.Any(x => x.IsChatGpt) && !changed[^1].IsAuthenticated, "app-server account notifications");
        }
        await using (var server = NewServer("early"))
        {
            var account = await server.LoginAsync(false, _ => Task.CompletedTask);
            require(account.IsChatGpt, "app-server completion before login/start response");
        }
        await using (var server = NewServer("device"))
        {
            CodexLoginChallenge? challenge = null;
            var account = await server.LoginAsync(true, value => { challenge = value; return server.ReadAccountAsync(); });
            require(account.IsChatGpt && challenge is { DeviceCode: true, UserCode: "TEST-1234" }
                && challenge.VerificationUri.Host == "auth.openai.com", "app-server device challenge");
        }
        foreach (var scenario in new[] { "cancel", "delayed-start" })
        {
            await using var server = NewServer(scenario);
            await server.ReadAccountAsync();
            using var cancel = new CancellationTokenSource();
            if (scenario == "delayed-start") cancel.CancelAfter(50);
            await ExpectAsync<OperationCanceledException>(() => server.LoginAsync(false, _ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token),
                require, "app-server cancels " + scenario);
            var account = await server.LoginAsync(false, _ => Task.CompletedTask);
            require(account.IsChatGpt && account.PlanType == "cancel-observed", "app-server cancel acknowledgement " + scenario);
        }
        await using (var server = NewServer("stalled-start", TimeSpan.FromSeconds(30)))
        {
            await server.ReadAccountAsync();
            using var cancel = new CancellationTokenSource(50);
            var elapsed = Stopwatch.StartNew();
            await ExpectAsync<OperationCanceledException>(() => server.LoginAsync(false, _ => Task.CompletedTask, cancel.Token), require, "app-server cancels without login/start response");
            require(elapsed.Elapsed < TimeSpan.FromSeconds(10), "app-server stalled login cancellation is bounded");
        }
        await using (var server = NewServer("cancel"))
        {
            using var cancel = new CancellationTokenSource();
            var challenged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = server.LoginAsync(false, _ => { challenged.TrySetResult(); return Task.CompletedTask; }, cancel.Token);
            await challenged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ExpectAsync<InvalidOperationException>(() => server.LoginAsync(false, _ => Task.CompletedTask), require, "app-server duplicate login guard");
            cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(async () => await first, require, "app-server duplicate attempt cleanup");
        }
        await using (var server = NewServer("failure"))
        {
            string? failure = null;
            try
            {
                await server.LoginAsync(false, _ => Task.CompletedTask);
            }
            catch (InvalidOperationException ex)
            {
                failure = ex.Message;
            }
            require(failure != null && !failure.Contains("mock-secret") && !failure.Contains("example.invalid"), "app-server login error redaction");
        }
        await using (var server = NewServer("untrusted-url"))
            await ExpectAsync<InvalidDataException>(() => server.LoginAsync(false, _ => throw new Exception("Must not open URL")), require, "app-server rejects nonofficial login URL");
        await using (var server = NewServer("unsupported"))
            await ExpectAsync<InvalidOperationException>(() => server.ReadAccountAsync(), require, "app-server unsupported method error");
        await using (var server = NewServer("exit"))
            await ExpectAsync<IOException>(() => server.ReadAccountAsync(), require, "app-server child exit completes pending request");
        await using (var server = NewServer("malformed"))
            await ExpectAsync<InvalidDataException>(() => server.ReadAccountAsync(), require, "app-server malformed JSON");
        await using (var server = NewServer("timeout", TimeSpan.FromSeconds(2)))
            await ExpectAsync<TimeoutException>(() => server.ReadAccountAsync(), require, "app-server request timeout");
        await using (var server = NewServer("server-request"))
            require(!(await server.ReadAccountAsync()).IsAuthenticated, "app-server refuses server-initiated tool request");
        var pidServer = NewServer("pid");
        var ownedPid = int.Parse((await pidServer.ReadAccountAsync()).PlanType!);
        await pidServer.DisposeAsync();
        bool exited;
        try { using var child = Process.GetProcessById(ownedPid); exited = child.HasExited; }
        catch (ArgumentException) { exited = true; }
        require(exited, "app-server dispose waits for owned process exit");
        await ExpectAsync<ObjectDisposedException>(() => pidServer.ReadAccountAsync(), require, "app-server disposed instance cannot start again");
    }

    static CodexAppServer NewServer(string scenario, TimeSpan? requestTimeout = null) =>
        new(() => CreateMockStart(scenario), requestTimeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

    static ProcessStartInfo CreateMockStart(string scenario)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Smoke executable path unavailable.");
        var info = new ProcessStartInfo(executable);
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Smoke assembly unavailable."));
        info.ArgumentList.Add("--mock-app-server");
        info.ArgumentList.Add(scenario);
        return info;
    }

    static async Task ExpectAsync<T>(Func<Task> action, Action<bool, string> require, string scenario) where T : Exception
    {
        bool caught = false;
        try { await action(); }
        catch (T) { caught = true; }
        require(caught, scenario);
    }

    public static async Task<bool> RunMockIfRequestedAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--mock-app-server") return false;
        var scenario = args.Length > 1 ? args[1] : "normal";
        bool initialized = false, loggedIn = scenario == "pid", cancelled = false, refused = false, deviceChallengeObserved = false;
        int logins = 0;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            if (!request.TryGetProperty("method", out var method))
            {
                if (request.TryGetProperty("id", out var serverId) && serverId.GetString() == "server-tool"
                    && request.TryGetProperty("error", out var refusedError) && refusedError.GetProperty("code").GetInt32() == -32601)
                    refused = true;
                continue;
            }
            if (method.GetString() == "initialized") { initialized = true; continue; }
            var id = request.GetProperty("id").Clone();
            if (method.GetString() == "initialize")
            {
                await Reply(id, new { userAgent = "mock", platformFamily = "windows", platformOs = "windows" });
                continue;
            }
            if (!initialized) { await Error(id, -32000); continue; }
            switch (method.GetString())
            {
                case "account/read":
                    if (scenario == "exit") return true;
                    if (scenario == "malformed") { await Console.Out.WriteLineAsync("invalid JSON mock-secret"); await Console.Out.FlushAsync(); continue; }
                    if (scenario == "timeout") continue;
                    if (scenario == "unsupported") { await Error(id, -32601); continue; }
                    if (scenario == "server-request" && !refused)
                    {
                        await Emit(new { id = "server-tool", method = "item/commandExecution/requestApproval", @params = new { command = "do-not-execute" } });
                        // Read the refusal before replying so this scenario proves the response was sent.
                        using var response = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
                        refused = response.RootElement.TryGetProperty("error", out var refusal) && refusal.GetProperty("code").GetInt32() == -32601;
                        if (!refused) { await Error(id, -32000); continue; }
                    }
                    await Reply(id, new { account = loggedIn ? new { type = "chatgpt", planType = scenario == "pid" ? Environment.ProcessId.ToString() : cancelled ? "cancel-observed" : "plus", email = "ignored@example.invalid" } : null, requiresOpenaiAuth = true });
                    if (scenario == "device" && deviceChallengeObserved && !loggedIn)
                    {
                        loggedIn = true;
                        await Complete(true);
                    }
                    break;
                case "account/login/start":
                    logins++;
                    if (scenario == "stalled-start") break;
                    if (scenario == "delayed-start" && logins == 1) await Task.Delay(200);
                    if ((scenario == "cancel" || scenario == "delayed-start") && logins > 1 && !cancelled) { await Error(id, -32000); break; }
                    var isDevice = request.GetProperty("params").GetProperty("type").GetString() == "chatgptDeviceCode";
                    var mayComplete = scenario != "cancel" && scenario != "delayed-start" || logins > 1;
                    if (scenario == "early") { loggedIn = true; await Complete(true); }
                    if (isDevice)
                    {
                        deviceChallengeObserved = true;
                        await Reply(id, new { type = "chatgptDeviceCode", loginId = "mock-login", verificationUrl = "https://auth.openai.com/codex/device", userCode = "TEST-1234" });
                    }
                    else await Reply(id, new { type = "chatgpt", loginId = "mock-login", authUrl = scenario == "untrusted-url" ? "https://example.invalid/not-official" : "https://auth.openai.com/authorize?mock=true" });
                    if (mayComplete && scenario != "early" && scenario != "untrusted-url" && scenario != "device")
                    {
                        loggedIn = scenario != "failure";
                        await Complete(loggedIn);
                    }
                    break;
                case "account/login/cancel":
                    if (request.GetProperty("params").GetProperty("loginId").GetString() != "mock-login") { await Error(id, -32000); break; }
                    cancelled = true;
                    await Complete(false);
                    await Reply(id, new { status = "cancelled" });
                    break;
                case "account/logout":
                    loggedIn = false;
                    await Emit(new { method = "account/updated", @params = new { authMode = (string?)null, planType = (string?)null } });
                    await Reply(id, new { });
                    break;
                default: await Error(id, -32601); break;
            }
        }
        return true;

        async Task Complete(bool success)
        {
            await Emit(new { method = "account/login/completed", @params = new { loginId = "mock-login", success, error = success ? null : "mock-secret https://example.invalid/private" } });
            if (success) await Emit(new { method = "account/updated", @params = new { authMode = "chatgpt", planType = "plus" } });
        }
    }

    static Task Reply(JsonElement id, object result) => Emit(new { id, result });
    static Task Error(JsonElement id, int code) => Emit(new { id, error = new { code, message = "mock-secret" } });
    static async Task Emit(object value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
        await Console.Out.FlushAsync();
    }
}
