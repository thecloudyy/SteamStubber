using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;

namespace SteamStubber;

/// <summary>Self-update: on launch the SHA256 of the running exe is compared
/// against the hash published with the latest release. On mismatch the user
/// is offered the new build, which is hash-verified before it replaces us.</summary>
internal static class UpdateService
{
    internal const string ExeUrl =
        "https://github.com/thecloudyy/SteamStubber/releases/latest/download/SteamStubber.exe";
    internal const string HashUrl =
        "https://github.com/thecloudyy/SteamStubber/releases/latest/download/SteamStubber.exe.sha256";

    private static readonly HttpClient Http = new();

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStubber/1.0");
        Http.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>Hash published with the latest release (first token of the file).</summary>
    internal static async Task<string?> GetRemoteHashAsync(CancellationToken ct)
    {
        try
        {
            string body = (await Http.GetStringAsync(HashUrl, ct).ConfigureAwait(false)).Trim();
            string hash = body.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$") ? hash : null;
        }
        catch { return null; }
    }

    internal static string? GetLocalHash()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(exe);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
        catch { return null; }
    }

    internal static bool VerifyHash(string path, string expected)
    {
        try
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static async Task DownloadAsync(string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var res = await Http.GetAsync(ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        long? total = res.Content.Headers.ContentLength;
        await using var net = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(dest);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await net.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total is > 0) progress?.Report(100.0 * done / total.Value);
        }
    }

    /// <summary>Swap in the verified download and restart. The running exe is
    /// locked until we exit, so a helper waits for our PID, moves the new
    /// file into place, and relaunches.</summary>
    internal static void ApplyUpdateAndRestart(string newExePath)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
                $"-NoProfile -WindowStyle Hidden -Command \"Wait-Process -Id {Environment.ProcessId}; " +
                $"Move-Item -LiteralPath '{newExePath}' -Destination '{exe}' -Force; Start-Process -FilePath '{exe}'\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
        Application.Current.Shutdown();
    }
}
