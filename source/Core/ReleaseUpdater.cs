using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("Smoke")]

namespace PressRelease;

public sealed class StagedUpdate
{
    internal StagedUpdate(Version version, string path, string digest, long size)
        => (Version, Path, Digest, Size) = (version, path, digest, size);

    public Version Version { get; }
    internal string Path { get; }
    internal string Digest { get; }
    internal long Size { get; }
}

/// <summary>A newer verified release whose executable has not been downloaded yet.</summary>
public sealed class AvailableUpdate
{
    internal AvailableUpdate(ReleaseUpdater.ReleaseAsset asset) => Asset = asset;

    public Version Version => Asset.Version;
    public long Size => Asset.Size;
    internal ReleaseUpdater.ReleaseAsset Asset { get; }
}

public static class ReleaseUpdater
{
    const string Repository = "newskool4d-sketch/press-release-writer";
    const string AssetName = "PressReleaseWriter.exe";
    const string ApplyFlag = "--apply-update";
    const string StagePrefix = "stage-";
    const string FinishedFile = "finished";
    const string PartFile = "download.part";
    const long MaxAssetBytes = 512L * 1024 * 1024;
    const int MaxMetadataBytes = 1024 * 1024;
    static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
    // A stage without a completion marker belongs to a crashed or killed run once it is this old.
    static readonly TimeSpan OrphanStageAge = TimeSpan.FromHours(24);

    public const string ReleasePage = "https://github.com/" + Repository + "/releases/latest";
    internal const string MetadataUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    static readonly Regex VersionPattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Reads release metadata only. The executable is downloaded later by <see cref="StageAsync(AvailableUpdate, string, CancellationToken)"/>.</summary>
    public static Task<AvailableUpdate?> CheckLatestAsync(Version currentVersion, string appFolder, CancellationToken cancel)
        => CheckLatestAsync(Http, currentVersion, appFolder, cancel);

    public static Task<StagedUpdate> StageAsync(AvailableUpdate update, string appFolder, CancellationToken cancel)
        => StageAsync(Http, update, appFolder, cancel);

    internal static async Task<StagedUpdate?> StageLatestAsync(HttpClient http, Version currentVersion, string appFolder, CancellationToken cancel)
    {
        var available = await CheckLatestAsync(http, currentVersion, appFolder, cancel).ConfigureAwait(false);
        return available == null ? null : await StageAsync(http, available, appFolder, cancel).ConfigureAwait(false);
    }

