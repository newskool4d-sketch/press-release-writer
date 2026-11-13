using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PressRelease;

internal static class ReleaseUpdaterChecks
{
    const string Tag = "v0.1.10";
    const string AssetUrl = "https://github.com/newskool4d-sketch/press-release-writer/releases/download/" + Tag + "/PressReleaseWriter.exe";
    const string CdnUrl = "https://release-assets.githubusercontent.com/example/release-asset";
    static readonly byte[] Fixture = Encoding.UTF8.GetBytes("synthetic download bytes");
    static readonly string Digest = Convert.ToHexString(SHA256.HashData(Fixture));

    public static async Task RunAsync(string testDirectory, Action<bool, string> require)
    {
        var current = new Version(0, 1, 9, 0);
        var release = Parse(Metadata(), current)!;
        require(release.Version == new Version(0, 1, 10) && release.Digest == Digest && release.Url.AbsoluteUri == AssetUrl,
            "updater selects the newer stable release and exact hashed executable asset");
        require(Parse(Metadata(tag: "v0.1.9"), current) == null && Parse(Metadata(tag: "v0.1.8"), current) == null &&
            Parse(Metadata(tag: "v0.1.10-preview.1"), current) == null && Parse(Metadata(prerelease: true), current) == null &&
            Parse(Metadata(draft: true), current) == null, "updater skips current, older, prerelease and draft versions");
        require(Rejects(() => Parse(Metadata(url: "https://example.invalid/PressReleaseWriter.exe"), current)) &&
            Rejects(() => Parse(Metadata(digest: "sha256:bad"), current)) &&
            Rejects(() => Parse(Metadata(size: 900L * 1024 * 1024), current)),
            "updater rejects foreign asset URLs, malformed digests and oversized downloads");

        var root = Directory.CreateDirectory(Path.Combine(testDirectory, "updater-fixture")).FullName;
        var download = Path.Combine(root, "verified.part");
        var urls = new List<string>();
        using (var http = new HttpClient(new StubHandler(request =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            if (request.Headers.Authorization != null) throw new InvalidOperationException("Unexpected update authorization");
            return request.RequestUri.AbsoluteUri == AssetUrl ? Redirect(CdnUrl) : Bytes(Fixture);
        }))) await ReleaseUpdater.DownloadAsync(http, release, download, default);
        require(urls.SequenceEqual(new[] { AssetUrl, CdnUrl }) && File.ReadAllBytes(download).SequenceEqual(Fixture),
            "updater follows GitHub CDN redirect and verifies the complete streamed asset");

        using (var http = new HttpClient(new StubHandler(request => request.RequestUri!.AbsoluteUri == AssetUrl
            ? Redirect("https://release-assets.githubusercontent.com.example.invalid/asset")
            : throw new InvalidOperationException("Foreign redirect was followed"))))
        {
            var path = Path.Combine(root, "forbidden.part");
            require(await RejectsAsync(() => ReleaseUpdater.DownloadAsync(http, release, path, default)) && !File.Exists(path),
                "updater rejects redirect to a lookalike CDN before sending a request or writing a file");
        }

        using (var http = new HttpClient(new StubHandler(request => request.RequestUri!.AbsoluteUri == ReleaseUpdater.MetadataUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Metadata()) }
            : Bytes(Enumerable.Repeat((byte)42, Fixture.Length).ToArray()))))
        {
            require(await RejectsAsync(() => ReleaseUpdater.StageLatestAsync(http, current, root, default)) &&
                !Directory.EnumerateFileSystemEntries(Path.Combine(root, "updates")).Any(),
                "updater rejects incorrect SHA-256 and removes partial staging files");
        }

        using (var http = new HttpClient(new StubHandler(request => request.RequestUri!.AbsoluteUri == ReleaseUpdater.MetadataUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Metadata()) } : Bytes(Fixture))))
        {
            require(await RejectsAsync(() => ReleaseUpdater.StageLatestAsync(http, current, root, default)) &&
                !Directory.EnumerateFileSystemEntries(Path.Combine(root, "updates")).Any(),
                "updater rejects a correctly hashed asset without the expected Windows executable identity");
        }

        var stage = Directory.CreateDirectory(Path.Combine(root, "updates", "stage-" + Guid.NewGuid().ToString("N"))).FullName;
        var image = Path.Combine(stage, "PressReleaseWriter.exe");
        File.WriteAllBytes(image, Fixture);
        using var owner = ReleaseUpdater.OpenStageLease(stage);
        var old = DateTime.UtcNow.AddHours(-25);
        foreach (var path in Directory.EnumerateFiles(stage)) File.SetLastWriteTimeUtc(path, old);
        Directory.SetLastWriteTimeUtc(stage, old);
        using var cleanup = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Metadata(tag: "v0.1.9")) }));
        await ReleaseUpdater.CheckLatestAsync(cleanup, current, root, default);
        require(File.Exists(image), "updater preserves a day-old stage while another instance owns it");
        using (ReleaseUpdater.OpenStageLease(stage))
        {
            owner.Dispose(); // The helper opens its lease before the first app releases its own.
            await ReleaseUpdater.CheckLatestAsync(cleanup, current, root, default);
            require(File.Exists(image), "updater preserves the stage during helper handoff");
        }
        await ReleaseUpdater.CheckLatestAsync(cleanup, current, root, default);
        require(!Directory.Exists(stage), "updater removes the orphaned stage once the last lease is released");
    }

    static string Metadata(string tag = Tag, bool draft = false, bool prerelease = false, string? url = null,
        string? digest = null, long? size = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        draft,
        prerelease,
        assets = new[]
        {
            new { name = "PressReleaseWriter.exe", state = "uploaded", browser_download_url = url ?? AssetUrl,
                digest = digest ?? "sha256:" + Digest, size = size ?? Fixture.Length }
        }
    });

    static ReleaseUpdater.ReleaseAsset? Parse(string json, Version current)
    {
        using var parsed = JsonDocument.Parse(json);
        return ReleaseUpdater.SelectRelease(parsed.RootElement, current);
    }

    static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    static async Task<bool> RejectsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    static HttpResponseMessage Redirect(string url)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(url);
        return response;
    }

    static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
