using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SteamStubber;

/// <summary>Installed Steam games: registry Steam detection, libraryfolders.vdf
/// parsing, appmanifest_*.acf scanning, and cover art (same CDN strategy as
/// Element / ManifestDownloader).</summary>
internal static class LibraryService
{
    private static readonly HttpClient Http = new();

    static LibraryService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStubber/1.0");
        Http.Timeout = TimeSpan.FromSeconds(60);
    }

    internal sealed record SteamLibrary(string Path);

    internal sealed record GameEntry(long AppId, string Name, string InstallDir, string LibraryPath);

    // ── Steam detection (same method as Element / ManifestDownloader) ──

    private static readonly (RegistryHive Hive, string SubKey, string Value)[] RegistryLocations =
    [
        (RegistryHive.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamPath"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
    ];

    internal static string? DetectSteamDir()
    {
        foreach (var (hive, subKey, value) in RegistryLocations)
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(subKey);
                if (key?.GetValue(value) is string raw && !string.IsNullOrWhiteSpace(raw))
                {
                    string p = Normalize(raw);
                    if (File.Exists(Path.Combine(p, "steam.exe")))
                        return p;
                }
            }
            catch { }
        }
        return null;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path.Trim().Replace('/', '\\')); }
        catch { return path.Trim().Replace('/', '\\'); }
    }

    // ── Library folders: steamapps\libraryfolders.vdf ("path" entries) ──

    private static readonly Regex LibraryPathRx = new(
        "\"path\"\\s+\"([^\"]+)\"",
        RegexOptions.Compiled);

    internal static IReadOnlyList<string> GetLibraryPaths(string steamDir)
    {
        var libs = new List<string> { steamDir };
        string vdf = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                string content = File.ReadAllText(vdf);
                foreach (Match m in LibraryPathRx.Matches(content))
                {
                    string p = Normalize(m.Groups[1].Value.Replace("\\\\", "\\"));
                    if (!libs.Contains(p, StringComparer.OrdinalIgnoreCase))
                        libs.Add(p);
                }
            }
        }
        catch { }
        return libs;
    }

    // ── appmanifest_<appid>.acf parsing ──

    private static readonly Regex ManifestNameRx = new("\"name\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    private static readonly Regex ManifestDirRx = new("\"installdir\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");

    /// <summary>All installed games across every library folder.</summary>
    internal static List<GameEntry> EnumerateInstalledGames(string steamDir)
    {
        var games = new List<GameEntry>();
        foreach (string lib in GetLibraryPaths(steamDir))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    string fileName = Path.GetFileNameWithoutExtension(manifest); // appmanifest_489830
                    if (!long.TryParse(fileName["appmanifest_".Length..], out long appId)) continue;
                    string text = File.ReadAllText(manifest);
                    string? name = ManifestNameRx.Match(text) is { Success: true } nm ? Unescape(nm.Groups[1].Value) : null;
                    string? installDir = ManifestDirRx.Match(text) is { Success: true } dm ? Unescape(dm.Groups[1].Value) : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)) continue;
                    games.Add(new GameEntry(appId, name, installDir, lib));
                }
                catch { }
            }
        }
        return games;
    }

    /// <summary>Full install folder for a game: &lt;library&gt;\steamapps\common\&lt;installdir&gt;.</summary>
    internal static string GetInstallPath(GameEntry game) =>
        Path.Combine(game.LibraryPath, "steamapps", "common", game.InstallDir);

    /// <summary>Resolve a typed AppID to a game entry (searches all libraries).</summary>
    internal static GameEntry? FindByAppId(string steamDir, long appId) =>
        EnumerateInstalledGames(steamDir).FirstOrDefault(g => g.AppId == appId);

    // ── Cover art (same CDN ladder as ManifestDownloader) ──

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamStubber");

    internal static async Task<string?> EnsureCoverAsync(long appId, CancellationToken ct)
    {
        string dir = Path.Combine(AppDataDir, "covers");
        string path = Path.Combine(dir, $"{appId}.jpg");
        if (IsValidImage(path)) return path;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        string[] urls =
        [
            Cdn(appId, "header.jpg"),
            Cdn(appId, "capsule_616x353.jpg"),
            Cdn(appId, "library_600x900.jpg"),
            Cdn(appId, "hero_capsule.jpg"),
            Cdn(appId, "library_hero.jpg"),
            Cdn(appId, "capsule_184x69.jpg"),
            Cdn(appId, "logo.png"),
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_616x353.jpg",
            $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/header.jpg",
        ];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string? hit = await DownloadFirstValidAsync(urls, path, ct);
            if (hit is not null) return hit;
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return null;
    }

    private static string Cdn(long appId, string file) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{file}";

    private static async Task<string?> DownloadFirstValidAsync(IEnumerable<string> urls, string path, CancellationToken ct)
    {
        foreach (string url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) continue;
                byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (!IsJpegBytes(bytes)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                return path;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return null;
    }

    private static bool IsValidImage(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 512) return false;
            byte[] head = new byte[4];
            using var fs = File.OpenRead(path);
            if (fs.Read(head, 0, 4) < 4) return false;
            return IsJpeg(head) || IsPng(head);
        }
        catch { return false; }
    }

    private static bool IsJpegBytes(byte[] b) => b.Length >= 512 && IsJpeg(b);

    private static bool IsJpeg(byte[] b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsPng(byte[] b) =>
        b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    private static Color HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = (h / 60) switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Monogram tile for games with no CDN art, so every row shows
    /// something — gradient hue derived from the AppID. Cached as {id}.placeholder.png.</summary>
    internal static string? GeneratePlaceholder(long appId, string name)
    {
        try
        {
            string dir = Path.Combine(AppDataDir, "covers");
            string path = Path.Combine(dir, $"{appId}.placeholder.png");
            if (File.Exists(path) && new FileInfo(path).Length >= 512) return path;
            var words = name.Split([' ', ':', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            string initial = words.Length switch
            {
                0 => "?",
                1 => words[0].Length > 0 ? char.ToUpperInvariant(words[0][0]).ToString() : "?",
                _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}",
            };
            const int w = 184, h = 86;
            double hue = ((appId % 360) * 47 + 360) % 360;
            var from = HslToRgb(hue, 0.44, 0.33);
            var to = HslToRgb((hue + 34) % 360, 0.52, 0.15);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(from, to, 55), null, new Rect(0, 0, w, h));
                var text = new FormattedText(initial,
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    40, new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)), 1.0);
                dc.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(dir);
            using var fs = File.Create(path);
            enc.Save(fs);
            return path;
        }
        catch { return null; }
    }
}
