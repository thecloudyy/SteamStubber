using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace SteamStubber;

/// <summary>Steamless unpack engine: port of the steamless.ps1 script.
/// Scans a game's install folder for candidate exes (skipping redist /
/// launcher noise), downloads Steamless.CLI once, and unpacks each exe.</summary>
internal static class StubberService
{
    private static readonly HttpClient Http = new();

    static StubberService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStubber/1.0");
        Http.Timeout = TimeSpan.FromMinutes(10);
    }

    internal const string SteamlessUrl =
        "https://github.com/atom0s/Steamless/releases/download/v3.1.0.5/Steamless.v3.1.0.5.-.by.atom0s.zip";

    // EXE name fragments to ignore (redistributables, third-party launchers, crash handlers...).
    internal static readonly string[] SkipPatterns =
    [
        "redist", "vcredist", "directx", "dxsetup", "vc_redist",
        "dotnet", "setup", "install", "uninstall", "crashreport",
        "crashhandler", "bugsplat", "easyanticheat", "battleye",
        "be_service", "launch", "_launcher", "bootstrap",
        "cefprocess", "cef_process", "steamwebhelper", "uplay",
    ];

    internal static bool ShouldSkip(string exeName)
    {
        string lower = exeName.ToLowerInvariant();
        foreach (string p in SkipPatterns)
        {
            if (lower.Contains(p, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal sealed record ExeEntry(string FullPath, long Size);

    /// <summary>All *.exe under the install folder (optionally recursive).</summary>
    internal static List<ExeEntry> ScanExes(string installPath, bool recursive)
    {
        var found = new List<ExeEntry>();
        try
        {
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string file in Directory.EnumerateFiles(installPath, "*.exe", opt))
            {
                try { found.Add(new ExeEntry(file, new FileInfo(file).Length)); }
                catch { }
            }
        }
        catch { }
        return found;
    }

    internal static List<ExeEntry> FilterCandidates(IEnumerable<ExeEntry> exes) =>
        exes.Where(e => !ShouldSkip(Path.GetFileName(e.FullPath))).ToList();

    private static string ToolDir =>
        Path.Combine(Path.GetTempPath(), "steamless");

    /// <summary>Download + extract Steamless once. Returns the CLI path.</summary>
    internal static async Task<string> EnsureSteamlessAsync(
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ToolDir);

        string? existing = FindCliExe(ToolDir);
        if (existing is not null)
            return existing;

        log?.Report("Downloading Steamless...");
        string zip = Path.Combine(ToolDir, "steamless.zip");
        try
        {
            using var res = await Http.GetAsync(SteamlessUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            await using var net = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(zip);
            await net.CopyToAsync(file, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Steamless download failed: {ex.Message}", ex);
        }

        log?.Report("Extracting Steamless...");
        try
        {
            ZipFile.ExtractToDirectory(zip, ToolDir, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Steamless extraction failed: {ex.Message}", ex);
        }
        finally
        {
            try { File.Delete(zip); } catch { }
        }

        string? cli = FindCliExe(ToolDir);
        if (cli is null)
            throw new InvalidOperationException("Steamless.CLI.exe not found after extraction.");
        return cli;
    }

    private static string? FindCliExe(string dir)
    {
        try
        {
            string direct = Path.Combine(dir, "Steamless.CLI.exe");
            if (File.Exists(direct)) return direct;
            foreach (string f in Directory.EnumerateFiles(dir, "Steamless.CLI.exe", SearchOption.AllDirectories))
                return f;
        }
        catch { }
        return null;
    }

    internal sealed record UnpackResult(bool Unpacked, string Output);

    /// <summary>Run Steamless.CLI on one exe. When it writes
    /// "&lt;exe&gt;.unpacked.exe", the original is replaced. Returns whether
    /// the file was unpacked plus the tool output for logging.</summary>
    internal static async Task<UnpackResult> UnpackExeAsync(
        string cliPath, string exePath, CancellationToken ct)
    {
        string unpacked = exePath + ".unpacked.exe";
        try { if (File.Exists(unpacked)) File.Delete(unpacked); } catch { }

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = $"\"{exePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(cliPath) ?? ToolDir,
        };

        string stdout, stderr;
        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start Steamless.CLI.exe.");
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new UnpackResult(false, $"Failed to run Steamless: {ex.Message}");
        }

        string output = (stdout + "\n" + stderr).Trim();

        if (File.Exists(unpacked))
        {
            try
            {
                File.Delete(exePath);
                File.Move(unpacked, exePath);
                return new UnpackResult(true, output);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(unpacked)) File.Delete(unpacked); } catch { }
                return new UnpackResult(false, $"Unpacked but replace failed: {ex.Message}\n{output}");
            }
        }

        return new UnpackResult(false, output);
    }
}
