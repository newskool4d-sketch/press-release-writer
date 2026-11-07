using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PressRelease;

public enum ModelConnection { CodexCli, ClaudeWeb, GeminiCli, GeminiWeb, ClaudeCli, GeminiApi }
public enum CliFailureKind { Installation, Authentication, Permission, RateLimit, Network, Unknown }
public sealed class CliExecutionException(CliFailureKind kind, int exitCode, string message) : Exception(message)
{
    public CliFailureKind Kind { get; } = kind;
    public int ExitCode { get; } = exitCode;
}

public static class Models
{
    public static string Label(ModelConnection model) => model switch
    {
        ModelConnection.CodexCli => "ChatGPT · Codex 연결",
        ModelConnection.ClaudeCli => "Claude Code",
        ModelConnection.ClaudeWeb => "Claude · 공식 웹",
        ModelConnection.GeminiCli => "Gemini CLI · 기관 계정",
        ModelConnection.GeminiApi => "Gemini · API",
        _ => "Gemini · 공식 웹"
    };

    public static bool IsCli(ModelConnection model) => model is ModelConnection.CodexCli or ModelConnection.ClaudeCli or ModelConnection.GeminiCli;

    public static string OfficialPage(ModelConnection model) => model switch
    {
        ModelConnection.CodexCli => "https://learn.chatgpt.com/docs/codex/cli",
        ModelConnection.ClaudeCli => "https://code.claude.com/docs/en/quickstart",
        ModelConnection.ClaudeWeb => "https://claude.ai/new",
        ModelConnection.GeminiCli => "https://geminicli.com/docs/get-started/installation/",
        ModelConnection.GeminiApi => "https://aistudio.google.com/apikey",
        _ => "https://gemini.google.com/app"
    };

    public static void OpenOfficialPage(ModelConnection model) =>
        Process.Start(new ProcessStartInfo(OfficialPage(model)) { UseShellExecute = true });

    public static string AccountLabel(CodexAccountInfo account) => account.IsChatGpt
        ? "ChatGPT 로그인됨" : account.IsAuthenticated ? "다른 인증 방식 · ChatGPT로 연결해 주세요" : "로그인 필요";

    public static ProcessStartInfo CreateAppServerStart(string appFolder)
    {
        var info = CreateStart(ModelConnection.CodexCli, appFolder);
        info.ArgumentList.Add("app-server");
        info.ArgumentList.Add("--listen");
        info.ArgumentList.Add("stdio://");
        return info;
    }

    public static string CommandLocation(ModelConnection model, string appFolder, string? googleProject = null)
    {
        var info = CreateStart(model, appFolder, googleProject);
        return Path.GetFileName(info.FileName).Equals("node.exe", StringComparison.OrdinalIgnoreCase)
            ? info.ArgumentList[0] : info.FileName;
    }

    public static async Task<string> CheckAsync(ModelConnection model, string appFolder, CancellationToken cancel,
        CodexAppServer? session = null, string? googleProject = null)
    {
        if (model == ModelConnection.GeminiApi) return "AI 연결에서 Gemini API 키를 입력하고 연결을 확인해 주세요.";
        if (!IsCli(model)) return "설치: 불필요\n인증: 공식 웹에서 직접 확인\n사용 가능 여부: 공식 웹에서 응답을 복사해 가져오세요.";
        var result = await RunAsync(CreateStart(model, appFolder, googleProject), ["--version"], null, cancel);
        if (result.ExitCode != 0) throw Failure(result.ExitCode, result.Output + "\n" + result.Error);
        var match = Regex.Match(result.Output, @"\b\d+\.\d+(?:\.\d+)?(?:[-+][\w.]+)?\b");
        var installed = match.Success ? match.Value : "실행 확인 · 버전 확인 필요";
        var authentication = "미확인 · 앱 전용 Google 로그인 사용";
        if (model == ModelConnection.CodexCli)
        {
            authentication = AccountLabel(await ReadCodexAccountAsync(appFolder, cancel, session));
        }
        else if (model == ModelConnection.ClaudeCli)
            authentication = await IsConnectedAsync(model, appFolder, cancel) ? "Claude 로그인됨" : "로그인 필요";
        return $"설치: {installed}\n인증: {authentication}\n사용 가능 여부: 미확인 · 실제 생성 시 권한·한도 확인";
    }

