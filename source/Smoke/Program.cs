using System.IO.Compression;
using System.Text;
using PressRelease;

if (await AppServerChecks.RunMockIfRequestedAsync(args)) return;
if (await ProcessChecks.RunMockIfRequestedAsync(args)) return;
if (args is ["--probe-claude"])
{
    var probeRoot = Directory.CreateTempSubdirectory("press-release-claude-probe-").FullName;
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = await Models.IsConnectedAsync(ModelConnection.ClaudeCli, probeRoot, timeout.Token);
        Console.WriteLine($"PASS: official Claude Code account status; subscription connected={connected}; no login or generation requested.");
    }
    finally { Directory.Delete(probeRoot, recursive: true); }
    return;
}
if (args is ["--probe-codex"])
{
    var probeRoot = Directory.CreateTempSubdirectory("press-release-codex-probe-").FullName;
    try
    {
        await using var probe = new CodexAppServer(() =>
        {
            var info = Models.CreateAppServerStart(probeRoot);
            var home = info.Environment["CODEX_HOME"];
            if (home != Path.Combine(probeRoot, "codex-home"))
                throw new InvalidOperationException("The production Codex app-server did not use the app-local profile.");
            return info;
        });
        using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var account = await probe.ReadAccountAsync(probeTimeout.Token);
        if (account.IsAuthenticated) throw new InvalidOperationException("Isolated probe unexpectedly found an authenticated account.");
        Console.WriteLine("PASS: official Codex app-server initialize/account/read, isolated unauthenticated profile; no login or generation requested.");
    }
    finally
    {
        // Windows may release SQLite handles shortly after the killed process has exited.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(probeRoot, recursive: true); break; }
            catch (IOException) when (attempt < 19) { await Task.Delay(100); }
        }
    }
    return;
}
var passed = 0;
var skipped = 0;
var dir = Directory.CreateTempSubdirectory("press-release-smoke-").FullName;
try
{
    const string Korean = "인천 학생 75명을 대상으로 진행했다.";
    var txt = Path.Combine(dir, "UTF-8 한글.txt");
    File.WriteAllText(txt, Korean, new UTF8Encoding(true));
    Require(SourceFiles.Read(txt).Text == Korean, "UTF-8 BOM");
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    File.WriteAllText(txt, Korean, Encoding.GetEncoding(949));
    Require(SourceFiles.Read(txt).Text == Korean, "CP949");
    var md = Path.Combine(dir, "원고.md");
    File.WriteAllText(md, $"# 제목\n\n{Korean}");
    Require(SourceFiles.Read(md).Text.Contains(Korean), "Markdown");

    var hwpx = Path.Combine(dir, "표 포함.hwpx");
    using (var zip = ZipFile.Open(hwpx, ZipArchiveMode.Create))
    {
        using (var xml = new StreamWriter(zip.CreateEntry("Contents/section10.xml").Open()))
            xml.Write("<s xmlns:h='urn:test'><h:p><h:run><h:t>끝 문단</h:t></h:run></h:p></s>");
        using (var xml = new StreamWriter(zip.CreateEntry("Contents/section2.xml").Open()))
            xml.Write($"""
                <s xmlns:h="urn:test"><h:p><h:run><h:t>{Korean}</h:t>
                <h:tbl><h:tr><h:tc><h:subList><h:p><h:run><h:t>대상</h:t></h:run></h:p></h:subList></h:tc>
                <h:tc><h:subList><h:p><h:run><h:t>75명</h:t></h:run></h:p></h:subList></h:tc></h:tr></h:tbl>
                </h:run></h:p></s>
                """);
    }
    var extracted = SourceFiles.Read(hwpx).Text;
    Require(extracted.StartsWith(Korean) && extracted.Contains("대상 | 75명") && extracted.EndsWith("끝 문단"), "HWPX table/section order");

    if (args.Length > 0)
    {
        var pdf = SourceFiles.Read(args[0]).Text;
        Require(pdf.Contains("[PDF 1쪽]") && pdf.Any(c => c is >= '가' and <= '힣'), "Korean PDF");
    }
    else { skipped++; Console.WriteLine("SKIP: Korean PDF (fixture not supplied)"); }

    var sample = $"## 보도자료 초안\n\n### 교육 활동\n\n{Korean}\n\n## 검토 의견\n\n인원 확인";
    var prompt = Writing.Prompt(Korean, "직속기관", "행사 완료", true);
    Require(prompt.Contains("원고의 문제") && prompt.Contains("3일 이상") && prompt.Contains(Korean), "plugin prompt");
    var result = Writing.FinalizeDraft(sample);
    Require(Writing.FinalizeDraft(result) == result && result.EndsWith($"**{Writing.ReviewNotice}**\n"), "review notice");
    var report = Writing.Check(sample);
    Require(report.Contains($"본문 추정 {Korean.Length}자") && !report.Contains("‘명’ 표기 확인"), "local review");
    var first = Writing.Save(dir, result, true, true).Split('\n');
    var second = Writing.Save(dir, result, true, true).Split('\n');
    Require(first.Length == 2 && second.Length == 2 && !first.Intersect(second).Any(), "non-overwriting save");
    Require(File.ReadAllText(Path.Combine(dir, first[0])).Contains(Korean) && !File.ReadAllText(Path.Combine(dir, first[0])).Contains("**"), "TXT encoding");
    Require(File.ReadAllText(Path.Combine(dir, first[1])) == result, "MD encoding");

    const string ndjson = """
    {"type":"thread.started","thread_id":"test"}
    {"type":"item.completed","item":{"type":"agent_message","text":"한글 초안"}}
    {"type":"turn.completed"}
    """;
    Require(Models.ParseReply(ModelConnection.CodexCli, ndjson) == "한글 초안", "Codex JSONL");
    Require(Models.ParseReply(ModelConnection.GeminiCli, """{"response":"한글 초안","stats":{}}""") == "한글 초안", "Gemini JSON");

    WritingChecks.Run(Require);
    ProofreadingChecks.Run(Require);
    await GeminiApiChecks.RunAsync(Require);
    await ReleaseUpdaterChecks.RunAsync(dir, Require);
    await AppServerChecks.RunAsync(Require);
    await ProcessChecks.RunAsync(Require);
    Console.WriteLine($"RESULT: PASS {passed}, SKIP {skipped}, FAIL 0; real service login/generation not run.");
}
finally { Directory.Delete(dir, recursive: true); }

void Require(bool ok, string scenario)
{
    if (!ok) throw new InvalidOperationException("FAIL: " + scenario);
    passed++;
    Console.WriteLine("PASS: " + scenario);
}