    internal static async Task<AvailableUpdate?> CheckLatestAsync(HttpClient http, Version currentVersion, string appFolder, CancellationToken cancel)
    {
        CleanStaleStages(UpdatesDirectory(appFolder));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(MetadataTimeout);
        try
        {
            using var request = NewGet(new Uri(MetadataUrl));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new IOException("GitHub 릴리스 정보를 확인하지 못했습니다.");
            await response.Content.LoadIntoBufferAsync(MaxMetadataBytes, limit.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: limit.Token).ConfigureAwait(false);
            var release = SelectRelease(document.RootElement, currentVersion);
            return release == null ? null : new AvailableUpdate(release);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("자동 업데이트 확인 시간이 초과됐습니다.");
        }
        catch (HttpRequestException)
        {
            throw new IOException("GitHub 자동 업데이트에 연결하지 못했습니다.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("GitHub 릴리스 정보 형식이 올바르지 않습니다.");
        }
    }

    internal static async Task<StagedUpdate> StageAsync(HttpClient http, AvailableUpdate update, string appFolder, CancellationToken cancel)
    {
        var release = update.Asset;
        var updates = UpdatesDirectory(appFolder);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(DownloadTimeout);
        try
        {
            Directory.CreateDirectory(updates);
            RequireRegularPath(updates);
            var stage = Directory.CreateDirectory(Path.Combine(updates, StagePrefix + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                var part = Path.Combine(stage, PartFile);
                await DownloadAsync(http, release, part, limit.Token).ConfigureAwait(false);
                if (ReadProductVersion(part) != release.Version)
                    throw new InvalidDataException("업데이트 실행 파일의 제품이나 버전이 일치하지 않습니다.");
                var executable = Path.Combine(stage, AssetName);
                File.Move(part, executable);
                return new StagedUpdate(release.Version, executable, release.Digest, release.Size);
            }
            catch
            {
                CleanStage(stage);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("업데이트 파일 내려받기 시간이 초과됐습니다. 네트워크를 확인한 뒤 다시 시도해 주세요.");
        }
        catch (HttpRequestException)
        {
            throw new IOException("GitHub 업데이트 파일을 내려받지 못했습니다.");
        }
    }

    internal static ReleaseAsset? SelectRelease(JsonElement release, Version currentVersion)
    {
        if (Get(release, "draft").ValueKind != JsonValueKind.False || Get(release, "prerelease").ValueKind != JsonValueKind.False)
            return null;
        var tag = Text(Get(release, "tag_name"));
        if (tag == null || !tag.StartsWith('v') || ParseVersion(tag[1..]) is not { } version ||
            version <= new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build)))
            return null;

        var assets = Get(release, "assets");
        var matches = assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray().Where(asset => Text(Get(asset, "name")) == AssetName).ToArray() : [];
        var expected = $"https://github.com/{Repository}/releases/download/{tag}/{AssetName}";
        if (matches.Length != 1 || Text(Get(matches[0], "state")) != "uploaded" ||
            Text(Get(matches[0], "browser_download_url")) != expected ||
            Text(Get(matches[0], "digest")) is not { } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !IsDigest(digest[7..]) || Get(matches[0], "size").ValueKind != JsonValueKind.Number ||
            !Get(matches[0], "size").TryGetInt64(out var size) || size <= 0 || size > MaxAssetBytes)
            throw new InvalidDataException("최신 릴리스에서 검증 가능한 업데이트 실행 파일을 찾지 못했습니다.");

        return new(version, new Uri(expected), digest[7..], size);
    }

    internal static async Task DownloadAsync(HttpClient http, ReleaseAsset release, string path, CancellationToken cancel)
    {
        var uri = release.Url;
        for (var redirects = 0; redirects <= 4; redirects++)
        {
            if (!IsDownloadHost(uri)) throw new InvalidDataException("GitHub 이외의 업데이트 다운로드 주소가 거부됐습니다.");
            using var request = NewGet(uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } next || !Uri.TryCreate(uri, next, out uri))
                    throw new InvalidDataException("GitHub 다운로드 이동 주소가 올바르지 않습니다.");
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException("GitHub 업데이트 파일을 내려받지 못했습니다.");
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("업데이트 파일 크기가 릴리스 정보와 다릅니다.");

            await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > release.Size) throw new InvalidDataException("업데이트 파일 크기가 릴리스 정보와 다릅니다.");
                hash.AppendData(buffer, 0, count);
                await file.WriteAsync(buffer.AsMemory(0, count), cancel).ConfigureAwait(false);
            }
            if (total != release.Size || !DigestEquals(hash.GetHashAndReset(), release.Digest))
                throw new InvalidDataException("업데이트 파일의 크기 또는 SHA-256 검증에 실패했습니다.");
            await file.FlushAsync(cancel).ConfigureAwait(false);
            return;
        }
        throw new InvalidDataException("GitHub 업데이트 다운로드가 너무 많이 이동했습니다.");
    }

    public static async Task<bool> LaunchApplyAsync(StagedUpdate pending)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } target || ReadProductVersion(target) is not { } oldVersion ||
            pending.Version <= oldVersion) return false;
        var stage = Path.GetDirectoryName(pending.Path)!;
        var updates = Path.GetDirectoryName(stage)!;
        var appFolder = Path.GetDirectoryName(updates)!;
        var id = ValidateStage(pending.Path, appFolder);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var image = File.Open(pending.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (image.Length != pending.Size || !DigestEquals(await SHA256.HashDataAsync(image, limit.Token).ConfigureAwait(false), pending.Digest))
            throw new InvalidDataException("준비된 업데이트 파일이 변경됐습니다.");

        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName(id), out var created);
        if (!created) return false;
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(pending.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = stage
        };
        foreach (var arg in new[] { ApplyFlag, target, current.Id.ToString(CultureInfo.InvariantCulture),
            current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), pending.Digest, pending.Version.ToString(3), appFolder })
            start.ArgumentList.Add(arg);
        using var helper = Process.Start(start);
        if (helper == null) return false;
        return await Task.Run(() =>
        {
            while (!limit.IsCancellationRequested && !helper.HasExited)
                if (ready.WaitOne(100)) return true;
            return ready.WaitOne(0);
        }).ConfigureAwait(false);
    }

    public static bool TryRunApplyMode(string[] args)
    {
        if (args.Length == 0 || args[0] != ApplyFlag) return false;
        Environment.ExitCode = 1;
        if (!OperatingSystem.IsWindows() || args.Length != 7 || Environment.ProcessPath is not { } image ||
            !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[6]) ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 || pid == Environment.ProcessId ||
            !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            !IsDigest(args[4]) || ParseVersion(args[5]) is not { } expected) return true;

        string? stage = null;
        var installed = false;
        try
        {
            var id = ValidateStage(image, args[6]);
            stage = Path.GetDirectoryName(image)!;
            var target = Path.GetFullPath(args[1]);
            if (SamePath(target, image) || !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
                return true;
            RequireRegularPath(target);
            using var old = Process.GetProcessById(pid);
            // Open the handle before acknowledging the parent. It remains tied to the original process.
            _ = old.Handle;
            if (old.HasExited || old.StartTime.ToUniversalTime().Ticks != ticks || old.MainModule?.FileName is not { } running ||
                !SamePath(running, target) || ReadProductVersion(target) is not { } oldVersion || oldVersion >= expected ||
                ReadProductVersion(image) != expected) return true;

            using var source = File.Open(image, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length <= 0 || source.Length > MaxAssetBytes || !DigestEquals(SHA256.HashData(source), args[4])) return true;
            using (var ready = EventWaitHandle.OpenExisting(ReadyEventName(id))) ready.Set();
            if (!old.WaitForExit(45000)) return true;
            source.Position = 0;
            installed = ReplaceAfterExit(source, target, oldVersion, expected, args[4]);
            if (installed) Environment.ExitCode = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or
            ArgumentException or System.ComponentModel.Win32Exception or WaitHandleCannotBeOpenedException or BadImageFormatException)
        {
            // The hidden helper reports only a fixed result. Paths or exception contents may be private.
        }
        finally
        {
            if (stage != null)
            {
                try { File.WriteAllText(Path.Combine(stage, FinishedFile), installed ? "installed" : "failed", Encoding.UTF8); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return true;
    }

    static bool ReplaceAfterExit(Stream source, string target, Version oldVersion, Version expected, string digest)
    {
        var mutexId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\PressReleaseWriter.Replace." + mutexId);
        bool locked;
        try { locked = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { locked = true; }
        if (!locked) return false;
        var suffix = Guid.NewGuid().ToString("N");
        var sibling = target + ".update-" + suffix + ".tmp";
        var backup = target + ".previous-" + suffix + ".bak";
        try
        {
            RequireRegularPath(target);
            if (ReadProductVersion(target) != oldVersion) return false;
            using (var file = new FileStream(sibling, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(file);
                file.Flush(flushToDisk: true);
            }
            using (var staged = File.OpenRead(sibling))
                if (!DigestEquals(SHA256.HashData(staged), digest)) return false;
            if (ReadProductVersion(sibling) != expected || ReadProductVersion(target) != oldVersion) return false;
            try { File.Replace(sibling, target, backup); }
            catch
            {
                if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target);
                throw;
            }
            PruneBackups(target, backup);
            return true;
        }
        finally
        {
            TryDelete(sibling);
            mutex.ReleaseMutex();
        }
    }

    public static void Discard(StagedUpdate pending) => CleanStage(Path.GetDirectoryName(pending.Path)!);

    static string ValidateStage(string image, string appFolder)
    {
        var stage = Path.GetDirectoryName(image) ?? throw new InvalidDataException("업데이트 폴더가 올바르지 않습니다.");
        var name = Path.GetFileName(stage);
        var updates = UpdatesDirectory(appFolder);
        if (Path.GetFileName(image) != AssetName || !IsStageName(name) || !SamePath(Path.GetDirectoryName(stage)!, updates))
            throw new InvalidDataException("준비된 업데이트 경로가 올바르지 않습니다.");
        RequireRegularPath(updates);
        RequireRegularPath(stage);
        RequireRegularPath(image);
        return name[StagePrefix.Length..];
    }

    // Only the newest backup is kept. Older ones from earlier updates are removed after a successful replace.
    static void PruneBackups(string target, string keep)
    {
        var directory = Path.GetDirectoryName(target);
        if (directory == null) return;
        try
        {
            foreach (var backup in Directory.EnumerateFiles(directory, Path.GetFileName(target) + ".previous-*.bak"))
                if (!SamePath(backup, keep)) TryDelete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void CleanStaleStages(string updates)
    {
        if (!Directory.Exists(updates)) return;
        RequireRegularPath(updates);
        var cutoff = DateTime.UtcNow - OrphanStageAge;
        foreach (var stage in Directory.EnumerateDirectories(updates, StagePrefix + "*"))
        {
            if (!IsStageName(Path.GetFileName(stage))) continue;
            // A finished helper leaves a marker. A marker-less stage from a crashed or killed run is removed once it is old;
            // the age guard protects a stage another live instance has just prepared.
            if (File.Exists(Path.Combine(stage, FinishedFile)) || StageTime(stage) < cutoff) CleanStage(stage);
        }
    }

    static DateTime StageTime(string stage)
    {
        try
        {
            var latest = Directory.GetLastWriteTimeUtc(stage);
            foreach (var file in Directory.EnumerateFiles(stage))
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written > latest) latest = written;
            }
            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow; // Treat an unreadable stage as fresh; it is retried on a later launch.
        }
    }

    static void CleanStage(string stage)
    {
        try
        {
            if (!IsStageName(Path.GetFileName(stage)) || !Directory.Exists(stage)) return;
            RequireRegularPath(stage);
            // The helper's own image stays locked until it exits. Keep the completion marker if deletion fails.
            if (!TryDelete(Path.Combine(stage, AssetName))) return;
            TryDelete(Path.Combine(stage, PartFile));
            TryDelete(Path.Combine(stage, FinishedFile));
            Directory.Delete(stage, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static void RequireRegularPath(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("업데이트 경로에서 링크를 사용할 수 없습니다.");
    }

    internal static Version? ReadProductVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            var headers = reader.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus ||
                (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0 ||
                (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0) return null;
        }
        catch (BadImageFormatException) { return null; }
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.ProductName == "PressReleaseWriter" && info.OriginalFilename == "PressReleaseWriter.dll"
            ? ParseVersion(info.ProductVersion?.Split('+', 2)[0]) : null;
    }

    static HttpRequestMessage NewGet(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("PressReleaseWriter-Updater/1.0");
        return request;
    }

    static bool IsDownloadHost(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.IdnHost is
        "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";

    static Version? ParseVersion(string? value) => value != null && value.Length <= 35 && VersionPattern.IsMatch(value) &&
        Version.TryParse(value, out var version) ? version : null;
    static bool IsDigest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    static bool DigestEquals(byte[] bytes, string digest) => string.Equals(Convert.ToHexString(bytes), digest, StringComparison.OrdinalIgnoreCase);
    static string UpdatesDirectory(string appFolder) => Path.Combine(Path.GetFullPath(appFolder), "updates");
    static string ReadyEventName(string stageId) => "Local\\PressReleaseWriter.Update." + stageId;
    static bool IsStageName(string name) => name.StartsWith(StagePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[StagePrefix.Length..], "N", out _);
    static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    static JsonElement Get(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) ? property : default;
    static string? Text(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    internal sealed record ReleaseAsset(Version Version, Uri Url, string Digest, long Size);
}