    static async Task<CodexAccountInfo> ReadCodexAccountAsync(string appFolder, CancellationToken cancel, CodexAppServer? session)
    {
        if (session != null) return await session.ReadAccountAsync(cancel);
        await using var temporary = new CodexAppServer(() => CreateAppServerStart(appFolder));
        return await temporary.ReadAccountAsync(cancel);
    }

    public static async Task<bool> IsConnectedAsync(ModelConnection model, string appFolder, CancellationToken cancel,
        CodexAppServer? session = null)
    {
        if (model == ModelConnection.CodexCli)
        {
            var account = await ReadCodexAccountAsync(appFolder, cancel, session);
            if (account.IsAuthenticated && !account.IsChatGpt)
                throw new CliExecutionException(CliFailureKind.Authentication, 1, "앱 전용 Codex에서 ChatGPT 계정으로 로그인해 주세요.");
            return account.IsChatGpt;
        }
        if (model != ModelConnection.ClaudeCli)
            throw new ArgumentException("공식 CLI 연결 상태를 확인할 수 없는 모델입니다.", nameof(model));
        var result = await RunAsync(CreateStart(model, appFolder), ["auth", "status", "--json"], null, cancel);
        return ParseClaudeStatus(result.ExitCode, result.Output, result.Error);
    }

