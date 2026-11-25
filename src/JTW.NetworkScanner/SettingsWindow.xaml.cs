using System.Windows;
using System.Windows.Controls;
using JTW.NetworkScanner.Core;

namespace JTW.NetworkScanner;

public class DnsPresetItem : ObservableObject
{
    public DnsPreset Preset { get; init; }

    private string _latency = "";
    public string LatencyText { get => _latency; set => Set(ref _latency, value); }

    private string _marker = "";
    public string CurrentMarker { get => _marker; set => Set(ref _marker, value); }
}

public partial class SettingsWindow : Window
{
    public enum Page { Dns, General, Updates }

    private readonly List<DnsPresetItem> _presets = DnsManager.Presets.Select(p => new DnsPresetItem { Preset = p }).ToList();
    private readonly Func<bool, Task<string>> _checkForUpdates;

    /// <param name="checkForUpdates">Runs an update check in the main window; returns a status message.</param>
    public SettingsWindow(Page page, Func<bool, Task<string>> checkForUpdates)
    {
        InitializeComponent();
        _checkForUpdates = checkForUpdates;
        PresetList.ItemsSource = _presets;
        Tabs.SelectedItem = page switch { Page.General => GeneralTab, Page.Updates => UpdatesTab, _ => DnsTab };

        LoadDnsAdapters();
        LoadGeneral();

        UpdVersionText.Text = $"{AppInfo.ProductName} {AppInfo.VersionText}";
        AutoUpdateCheck.IsChecked = AppSettings.Current.CheckForUpdatesOnStartup;
        UpdStatus.Text = string.IsNullOrEmpty(AppSettings.Current.LastUpdateCheck) ? "" : "Last checked: " + AppSettings.Current.LastUpdateCheck;
    }

    // ---------------------------------------------------------------- DNS

    private DnsAdapter SelectedDnsAdapter => DnsAdapterCombo.SelectedItem as DnsAdapter;
    private DnsPresetItem SelectedPreset => PresetList.SelectedItem as DnsPresetItem;

    private void LoadDnsAdapters(string keepId = null)
    {
        var adapters = DnsManager.GetAdapters();
        DnsAdapterCombo.ItemsSource = adapters;
        DnsAdapterCombo.SelectedItem = adapters.FirstOrDefault(a => a.Id == keepId) ?? adapters.FirstOrDefault();
        if (adapters.Count == 0) CurrentDnsText.Text = "No active network adapters found.";
    }

    private void DnsRefresh_Click(object sender, RoutedEventArgs e) => LoadDnsAdapters(SelectedDnsAdapter?.Id);

    private void DnsAdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var a = SelectedDnsAdapter;
        if (a == null) return;
        CurrentDnsText.Text = a.ServersText;
        CurrentModeText.Text = $"{a.ModeText}  •  matches: {a.MatchedPreset}";
        foreach (var p in _presets) p.CurrentMarker = p.Preset.Name == a.MatchedPreset ? "  ✓ current" : "";
        PresetList.SelectedItem = _presets.FirstOrDefault(p => p.Preset.Name == a.MatchedPreset);
        if (a.MatchedPreset == "Custom" && !a.IsAutomatic)
        {
            CustomPrimary.Text = a.Servers.ElementAtOrDefault(0) ?? "";
            CustomSecondary.Text = a.Servers.ElementAtOrDefault(1) ?? "";
        }
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        CustomPanel.IsEnabled = SelectedPreset?.Preset.IsCustom == true;

