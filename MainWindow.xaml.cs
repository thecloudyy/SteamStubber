using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SteamStubber;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private CancellationTokenSource? _cts;
    private AppSettings _settings = new();
    private readonly Dictionary<string, LibraryService.GameEntry> _entriesByAppId = new(StringComparer.Ordinal);
    private readonly List<string> _allGames = new();
    private bool _loadingLib;

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();

        UpdateSteamStatus();
        Loaded += async (_, _) =>
        {
            HookFilterBox();
            await LoadLibraryAsync();
        };
        Closed += (_, _) =>
        {
            try { _cts?.Cancel(); } catch { }
            SaveSettings();
        };
    }

    // ── Games dropdown ──

    private async Task LoadLibraryAsync()
    {
        if (_loadingLib) return;
        _loadingLib = true;
        BtnRefresh.IsEnabled = false;
        try
        {
            string? steamDir = LibraryService.DetectSteamDir();
            if (steamDir is null)
            {
                Status("Steam not found.");
                UpdateSteamStatus();
                return;
            }
            UpdateSteamStatus();
            var games = await Task.Run(() => LibraryService.EnumerateInstalledGames(steamDir));
            _entriesByAppId.Clear();
            _allGames.Clear();
            CmbGames.Items.Clear();
            foreach (var g in games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                string id = g.AppId.ToString();
                _entriesByAppId[id] = g;
                string label = $"{g.Name} ({id})";
                _allGames.Add(label);
                CmbGames.Items.Add(label);
            }
            Status($"{games.Count} game(s) found.");
            if (!string.IsNullOrWhiteSpace(_settings.LastAppId) &&
                _entriesByAppId.TryGetValue(_settings.LastAppId, out var last))
                CmbGames.SelectedItem = $"{last.Name} ({_settings.LastAppId})";
        }
        catch (Exception ex)
        {
            Status($"Library failed: {ex.Message}");
        }
        finally
        {
            _loadingLib = false;
            BtnRefresh.IsEnabled = BtnUnpack.IsEnabled;
        }
    }

    private static string? ParseAppId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\((\d+)\)\s*$");
        if (m.Success) return m.Groups[1].Value;
        return Regex.IsMatch(text.Trim(), @"^\d+$") ? text.Trim() : null;
    }

    private string? CurrentAppId() =>
        ParseAppId(CmbGames.SelectedItem as string ?? CmbGames.Text);

    private void CmbGames_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInstallLabel();

    /// <summary>Filter-as-you-type: narrowing the dropdown to matches keeps
    /// picking fast with large libraries.</summary>
    private void HookFilterBox()
    {
        if (CmbGames.Template.FindName("PART_EditableTextBox", CmbGames) is TextBox box)
            box.TextChanged += FilterBox_TextChanged;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = ((TextBox)sender).Text.Trim();
        // An exact pick (or programmatic set) leaves the list alone.
        if (_allGames.Contains(q)) return;
        CmbGames.Items.Clear();
        foreach (var g in _allGames.Where(g =>
                     q.Length == 0 || g.Contains(q, StringComparison.OrdinalIgnoreCase)))
            CmbGames.Items.Add(g);
        CmbGames.IsDropDownOpen = true;
    }

    private void CmbGames_DropDownOpened(object? sender, EventArgs e)
    {
        // Reopening after a pick shows the whole library again.
        if (_allGames.Contains(CmbGames.Text.Trim()))
        {
            CmbGames.Items.Clear();
            foreach (var g in _allGames) CmbGames.Items.Add(g);
        }
    }

    private void CmbGames_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            UpdateInstallLabel();
            _ = RunUnpackAsync();
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadLibraryAsync();

    private void UpdateInstallLabel()
    {
        string? id = CurrentAppId();
        if (id is not null && _entriesByAppId.TryGetValue(id, out var entry))
            LblInstallPath.Text = LibraryService.GetInstallPath(entry);
        else if (id is not null)
            LblInstallPath.Text = $"AppID {id} is not installed.";
        else
            LblInstallPath.Text = "Pick a game.";
    }

    // ── Buttons ──

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _ = LoadLibraryAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _cts is not null)
        {
            _cts.Cancel();
            e.Handled = true;
        }
    }

    private async void BtnUnpack_Click(object sender, RoutedEventArgs e) => await RunUnpackAsync();

    // ── Core flow ──

    private async Task RunUnpackAsync()
    {
        if (!BtnUnpack.IsEnabled) return;
        string? appId = CurrentAppId();
        if (appId is null)
        {
            Status("Pick a game or type an AppID.");
            return;
        }

        string? steamDir = LibraryService.DetectSteamDir();
        if (steamDir is null) { Status("Steam not found."); return; }

        LibraryService.GameEntry? entry = _entriesByAppId.TryGetValue(appId, out var cached)
            ? cached
            : LibraryService.FindByAppId(steamDir, long.Parse(appId));
        if (entry is null)
        {
            Status($"AppID {appId} is not installed.");
            return;
        }

        string installPath = LibraryService.GetInstallPath(entry);
        if (!Directory.Exists(installPath))
        {
            Status("Install folder missing.");
            return;
        }

        SaveSettings();
        FileBox.Items.Clear();
        FileBox.Visibility = Visibility.Collapsed;

        var all = await Task.Run(() => StubberService.ScanExes(installPath, recursive: true));
        var candidates = StubberService.FilterCandidates(all);
        if (candidates.Count == 0)
        {
            Status($"{all.Count} exe(s) found, all skipped.");
            return;
        }

        var targets = candidates
            .OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SetRunning(true);
        Status($"Unpacking {targets.Count} file(s)…");
        ProgOverall.Value = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int done = 0, ok = 0, left = 0;

        try
        {
            string cli;
            try
            {
                Status("Preparing Steamless…");
                cli = await StubberService.EnsureSteamlessAsync(null, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Status($"Steamless failed: {ex.Message}");
                return;
            }

            int total = targets.Count;
            for (int i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(targets[i].FullPath);
                Status($"{done}/{total} — {name}");

                var result = await StubberService.UnpackExeAsync(cli, targets[i].FullPath, ct);
                if (result.Unpacked)
                {
                    ok++;
                    FileBox.Items.Add($"{name} — unpacked");
                    FileBox.Visibility = Visibility.Visible;
                }
                else
                {
                    left++;
                }

                done++;
                ProgOverall.Value = 100.0 * done / total;
            }

            Status($"Done: {ok} unpacked, {left} left as-is.");
        }
        catch (OperationCanceledException)
        {
            Status("Cancelled.");
        }
        catch (Exception ex)
        {
            Status($"Error: {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    // ── Helpers ──

    private void SetRunning(bool running)
    {
        BtnUnpack.IsEnabled = !running;
        BtnRefresh.IsEnabled = !running && !_loadingLib;
        CmbGames.IsEnabled = !running;
    }

    private void UpdateSteamStatus()
    {
        string? steamDir = LibraryService.DetectSteamDir();
        LblSteamPath.Text = steamDir is null ? "Steam: not found" : $"Steam: {steamDir}";
    }

    private void SaveSettings()
    {
        if (CurrentAppId() is { } id)
            _settings.LastAppId = id;
        SettingsStore.Save(_settings);
    }

    private void Status(string message)
    {
        void Write() => LblStatus.Text = message;
        if (Dispatcher.CheckAccess()) Write();
        else Dispatcher.InvokeAsync(Write);
    }
}
