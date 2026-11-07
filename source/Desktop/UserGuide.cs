using System.IO;

namespace PressReleaseDesktop;

static class UserGuide
{
    // A stable app-owned path keeps browser reading preferences across launches.
    public static string Extract(string appFolder)
    {
        using var resource = typeof(UserGuide).Assembly.GetManifestResourceStream("PressReleaseDesktop.UserGuide.html")
            ?? throw new InvalidOperationException("앱에 포함된 사용 안내를 찾지 못했습니다.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var content = buffer.ToArray();
        var directory = Path.Combine(appFolder, "help");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PressReleaseWriter-Guide-ko.html");
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            File.WriteAllBytes(path, content);
        return Path.GetFullPath(path);
    }
}