    private async void DnsApply_Click(object sender, RoutedEventArgs e)
    {
        var a = SelectedDnsAdapter;
        var p = SelectedPreset?.Preset;
        if (a == null || p == null) { DnsStatus.Text = "Choose an adapter and a DNS provider first."; return; }

        string[] v4 = null, v6 = null;
        if (p.IsCustom)
        {
            var servers = new[] { CustomPrimary.Text.Trim(), CustomSecondary.Text.Trim() }.Where(s => s.Length > 0).ToArray();
            if (servers.Length == 0) { DnsStatus.Text = "Enter at least a primary DNS server."; return; }
            if (servers.Any(s => !System.Net.IPAddress.TryParse(s, out _))) { DnsStatus.Text = "Custom servers must be IP addresses."; return; }
            v4 = servers;
        }
        else if (!p.IsAutomatic)
        {
            v4 = p.IPv4; v6 = p.IPv6;
        }

        var confirm = MessageBox.Show(
            $"Change DNS on '{a.Name}' to {p.Name}{(v4 != null ? " (" + string.Join(", ", v4) + ")" : "")}?\n\nWindows will ask for administrator permission.",
            "Change DNS", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        DnsApplyButton.IsEnabled = false;
        DnsStatus.Text = "Applying… (approve the Windows prompt)";
        try
        {
            await DnsManager.ApplyAsync(a, v4, v6, Ipv6Check.IsChecked == true);
            await Task.Delay(800); // let Windows settle before re-reading
            LoadDnsAdapters(a.Id);
            DnsStatus.Text = $"✓ DNS on '{a.Name}' is now {p.Name}. Cache flushed.";
        }
        catch (OperationCanceledException ex) { DnsStatus.Text = ex.Message; }
        catch (Exception ex)
        {
            Log.Error("DNS change failed", ex);
            DnsStatus.Text = "DNS change failed: " + ex.Message;
        }
        finally { DnsApplyButton.IsEnabled = true; }
    }

    private async void DnsBenchmark_Click(object sender, RoutedEventArgs e)
    {
        BenchmarkButton.IsEnabled = false;
        DnsStatus.Text = "Timing a lookup against each provider…";
        var a = SelectedDnsAdapter;
        var tasks = _presets.Select(async item =>
        {
            string server = item.Preset.IsAutomatic ? a?.Servers.FirstOrDefault(s => !s.Contains(':'))
                          : item.Preset.IsCustom ? (CustomPrimary.Text.Trim().Length > 0 ? CustomPrimary.Text.Trim() : null)
                          : item.Preset.IPv4.FirstOrDefault();
            if (server == null) { item.LatencyText = ""; return; }
            item.LatencyText = "…";
            var ms = await DnsManager.MeasureAsync(server);
            item.LatencyText = ms.HasValue ? $"{ms:0} ms" : "timeout";
        });
        await Task.WhenAll(tasks);
        DnsStatus.Text = "Benchmark done (lower is faster). Results are one lookup from this PC, so run it a couple of times.";
        BenchmarkButton.IsEnabled = true;
    }

    private void DnsFlush_Click(object sender, RoutedEventArgs e)
    {
        try { DnsManager.FlushCache(); DnsStatus.Text = "✓ DNS resolver cache flushed."; }
        catch (Exception ex) { DnsStatus.Text = "Flush failed: " + ex.Message; }
    }

    // ---------------------------------------------------------------- General

    private void LoadGeneral()
    {
        var s = AppSettings.Current;
        SshUserBox.Text = s.SshUser;
        PingTimeoutBox.Text = s.PingTimeoutMs.ToString();
        PortTimeoutBox.Text = s.PortTimeoutMs.ToString();
        SpeedPortBox.Text = s.SpeedTestPort.ToString();
        DefaultPortsBox.Text = s.Ports;
    }

    private void GeneralSave_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        if (!int.TryParse(PingTimeoutBox.Text, out var pt) || pt is < 100 or > 10000) { GeneralStatus.Text = "Ping timeout must be 100–10000 ms."; return; }
        if (!int.TryParse(PortTimeoutBox.Text, out var tt) || tt is < 50 or > 10000) { GeneralStatus.Text = "Port timeout must be 50–10000 ms."; return; }
        if (!int.TryParse(SpeedPortBox.Text, out var sp) || sp is < 1 or > 65535) { GeneralStatus.Text = "Speed test port must be 1–65535."; return; }
        s.SshUser = SshUserBox.Text.Trim();
        s.PingTimeoutMs = pt;
        s.PortTimeoutMs = tt;
        s.SpeedTestPort = sp;
        s.Ports = DefaultPortsBox.Text.Trim();
        AppSettings.Save();
        GeneralStatus.Text = "✓ Saved. New timeouts apply to the next scan.";
        SettingsChanged?.Invoke();
    }

    private void GeneralDefaults_Click(object sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        SshUserBox.Text = d.SshUser;
        PingTimeoutBox.Text = d.PingTimeoutMs.ToString();
        PortTimeoutBox.Text = d.PortTimeoutMs.ToString();
        SpeedPortBox.Text = d.SpeedTestPort.ToString();
        DefaultPortsBox.Text = d.Ports;
        GeneralStatus.Text = "Defaults restored. Press Save to keep them.";
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.DataDir);

    /// <summary>Raised after General settings are saved.</summary>
    public event Action SettingsChanged;

    // ---------------------------------------------------------------- Updates

    private void AutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.CheckForUpdatesOnStartup = AutoUpdateCheck.IsChecked == true;
        AppSettings.Save();
    }

    private async void UpdCheckNow_Click(object sender, RoutedEventArgs e)
    {
        UpdStatus.Text = "Checking GitHub releases…";
        UpdStatus.Text = await _checkForUpdates(false);
    }

    private void UpdReleases_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.RepoUrl + "/releases");
}