    public static bool ParseClaudeStatus(int exitCode, string output, string error)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("loggedIn", out var loggedIn))
            {
                if (loggedIn.ValueKind == JsonValueKind.False && exitCode is 0 or 1) return false;
                if (loggedIn.ValueKind == JsonValueKind.True && exitCode == 0)
                {
                    if (root.TryGetProperty("authMethod", out var method) && method.ValueKind == JsonValueKind.String
                        && method.GetString() == "claude.ai") return true;
                    throw new CliExecutionException(CliFailureKind.Authentication, 1, "Claude Code에서 Claude 구독 계정으로 로그인해 주세요.");
                }
            }
        }
        catch (JsonException) { }
        if (exitCode != 0) throw Failure(exitCode, output + "\n" + error);
        throw new InvalidDataException("Claude Code의 계정 상태를 확인하지 못했습니다. 공식 CLI 버전을 확인해 주세요.");
    }

    public static async Task LoginClaudeAsync(string appFolder, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var info = CreateStart(ModelConnection.ClaudeCli, appFolder);
        foreach (var argument in new[] { "auth", "login", "--claudeai" }) info.ArgumentList.Add(argument);
        // The official CLI opens its browser sign-in and handles any terminal input itself.
        info.CreateNoWindow = false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromMinutes(5));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Claude Code를 시작하지 못했습니다.");
        try
        {
            await process.WaitForExitAsync(limit.Token);
            if (process.ExitCode != 0 || !await IsConnectedAsync(ModelConnection.ClaudeCli, appFolder, cancel))
                throw Failure(process.ExitCode == 0 ? 1 : process.ExitCode, "authentication");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("Claude Code 로그인 시간이 초과되었습니다.");
        }
        finally { await StopAsync(process); }
    }

    public static async Task LogoutClaudeAsync(string appFolder, CancellationToken cancel)
    {
        var result = await RunAsync(CreateStart(ModelConnection.ClaudeCli, appFolder), ["auth", "logout"], null, cancel);
        if (result.ExitCode != 0) throw Failure(result.ExitCode, result.Output + "\n" + result.Error);
    }

    public static async Task LoginGeminiAsync(string appFolder, string? googleProject, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var info = CreateStart(ModelConnection.GeminiCli, appFolder, ValidateGeminiProject(googleProject));
        info.ArgumentList.Add("--extensions");
        info.ArgumentList.Add("none");
        // The user interacts with this terminal during the official Google sign-in.
        info.CreateNoWindow = false;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Gemini CLI를 시작하지 못했습니다.");
        try
        {
            await process.WaitForExitAsync(cancel);
            if (process.ExitCode != 0) throw Failure(process.ExitCode, "authentication");
        }
        finally { await StopAsync(process); }
    }

    public static async Task CheckGeminiInstallationAsync(string appFolder, CancellationToken cancel)
    {
        // --version verifies the installed CLI without checking credentials or sending a prompt.
        var result = await RunAsync(CreateStart(ModelConnection.GeminiCli, appFolder), ["--version"], null, cancel);
        if (result.ExitCode != 0) throw Failure(result.ExitCode, result.Output + "\n" + result.Error);
    }

    public static string ValidateGeminiProject(string? googleProject)
    {
        var project = googleProject?.Trim();
        if (string.IsNullOrEmpty(project))
            throw new ArgumentException("AI 연결에서 기관의 Google Cloud 프로젝트 ID를 입력해 주세요.");
        if (!Regex.IsMatch(project, @"\A[a-z][a-z0-9-]{4,28}[a-z0-9]\z"))
            throw new ArgumentException("Google Cloud 프로젝트 ID 형식을 확인해 주세요.");
        return project;
    }

    public static Task CheckGeminiApiAsync(string? apiKey, CancellationToken cancel)
        => GeminiApiClient.Shared.CheckAsync(apiKey, cancel);

    public static async Task<string> GenerateAsync(ModelConnection model, string appFolder, string prompt, CancellationToken cancel,
        CodexAppServer? session = null, string? googleProject = null, string? geminiApiKey = null)
        => Writing.FinalizeDraft(await CompleteAsync(model, appFolder, prompt, cancel, session, googleProject, geminiApiKey));

    public static async Task<string> CompleteAsync(ModelConnection model, string appFolder, string prompt, CancellationToken cancel,
        CodexAppServer? session = null, string? googleProject = null, string? geminiApiKey = null)
    {
        if (model == ModelConnection.GeminiApi)
            return await GeminiApiClient.Shared.CompleteAsync(geminiApiKey, prompt, cancel);
        if (!IsCli(model)) throw new InvalidOperationException("공식 웹에서 응답을 복사해 주세요.");
        if (model == ModelConnection.GeminiCli)
        {
            googleProject = ValidateGeminiProject(googleProject);
            prompt = PrepareGeminiPrompt(prompt);
        }
        if (model is ModelConnection.CodexCli or ModelConnection.ClaudeCli
            && !await IsConnectedAsync(model, appFolder, cancel, session))
            throw Failure(1, "authentication");

        string[] args = model switch
        {
            ModelConnection.CodexCli => ["--ask-for-approval", "never", "--disable", "shell_tool", "--disable", "apps",
                "--disable", "plugins", "--disable", "memories", "--disable", "request_permissions_tool",
                "-c", "web_search=\"disabled\"", "exec", "--ignore-user-config", "--sandbox", "read-only",
                "--ignore-rules", "--skip-git-repo-check", "--ephemeral", "--json", "-"],
            ModelConnection.ClaudeCli => ["-p", "--output-format", "json", "--tools", "", "--strict-mcp-config",
                "--restricted", "--disable-slash-commands", "--no-session-persistence", "--no-chrome"],
            _ => ["--output-format", "json", "--approval-mode", "default", "--extensions", "none"]
        };
        var result = await RunAsync(CreateStart(model, appFolder, googleProject), args, prompt, cancel);
        if (result.ExitCode != 0) throw Failure(result.ExitCode, result.Output + "\n" + result.Error);
        return ParseReply(model, result.Output);
    }

    static readonly JsonSerializerOptions GeminiPromptOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PrepareGeminiPrompt(string prompt)
    {
        // Gemini CLI interprets @ references and leading slash commands even in piped input.
        if (!prompt.Contains('@') && !prompt.TrimStart().StartsWith('/')) return prompt;
        var encoded = JsonSerializer.Serialize(prompt, GeminiPromptOptions).Replace("@", "\\u0040", StringComparison.Ordinal);
        return "다음 JSON 문자열을 디코딩한 텍스트를 작성 요청으로 처리하고, 요청된 형식의 결과만 출력하세요.\n" + encoded;
    }

    public static string ParseReply(ModelConnection model, string output)
    {
        // CLIs may print warnings or notices around the JSON they emit. Only the JSON payload is interpreted.
        if (model == ModelConnection.ClaudeCli)
        {
            using var document = ParseEmbeddedObject(output, "Claude Code 응답 형식을 확인해 주세요.");
            var root = document.RootElement;
            if (root.TryGetProperty("is_error", out var error))
            {
                if (error.ValueKind == JsonValueKind.True) throw Failure(1, output);
                if (error.ValueKind == JsonValueKind.False && StringProperty(root, "result") is { } result && !string.IsNullOrWhiteSpace(result))
                    return result;
            }
        }
        else if (model == ModelConnection.GeminiCli)
        {
            using var json = ParseEmbeddedObject(output, "Gemini CLI 응답 형식을 확인해 주세요.");
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
                throw Failure(1, error.ToString());
            if (StringProperty(json.RootElement, "response") is { } response && !string.IsNullOrWhiteSpace(response)) return response;
        }
        else if (model == ModelConnection.CodexCli)
        {
            string? last = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith('{')) continue;
                JsonDocument json;
                try { json = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (json)
                {
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    var type = StringProperty(root, "type");
                    if (type == "turn.failed") throw Failure(1, root.ToString());
                    if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                        StringProperty(item, "type") == "agent_message" && StringProperty(item, "text") is { } message)
                        last = message;
                }
            }
            if (!string.IsNullOrWhiteSpace(last)) return last;
        }
        throw new InvalidDataException("CLI 응답에 초안 텍스트가 없습니다.");
    }

    static JsonDocument ParseEmbeddedObject(string output, string message)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start) throw new InvalidDataException(message);
        try
        {
            var document = JsonDocument.Parse(output.AsMemory(start, end - start + 1));
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        throw new InvalidDataException(message);
    }

    static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    public static CliFailureKind ClassifyError(string detail)
    {
        var text = detail.ToLowerInvariant();
        if (Regex.IsMatch(text, @"rate.?limit|quota|resource_exhausted|\b429\b|usage limit")) return CliFailureKind.RateLimit;
        if (Regex.IsMatch(text, @"unexpected argument|unknown (option|flag|feature)|unrecognized|enoent")) return CliFailureKind.Installation;
        if (Regex.IsMatch(text, @"project.?id|google_cloud_project|permission.denied|forbidden|\b403\b|not (authorized|allowed)|access.denied")) return CliFailureKind.Permission;
        if (Regex.IsMatch(text, @"unauthorized|\b401\b|login|log in|sign.in|authenticat|credential|token.*expir")) return CliFailureKind.Authentication;
        if (Regex.IsMatch(text, @"network|econn|enotfound|dns|proxy|certificate|tls|connection|timed? ?out|fetch failed")) return CliFailureKind.Network;
        return CliFailureKind.Unknown;
    }

    public static CliExecutionException Failure(int exitCode, string detail)
    {
        var kind = ClassifyError(detail);
        var message = kind switch
        {
            CliFailureKind.Installation => "CLI 설치 또는 지원 버전 문제입니다. 공식 설치 안내에서 업데이트한 뒤 연결을 확인해 주세요.",
            CliFailureKind.Authentication => "로그인이 필요하거나 인증이 만료됐습니다. 다시 연결해 주세요.",
            CliFailureKind.Permission => "계정 권한과 조직 설정을 확인해 주세요.",
            CliFailureKind.RateLimit => "사용 한도에 도달했습니다. 제공사에서 한도와 재사용 가능 시각을 확인해 주세요.",
            CliFailureKind.Network => "네트워크·기관 프록시·인증서 연결을 확인해 주세요.",
            _ => "CLI 실행에 실패했습니다. 공식 CLI에서 연결 상태를 확인해 주세요."
        };
        // Provider output may contain prompt fragments or credentials. Never echo it.
        return new(kind, exitCode, $"{message} (종료 코드 {exitCode})");
    }

    public static ProcessStartInfo CreateStart(ModelConnection model, string appFolder, string? googleProject = null)
    {
        if (!IsCli(model)) throw new ArgumentException("CLI 연결이 아닙니다.", nameof(model));
        var command = model switch
        {
            ModelConnection.CodexCli => "codex",
            ModelConnection.ClaudeCli => "claude",
            _ => "gemini"
        };
        var work = Path.Combine(appFolder, "cli");
        Directory.CreateDirectory(work);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => d.Trim().Trim('"')).Where(Path.IsPathFullyQualified).ToArray();
        var paths = dirs.Append(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
            .Append(Path.Combine(roaming, "npm")).Distinct(StringComparer.OrdinalIgnoreCase);
        var scriptRelative = model switch
        {
            ModelConnection.CodexCli => Path.Combine("@openai", "codex", "bin", "codex.js"),
            ModelConnection.ClaudeCli => Path.Combine("@anthropic-ai", "claude-code", "cli.js"),
            _ => Path.Combine("@google", "gemini-cli", "dist", "index.js")
        };
        var node = dirs.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"))
            .Select(d => Path.Combine(d, "node.exe")).FirstOrDefault(File.Exists);
        var info = new ProcessStartInfo { WorkingDirectory = work, UseShellExecute = false };
        foreach (var dir in paths)
        {
            var native = Path.Combine(dir, command + ".exe");
            if (File.Exists(native)) { info.FileName = native; break; }
            var script = Path.Combine(dir, "node_modules", scriptRelative);
            if (node != null && File.Exists(script))
            { info.FileName = node; info.ArgumentList.Add(script); break; }
        }
        if (string.IsNullOrEmpty(info.FileName))
            throw new FileNotFoundException($"{command} CLI를 찾지 못했습니다. 공식 설치 후 앱을 다시 열어 주세요.");

        if (model == ModelConnection.CodexCli)
        {
            foreach (var key in new[] { "CODEX_API_KEY", "OPENAI_API_KEY", "OPENAI_BASE_URL", "CODEX_ACCESS_TOKEN",
                "OPENAI_WORKLOAD_IDENTITY_CONTEXT", "NODE_OPTIONS", "NODE_PATH" })
                info.Environment.Remove(key);
            var home = Directory.CreateDirectory(Path.Combine(appFolder, "codex-home")).FullName;
            if (File.Exists(Path.Combine(home, "AGENTS.md")) || File.Exists(Path.Combine(home, "AGENTS.override.md")))
                throw new InvalidOperationException("앱 전용 Codex 폴더에 개인 지침 파일이 있어 실행할 수 없습니다.");
            info.Environment["CODEX_HOME"] = home;
            // Login, account reads, and generation must all use the same CLI-owned credential store.
            foreach (var setting in new[] { "cli_auth_credentials_store=\"file\"", "forced_login_method=\"chatgpt\"",
                "model_provider=\"openai\"", "project_doc_max_bytes=0", "skills.include_instructions=false",
                "features.skip_host_skill_discovery=true", "history.persistence=\"none\"" })
            {
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add(setting);
            }
        }
        else if (model == ModelConnection.ClaudeCli)
        {
            // Preserve Claude's native login location. Discard inherited billing and alternate-routing overrides.
            foreach (var key in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
                "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_PROFILE", "ANTHROPIC_FEDERATION_RULE_ID",
                "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX",
                "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_USE_MANTLE", "CLAUDE_CODE_USE_ANTHROPIC_AWS",
                "CLAUDE_CODE_USE_ANTHROPIC_GOOGLE_CLOUD", "NODE_OPTIONS", "NODE_PATH" })
                info.Environment.Remove(key);
        }
        else
        {
            ConfigureGeminiStart(info, appFolder, googleProject);
        }
        return info;
    }

    static void ConfigureGeminiStart(ProcessStartInfo info, string appFolder, string? googleProject)
    {
        var project = string.IsNullOrWhiteSpace(googleProject) ? null : ValidateGeminiProject(googleProject);
        var profile = Path.Combine(appFolder, "GeminiProfile");
        var config = Path.Combine(profile, ".gemini");
        var workspace = Path.Combine(profile, "workspace");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(workspace);
        info.WorkingDirectory = workspace;

        // An empty local .env prevents CLI fallback to an ancestor or the user's home .env.
        var envFile = Path.Combine(workspace, ".env");
        if (!File.Exists(envFile))
        {
            using var empty = new FileStream(envFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        if (new FileInfo(envFile).Length != 0 || Directory.Exists(Path.Combine(workspace, ".gemini")))
            throw new InvalidOperationException("앱 전용 Gemini 작업 폴더에 별도 CLI 설정이 있습니다. 해당 설정을 확인해 주세요.");

        var settings = Path.Combine(config, "settings.json");
        try
        {
            var original = File.Exists(settings) ? File.ReadAllText(settings) : null;
            var document = original == null ? new JsonObject() : JsonNode.Parse(original) as JsonObject ?? throw new JsonException();
            var auth = Section(Section(document, "security"), "auth");
            auth["selectedType"] = "oauth-personal";
            auth["enforcedType"] = "oauth-personal";
            auth["useExternal"] = false;

            var tools = Section(document, "tools");
            tools["core"] = new JsonArray("press_release_no_tools");
            tools["allowed"] = new JsonArray();
            tools.Remove("discoveryCommand");
            tools.Remove("callCommand");
            document["mcpServers"] = new JsonObject();
            Section(Section(document, "admin"), "mcp")["enabled"] = false;
            Section(document, "hooksConfig")["enabled"] = false;
            Section(document, "skills")["enabled"] = false;
            var context = Section(document, "context");
            context["fileName"] = new JsonArray();
            context["includeDirectories"] = new JsonArray();
            context["includeDirectoryTree"] = false;
            Section(document, "advanced")["ignoreLocalEnv"] = false;
            Section(document, "telemetry")["enabled"] = false;
            Section(document, "privacy")["usageStatisticsEnabled"] = false;

            var policy = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (original != policy) File.WriteAllText(settings, policy, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("앱 전용 Gemini 설정을 읽지 못했습니다. 설정 파일을 확인해 주세요.");
        }

        info.Environment["GEMINI_CLI_HOME"] = profile;
        // Preserve the OS home and system policy paths, with explicit org project and official auth routing.
        foreach (var key in new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI", "GOOGLE_APPLICATION_CREDENTIALS",
            "GOOGLE_CLOUD_PROJECT", "GOOGLE_CLOUD_PROJECT_ID", "GOOGLE_CLOUD_LOCATION", "GOOGLE_GEMINI_BASE_URL", "GOOGLE_VERTEX_BASE_URL",
            "CODE_ASSIST_ENDPOINT", "CODE_ASSIST_API_VERSION", "GEMINI_SYSTEM_MD", "GEMINI_WRITE_SYSTEM_MD", "GEMINI_CLI_TRUSTED_FOLDERS_PATH",
            "GEMINI_CLI_TRUST_WORKSPACE", "GEMINI_CLI_USE_COMPUTE_ADC", "CLOUD_SHELL", "NODE_OPTIONS", "NODE_PATH" })
            info.Environment.Remove(key);
        if (project != null) info.Environment["GOOGLE_CLOUD_PROJECT"] = project;
    }

    static JsonObject Section(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject section) return section;
        if (parent[key] != null) throw new JsonException();
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo info, string[] args,
        string? prompt, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(prompt == null ? 1 : 3));
        var token = limit.Token;
        token.ThrowIfCancellationRequested();
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.CreateNoWindow = true;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.StandardInputEncoding = new UTF8Encoding(false);
        info.StandardOutputEncoding = info.StandardErrorEncoding = Encoding.UTF8;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("CLI를 시작하지 못했습니다.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            if (prompt != null) await process.StandardInput.WriteAsync(prompt.AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            var stdout = await output;
            var stderr = await error;
            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("연결 또는 생성 시간이 초과되었습니다. 로그인·네트워크를 확인해 주세요.");
        }
        catch (IOException) when (process.HasExited && process.ExitCode != 0)
        {
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            await StopAsync(process);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { }
    }
}
