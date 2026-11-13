using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PressRelease;

static class ProcessChecks
{
    const string PrivateMarker = "MOCK_PRIVATE_MARKER_DO_NOT_DISPLAY";

    public static async Task RunAsync(Action<bool, string> require)
    {
        var fixture = Directory.CreateTempSubdirectory("press-process-check-").FullName;
        try
        {
            CheckStartConfiguration(fixture, require);
            CheckClaudeResponses(require);
            await CheckGeminiRequestBoundary(fixture, require);
            var prompt = "인천 학생 75명 · 인용 \"안내\" & | <> 😀\r\n" + new string('한', 49_900);
            var echo = await Models.RunAsync(Start("echo"), [], prompt, default, TimeSpan.FromSeconds(10));
            require(echo.ExitCode == 0 && echo.Output == prompt, "Windows CLI UTF-8 stdin/stdout round trip");
            require(echo.Error == new string('진', 40_000), "Windows CLI concurrently drains large stderr");

            foreach (var (scenario, expected) in new[]
            {
                ("authentication", CliFailureKind.Authentication), ("permission", CliFailureKind.Permission),
                ("limit", CliFailureKind.RateLimit), ("installation", CliFailureKind.Installation),
                ("network", CliFailureKind.Network), ("unknown", CliFailureKind.Unknown)
            })
            {
                var result = await Models.RunAsync(Start(scenario), [], "합성 원자료", default, TimeSpan.FromSeconds(10));
                var failure = Models.Failure(result.ExitCode, result.Output + "\n" + result.Error);
                require(result.ExitCode == 23 && result.Output.Contains("partial output") && result.Error.Contains(PrivateMarker), "Windows CLI nonzero output captured: " + scenario);
                require(failure.Kind == expected && failure.ExitCode == 23 && !failure.Message.Contains(PrivateMarker)
                    && !failure.Message.Contains("partial output") && !failure.Message.Contains("example.invalid"), "Windows CLI safe classified error: " + scenario);
            }

            var early = await Models.RunAsync(Start("early-exit"), [], new string('한', 50_000), default, TimeSpan.FromSeconds(10));
            require(early.ExitCode == 29 && early.Error.Contains("early exit"), "Windows CLI early exit during stdin write");

            await CheckStoppedProcess(fixture, false, require);
            await CheckStoppedProcess(fixture, true, require);
            await CheckStoppedProcess(fixture, false, require, rootExits: true);
            await CheckStoppedProcess(fixture, true, require, rootExits: true);
        }
        finally
        {
            // Only this invocation's newly created synthetic fixture directory is removed.
            Directory.Delete(fixture, recursive: true);
        }
    }

    static void CheckStartConfiguration(string fixture, Action<bool, string> require)
    {
        var earlier = Directory.CreateDirectory(Path.Combine(fixture, "earlier npm")).FullName;
        var later = Directory.CreateDirectory(Path.Combine(fixture, "later native")).FullName;
        var nodeDir = Directory.CreateDirectory(Path.Combine(fixture, "node")).FullName;
        var node = Path.Combine(nodeDir, "node.exe");
        var codexScript = Path.Combine(earlier, "node_modules", "@openai", "codex", "bin", "codex.js");
        var geminiScript = Path.Combine(earlier, "node_modules", "@google", "gemini-cli", "dist", "index.js");
        Directory.CreateDirectory(Path.GetDirectoryName(codexScript)!);
        Directory.CreateDirectory(Path.GetDirectoryName(geminiScript)!);
        File.WriteAllText(node, "synthetic discovery fixture; never execute");
        File.WriteAllText(codexScript, "synthetic discovery fixture; never execute");
        File.WriteAllText(geminiScript, "synthetic discovery fixture; never execute");
        File.WriteAllText(Path.Combine(later, "codex.exe"), "synthetic discovery fixture; never execute");
        var claudeExe = Path.Combine(later, "claude.exe");
        File.WriteAllText(claudeExe, "synthetic discovery fixture; never execute");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, earlier, later, nodeDir));
            var app = Path.Combine(fixture, "한글 앱 프로필");
            var selected = Models.CreateStart(ModelConnection.CodexCli, app);
            require(selected.FileName == node && selected.ArgumentList[0] == codexScript, "CLI discovery respects earlier npm directory over later EXE");
            require(!selected.Environment.ContainsKey("OPENAI_API_KEY") && !selected.Environment.ContainsKey("CODEX_ACCESS_TOKEN"), "Codex child excludes API/token environment overrides");
            var appServer = Models.CreateAppServerStart(app);
            var codexHome = Path.Combine(app, "codex-home");
            require(selected.Environment["CODEX_HOME"] == codexHome && appServer.Environment["CODEX_HOME"] == codexHome
                && Directory.Exists(codexHome)
                && selected.ArgumentList.Contains("cli_auth_credentials_store=\"file\"")
                && appServer.ArgumentList.Contains("cli_auth_credentials_store=\"file\""),
                "Codex account and generation use the same isolated credential store");
            require(!File.Exists(Path.Combine(codexHome, "auth.json")), "Codex discovery does not copy credentials");

            var claude = Models.CreateStart(ModelConnection.ClaudeCli, app);
            claude.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var claudeConfig);
            require(claude.FileName == claudeExe && claude.ArgumentList.Count == 0
                && claudeConfig == Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                && !claude.Environment.ContainsKey("ANTHROPIC_API_KEY")
                && !claude.Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN")
                && !claude.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                "Claude uses the installed native binary and its login profile without inherited overrides");

            var gemini = Models.CreateStart(ModelConnection.GeminiCli, app, "test-project-123");
            require(gemini.FileName == node && gemini.ArgumentList[0] == geminiScript, "Gemini npm discovery");
            gemini.Environment.TryGetValue("HOME", out var childHome);
            gemini.Environment.TryGetValue("USERPROFILE", out var childUserProfile);
            require(gemini.Environment["GEMINI_CLI_HOME"] == Path.Combine(app, "GeminiProfile")
                && childHome == Environment.GetEnvironmentVariable("HOME")
                && childUserProfile == Environment.GetEnvironmentVariable("USERPROFILE"), "Gemini profile isolation preserves OS home");
            require(gemini.Environment["GOOGLE_CLOUD_PROJECT"] == "test-project-123", "Gemini organization project environment");
            require(!gemini.Environment.ContainsKey("GEMINI_API_KEY") && !gemini.Environment.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS")
                && !gemini.Environment.ContainsKey("CODE_ASSIST_ENDPOINT"), "Gemini does not inherit alternative credentials or endpoints");
            require(new FileInfo(Path.Combine(gemini.WorkingDirectory, ".env")).Length == 0
                && gemini.WorkingDirectory.StartsWith(gemini.Environment["GEMINI_CLI_HOME"] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Gemini workspace prevents fallback to user dotenv configuration");

            var settings = Path.Combine(app, "GeminiProfile", ".gemini", "settings.json");
            var modified = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
            modified["tools"]!["core"] = new JsonArray("read_file");
            modified["mcpServers"] = new JsonObject { ["synthetic"] = new JsonObject { ["command"] = "never-execute" } };
            modified["unrelatedPreference"] = "preserved";
            File.WriteAllText(settings, modified.ToJsonString());
            _ = Models.CreateStart(ModelConnection.GeminiCli, app, "test-project-123");
            using var refreshed = JsonDocument.Parse(File.ReadAllText(settings));
            var root = refreshed.RootElement;
            require(root.GetProperty("tools").GetProperty("core").GetArrayLength() == 1
                && root.GetProperty("tools").GetProperty("core")[0].GetString() == "press_release_no_tools"
                && !root.GetProperty("mcpServers").EnumerateObject().Any()
                && !root.GetProperty("admin").GetProperty("mcp").GetProperty("enabled").GetBoolean()
                && !root.GetProperty("hooksConfig").GetProperty("enabled").GetBoolean()
                && !root.GetProperty("skills").GetProperty("enabled").GetBoolean()
                && !root.GetProperty("context").GetProperty("includeDirectoryTree").GetBoolean()
                && root.GetProperty("security").GetProperty("auth").GetProperty("enforcedType").GetString() == "oauth-personal"
                && root.GetProperty("unrelatedPreference").GetString() == "preserved", "Gemini reapplies tool restrictions while preserving unrelated preference");
            bool invalidProject = false;
            try { _ = Models.CreateStart(ModelConnection.GeminiCli, app, "invalid project !"); }
            catch (ArgumentException) { invalidProject = true; }
            require(invalidProject, "Gemini rejects invalid project identifier");
        }
        finally { Environment.SetEnvironmentVariable("PATH", previousPath); }
    }

    static async Task CheckGeminiRequestBoundary(string fixture, Action<bool, string> require)
    {
        var app = Path.Combine(fixture, "Gemini requires project");
        bool loginRejected = false;
        bool generationRejected = false;
        try { await Models.LoginGeminiAsync(app, null, default); }
        catch (ArgumentException) { loginRejected = true; }
        try { _ = await Models.CompleteAsync(ModelConnection.GeminiCli, app, "합성 자료", default); }
        catch (ArgumentException) { generationRejected = true; }
        require(loginRejected && generationRejected && !Directory.Exists(app), "Gemini requires an organization project before launching the CLI");

        const string sensitiveSyntax = "/auth\n실제 파일이 아닌 인용 @sample.txt\ninfo@example.invalid";
        var wrapped = Models.PrepareGeminiPrompt(sensitiveSyntax);
        require(!wrapped.StartsWith('/') && !wrapped.Contains('@')
            && JsonSerializer.Deserialize<string>(wrapped[(wrapped.IndexOf('\n') + 1)..]) == sensitiveSyntax
            && Models.PrepareGeminiPrompt("일반 요청") == "일반 요청", "Gemini command-like source is sent as reversible literal text");
    }

    static void CheckClaudeResponses(Action<bool, string> require)
    {
        require(Models.ParseClaudeStatus(0, """{"loggedIn":true,"authMethod":"claude.ai"}""", "")
            && !Models.ParseClaudeStatus(1, """{"loggedIn":false,"authMethod":"none"}""", ""),
            "Claude distinguishes subscription login from signed out");
        bool rejected = false;
        try
        {
            _ = Models.ParseClaudeStatus(0, """{"loggedIn":true,"authMethod":"api_key","email":"MOCK_PRIVATE_MARKER_DO_NOT_DISPLAY"}""", "");
        }
        catch (CliExecutionException ex)
        {
            rejected = ex.Kind == CliFailureKind.Authentication && !ex.Message.Contains(PrivateMarker);
        }
        require(rejected, "Claude refuses alternate billing authentication without revealing account details");
        require(Models.ParseReply(ModelConnection.ClaudeCli, """{"type":"result","is_error":false,"result":"합성 응답"}""") == "합성 응답",
            "Claude machine-readable result is returned as text");
    }

    static async Task CheckStoppedProcess(string fixture, bool timeout, Action<bool, string> require, bool rootExits = false)
    {
        var scenario = rootExits ? "exit-with-leaf" : "hold-tree";
        var pidFile = Path.Combine(fixture, scenario + (timeout ? "-timeout.pid" : "-cancel.pid"));
        using var cancellation = new CancellationTokenSource();
        var operation = Models.RunAsync(Start(scenario, pidFile), [], "합성 취소 검증", cancellation.Token,
            timeout ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(20));
        Process? parentFixture = null, leafFixture = null;
        try
        {
            var readyLimit = Stopwatch.StartNew();
            while (!File.Exists(pidFile))
            {
                if (operation.IsCompleted) await operation;
                if (readyLimit.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Mock CLI readiness timed out.");
                await Task.Delay(20);
            }
            var pids = File.ReadAllText(pidFile).Split(',');
            parentFixture = OpenFixture(int.Parse(pids[0]));
            leafFixture = OpenFixture(int.Parse(pids[1]));
            if (rootExits)
            {
                if (parentFixture != null) await parentFixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                require(leafFixture is { HasExited: false } && !operation.IsCompleted,
                    "Windows CLI descendant holds the output open after the launcher exits");
            }
            if (!timeout) cancellation.Cancel();
            Exception? observed = null;
            try { await operation; }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { observed = ex; }
            require(timeout ? observed is TimeoutException : observed is OperationCanceledException,
                timeout ? "Windows CLI timeout is distinct from cancellation" : "Windows CLI cancellation propagated");
            if (leafFixture != null) await Task.WhenAny(leafFixture.WaitForExitAsync(), Task.Delay(1000));
            require(IsGone(parentFixture) && IsGone(leafFixture),
                $"Windows CLI {(timeout ? "timeout" : "cancel")} stops descendants ({scenario})");
        }
        finally
        {
            cancellation.Cancel();
            try { await operation; } catch (Exception) { }
            // Retain the original process handles, so cleanup cannot target a reused PID.
            StopFixture(parentFixture);
            StopFixture(leafFixture);
            parentFixture?.Dispose();
            leafFixture?.Dispose();
        }
    }

    static Process? OpenFixture(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            _ = process.Handle;
            return process;
        }
        catch (ArgumentException) { return null; }
    }

    static bool IsGone(Process? process) => process == null || process.HasExited;

    static void StopFixture(Process? process)
    {
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    static ProcessStartInfo Start(string scenario, string? pidFile = null)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Smoke executable unavailable.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Smoke assembly unavailable."));
        info.ArgumentList.Add("--mock-cli-process");
        info.ArgumentList.Add(scenario);
        if (pidFile != null) info.ArgumentList.Add(pidFile);
        return info;
    }

    public static async Task<bool> RunMockIfRequestedAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--mock-cli-process") return false;
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var scenario = args[1];
        if (scenario == "echo")
        {
            await Console.Error.WriteAsync(new string('진', 40_000));
            var input = await Console.In.ReadToEndAsync();
            await Console.Out.WriteAsync(input);
            return true;
        }
        if (scenario == "early-exit")
        {
            await Console.Error.WriteAsync("early exit");
            await Console.Error.FlushAsync();
            Environment.ExitCode = 29;
            return true;
        }
        if (scenario == "leaf")
        {
            await Task.Delay(TimeSpan.FromSeconds(25));
            return true;
        }
        if (scenario is "hold-tree" or "exit-with-leaf")
        {
            using var leaf = Process.Start(Start("leaf")) ?? throw new InvalidOperationException("Mock leaf failed to start.");
            var temporary = args[2] + ".tmp";
            File.WriteAllText(temporary, Environment.ProcessId + "," + leaf.Id);
            File.Move(temporary, args[2]);
            if (scenario == "hold-tree") await Task.Delay(TimeSpan.FromSeconds(25));
            return true;
        }
        _ = await Console.In.ReadToEndAsync();
        var detail = scenario switch
        {
            "authentication" => "401 unauthorized: authentication expired",
            "permission" => "403 permission denied GOOGLE_CLOUD_PROJECT missing",
            "limit" => "429 rate limit quota exceeded",
            "installation" => "unknown option --synthetic-flag",
            "network" => "ECONNRESET network connection failed",
            _ => "unspecified provider failure"
        };
        await Console.Out.WriteAsync("partial output");
        await Console.Error.WriteAsync(detail + " " + PrivateMarker + " https://example.invalid/private");
        Environment.ExitCode = 23;
        return true;
    }
}
