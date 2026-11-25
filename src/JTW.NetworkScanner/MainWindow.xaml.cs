using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JTW.NetworkScanner.Core;
using JTW.NetworkScanner.Modules;

namespace JTW.NetworkScanner;

public partial class MainWindow : Window
{
    private ObservableCollection<HostResult> _hosts = new();
    private ListCollectionView _view;
    private CancellationTokenSource _scanCts;
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _scanClock = new();
    private string _lastScanRange = "";
    private UpdateInfo _pendingUpdate;
    private HashSet<int> _portFilter = new();

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"{AppInfo.VersionText} · portable · {AppInfo.AppDir}";
        Title = $"{AppInfo.ProductName} {AppInfo.VersionText}";

        _view = new ListCollectionView(_hosts) { Filter = FilterHost };
        HostsGrid.ItemsSource = _view;

        ModulesList.ItemsSource = ModuleManager.Modules;
        ModuleManager.Modules.CollectionChanged += (_, _) => UpdateModulesHint();

        var s = AppSettings.Current;
        PortsBox.Text = s.Ports;
        ScanPortsCheck.IsChecked = s.ScanPorts;
        ResolveCheck.IsChecked = s.ResolveNames;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _uiTimer.Tick += (_, _) => RefreshCounters(refreshView: true);

        LoadAdapters();
        if (!string.IsNullOrWhiteSpace(s.LastRange)) RangeBox.Text = s.LastRange;

        Loaded += async (_, _) =>
        {
            await Task.Run(OuiDatabase.EnsureLoaded);
            OuiText.Text = $"Vendor database: {OuiDatabase.Count:N0} manufacturers";
            if (AppSettings.Current.CheckForUpdatesOnStartup) await CheckForUpdatesAsync(silent: true);
        };
    }

    // ---------------------------------------------------------------- Adapters / range

    private void LoadAdapters()
    {
        var adapters = NetUtil.GetAdapters();
        AdapterCombo.ItemsSource = adapters;
        if (adapters.Count > 0) AdapterCombo.SelectedIndex = 0;
        else StatusText.Text = "No active IPv4 network adapters found — enter a range manually.";
    }

    private AdapterInfo SelectedAdapter => AdapterCombo.SelectedItem as AdapterInfo;

    private void AdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedAdapter == null) return;
        var cidr = SelectedAdapter.Cidr;
        // Very large local subnets: default to the /24 around this PC.
        if (SelectedAdapter.PrefixLength < 22)
            cidr = $"{NetUtil.NetworkAddress(SelectedAdapter.Address, NetUtil.PrefixToMask(24))}/24";
        RangeBox.Text = cidr;
    }

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e) => LoadAdapters();

    private void DefaultPorts_Click(object sender, RoutedEventArgs e) => PortsBox.Text = PortCatalog.DefaultPortList;

    private void RangeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _scanCts == null) Scan_Click(sender, e);
    }

    // ---------------------------------------------------------------- Scanning

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts != null)
        {
            _scanCts.Cancel();
            ScanButton.IsEnabled = false;
            StatusText.Text = "Stopping…";
            return;
        }

        if (!NetUtil.TryParseRange(RangeBox.Text, out var addresses, out var error))
        {
            MessageBox.Show(error, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ports = PortCatalog.ParsePorts(PortsBox.Text);
        var opt = new ScanOptions
        {
            PingTimeoutMs = AppSettings.Current.PingTimeoutMs,
            PortTimeoutMs = AppSettings.Current.PortTimeoutMs,
            ScanPorts = ScanPortsCheck.IsChecked == true,
            ResolveNames = ResolveCheck.IsChecked == true,
            Ports = ports,
            Adapter = PickAdapterFor(addresses)
        };

        var s = AppSettings.Current;
        s.LastRange = RangeBox.Text.Trim();
        s.Ports = PortsBox.Text.Trim();
        s.ScanPorts = opt.ScanPorts;
        s.ResolveNames = opt.ResolveNames;
        AppSettings.Save();

        var list = addresses.Select(a => new HostResult(a)).ToList();
        _hosts = new ObservableCollection<HostResult>(list);
        _view = new ListCollectionView(_hosts) { Filter = FilterHost };
        HostsGrid.ItemsSource = _view;
        _lastScanRange = RangeBox.Text.Trim();

        ScanProgress.Maximum = list.Count;
        ScanProgress.Value = 0;
        ScanButton.Content = "■  Stop";
        StatusText.Text = $"Scanning {list.Count:N0} addresses…";
        _scanCts = new CancellationTokenSource();
        _scanClock.Restart();
        _uiTimer.Start();

        var progress = new Progress<int>(n => ScanProgress.Value = n);
        try
        {
            var scanner = new Core.NetworkScanner();
            var token = _scanCts.Token;
            await Task.Run(() => scanner.ScanAsync(list, opt, progress, token));
        }
        catch (Exception ex)
        {
            Log.Error("Scan failed", ex);
            MessageBox.Show("Scan failed: " + ex.Message, AppInfo.ProductName);
        }
        finally
        {
            var cancelled = _scanCts.IsCancellationRequested;
            _scanCts.Dispose();
            _scanCts = null;
            _uiTimer.Stop();
            _scanClock.Stop();
            foreach (var h in list.Where(h => h.State is HostState.Pending or HostState.Scanning))
                h.State = cancelled ? HostState.Pending : HostState.Offline;
            ScanButton.Content = "▶  Scan";
            ScanButton.IsEnabled = true;
            RefreshCounters(refreshView: true);
            StatusText.Text = (cancelled ? "Scan stopped" : "Scan complete") +
                $" in {_scanClock.Elapsed.TotalSeconds:0.0}s — {_lastScanRange}, {list.Count:N0} addresses, ports checked: {(opt.ScanPorts ? ports.Count : 0)}.";
        }
    }

    private AdapterInfo PickAdapterFor(List<System.Net.IPAddress> addresses)
    {
        var first = addresses.FirstOrDefault();
        if (first == null) return SelectedAdapter;
        return NetUtil.GetAdapters().FirstOrDefault(a => NetUtil.IsInSubnet(first, a)) ?? SelectedAdapter;
    }

    private void RefreshCounters(bool refreshView)
    {
        int alive = 0, dead = 0;
        foreach (var h in _hosts)
        {
            if (h.State == HostState.Alive) alive++;
            else if (h.State == HostState.Offline) dead++;
        }
        AliveCount.Text = alive.ToString("N0");
        DeadCount.Text = dead.ToString("N0");
        if (_scanCts != null)
            StatusText.Text = $"Scanning {_lastScanRange} — {ScanProgress.Value:N0}/{ScanProgress.Maximum:N0} checked, {alive} alive · {_scanClock.Elapsed.TotalSeconds:0}s";
        if (refreshView && FilterAll.IsChecked != true) RefreshViewKeepSelection();
    }

    private void RefreshViewKeepSelection()
    {
        var sel = HostsGrid.SelectedItem;
        _view.Refresh();
        if (sel != null && _view.Contains(sel)) HostsGrid.SelectedItem = sel;
    }

    // ---------------------------------------------------------------- Filters

    private bool FilterHost(object o)
    {
        if (o is not HostResult h) return false;
        if (FilterAlive?.IsChecked == true && h.State != HostState.Alive) return false;
        if (FilterOffline?.IsChecked == true && h.State != HostState.Offline) return false;
        if (_portFilter.Count > 0 && !h.OpenPorts.Any(_portFilter.Contains)) return false;
        var q = SearchBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            var c = StringComparison.OrdinalIgnoreCase;
            if (!(h.Ip.Contains(q, c) || h.Hostname.Contains(q, c) || h.Vendor.Contains(q, c) || h.Mac.Contains(q, c) || h.PortsText.Contains(q, c)))
                return false;
        }
        return true;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_view == null) return;
        _portFilter = PortFilterBox.Text.Trim().Length == 0 ? new HashSet<int>() : PortCatalog.ParsePorts(PortFilterBox.Text).ToHashSet();
        _view.Refresh();
    }

    // ---------------------------------------------------------------- Grid interaction

    private HostResult SelectedHost => HostsGrid.SelectedItem as HostResult;

    private void HostsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow) dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row) row.IsSelected = true;
    }

    private void HostsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var h = SelectedHost;
        if (h == null) return;
        var order = new[] { PortAction.Https, PortAction.Http, PortAction.Rdp, PortAction.Ssh, PortAction.Smb };
        foreach (var action in order)
        {
            var p = h.OpenPorts.Select(PortCatalog.Get).FirstOrDefault(x => x.Action == action);
            if (p != null) { RunLaunch(p, h); return; }
        }
        OpenModule<LivePingWindow>(w => w.SetTarget(h.Ip, autoStart: true));
    }

    private void HostsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var h = SelectedHost;
        var menu = HostsGrid.ContextMenu;
        menu.Items.Clear();
        if (h == null) { e.Handled = true; return; }

        menu.Items.Add(new MenuItem { Header = string.IsNullOrEmpty(h.Hostname) ? h.Ip : $"{h.Ip}  —  {h.Hostname}", IsEnabled = false });

        var launchable = h.OpenPorts.Select(PortCatalog.Get).Where(p => p.Action != PortAction.None).ToList();
        if (launchable.Count > 0)
        {
            foreach (var p in launchable)
            {
                var item = new MenuItem { Header = LaunchActions.Label(p, h.Ip) };
                var port = p;
                item.Click += (_, _) => RunLaunch(port, h);
                menu.Items.Add(item);
            }
        }
        // Any other open port: offer "try in browser"
        foreach (var p in h.OpenPorts.Select(PortCatalog.Get).Where(p => p.Action == PortAction.None).Take(6))
        {
            var port = p.Port;
            var item = new MenuItem { Header = $"Try port {PortCatalog.Describe(port)} in browser" };
            item.Click += (_, _) => AppInfo.OpenUrl($"http://{h.Ip}:{port}/");
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        AddItem(menu, "📈  Live ping + route map", () => OpenModule<LivePingWindow>(w => w.SetTarget(h.Ip, autoStart: true)));
        AddItem(menu, "🧭  Traceroute", () => OpenModule<TracerouteWindow>(w => w.SetTarget(h.Ip, pathPing: false, autoStart: true)));
        AddItem(menu, "📊  PathPing", () => OpenModule<TracerouteWindow>(w => w.SetTarget(h.Ip, pathPing: true, autoStart: true)));
        AddItem(menu, "⚡  Speed test to this host", () => OpenModule<SpeedTestWindow>(w => w.SetTarget(h.Ip)));
        if (!string.IsNullOrEmpty(h.Mac))
            AddItem(menu, "⏻  Wake-on-LAN", () => SendWol(h));
        AddItem(menu, "↻  Rescan this host", () => RescanHost(h));
        menu.Items.Add(new Separator());
        AddItem(menu, "Copy IP address", () => Clipboard.SetText(h.Ip));
        if (!string.IsNullOrEmpty(h.Mac)) AddItem(menu, "Copy MAC address", () => Clipboard.SetText(h.Mac));
        AddItem(menu, "Copy row", () => Clipboard.SetText(string.Join("\t", RowFor(h))));
    }

    private static void AddItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void RunLaunch(PortInfo p, HostResult h)
    {
        try
        {
            string user = null;
            if (p.Action == PortAction.Ssh)
            {
                user = InputDialog.Ask(this, "SSH", $"Username for {h.Ip} (leave blank to be asked by SSH):", AppSettings.Current.SshUser);
                if (user == null) return;
                AppSettings.Current.SshUser = user.Trim();
                AppSettings.Save();
            }
            LaunchActions.Launch(p, h.Ip, user);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void SendWol(HostResult h)
    {
        try
        {
            var adapter = NetUtil.GetAdapters().FirstOrDefault(a => NetUtil.IsInSubnet(h.Address, a)) ?? SelectedAdapter;
            await WakeOnLan.SendAsync(h.Mac, NetUtil.GetBroadcast(adapter));
            StatusText.Text = $"Wake-on-LAN magic packet sent to {h.Mac} ({h.Ip}).";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, AppInfo.ProductName); }
    }

    private async void RescanHost(HostResult h)
    {
        var opt = new ScanOptions
        {
            PingTimeoutMs = AppSettings.Current.PingTimeoutMs,
            PortTimeoutMs = AppSettings.Current.PortTimeoutMs,
            ScanPorts = ScanPortsCheck.IsChecked == true,
            ResolveNames = ResolveCheck.IsChecked == true,
            Ports = PortCatalog.ParsePorts(PortsBox.Text),
            Adapter = PickAdapterFor(new List<System.Net.IPAddress> { h.Address })
        };
        StatusText.Text = $"Rescanning {h.Ip}…";
        await Task.Run(() => new Core.NetworkScanner().ScanAsync(new[] { h }, opt, null, CancellationToken.None));
        RefreshCounters(refreshView: true);
        StatusText.Text = $"{h.Ip} rescanned: {h.StatusText}.";
    }

    // ---------------------------------------------------------------- Export

    private static readonly string[] ExportHeaders = { "IP address", "Status", "Hostname", "Role", "MAC address", "Vendor", "Latency", "Open ports", "Found by" };

    private static List<string> RowFor(HostResult h) =>
        new() { h.Ip, h.StatusText, h.Hostname, h.Role, h.Mac, h.Vendor, h.LatencyText, h.PortsText, h.DetectedBy };

    private void Export(bool pdf)
    {
        var rows = _view.Cast<HostResult>().Select(h => (IList<string>)RowFor(h)).ToList();
        if (rows.Count == 0) { MessageBox.Show("Nothing to export yet — run a scan first.", AppInfo.ProductName); return; }
        int alive = _hosts.Count(h => h.IsAlive), dead = _hosts.Count(h => h.State == HostState.Offline);
        var subtitle = $"Range {_lastScanRange}  •  {alive} alive, {dead} not alive  •  filter: {(FilterAlive.IsChecked == true ? "online" : FilterOffline.IsChecked == true ? "offline" : "all")}  •  {DateTime.Now:dd MMM yyyy HH:mm}";
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"network-scan-{DateTime.Now:yyyyMMdd-HHmm}",
                Filter = pdf ? "PDF report (*.pdf)|*.pdf" : "CSV spreadsheet (*.csv)|*.csv",
                AddExtension = true
            };
            if (dlg.ShowDialog(this) != true) return;
            if (pdf) Exporter.ExportPdf(dlg.FileName, "Network scan report", subtitle, ExportHeaders, rows);
            else Exporter.ExportCsv(dlg.FileName, ExportHeaders, rows);
            StatusText.Text = "Exported to " + dlg.FileName;
            AppInfo.OpenUrl(dlg.FileName);
        }
        catch (Exception ex) { MessageBox.Show("Export failed: " + ex.Message, AppInfo.ProductName); }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e) => Export(pdf: false);
    private void ExportPdf_Click(object sender, RoutedEventArgs e) => Export(pdf: true);

    // ---------------------------------------------------------------- Modules

    private T OpenModule<T>(Action<T> configure = null) where T : ModuleWindowBase, new() =>
        ModuleManager.Open(this, configure);

    private string SelectedIpOrEmpty => SelectedHost?.IsAlive == true ? SelectedHost.Ip : "";

    private void OpenLivePing_Click(object sender, RoutedEventArgs e) => OpenModule<LivePingWindow>(w => w.SetTarget(SelectedIpOrEmpty, false));
    private void OpenTraceroute_Click(object sender, RoutedEventArgs e) => OpenModule<TracerouteWindow>(w => w.SetTarget(SelectedIpOrEmpty, false, false));
    private void OpenPathPing_Click(object sender, RoutedEventArgs e) => OpenModule<TracerouteWindow>(w => w.SetTarget(SelectedIpOrEmpty, true, false));
    private void OpenSpeedTest_Click(object sender, RoutedEventArgs e) => OpenModule<SpeedTestWindow>(w => w.SetTarget(SelectedIpOrEmpty));

    private void OpenWol_Click(object sender, RoutedEventArgs e) => OpenModule<WakeOnLanWindow>(w =>
    {
        var h = SelectedHost;
        var adapter = SelectedAdapter;
        w.Prefill(h?.Mac, h?.Hostname, adapter != null ? NetUtil.GetBroadcast(adapter).ToString() : "255.255.255.255");
    });

    private void ModuleShow_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.Tag as ModuleWindowBase)?.Retrieve();
    private void ModuleClose_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.Tag as ModuleWindowBase)?.ForceClose();

    private void UpdateModulesHint() =>
        NoModulesText.Visibility = ModuleManager.Modules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- Updates / about

    private async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var info = await Updater.CheckAsync();
            if (info == null)
            {
                if (!silent) MessageBox.Show($"You're on the latest version ({AppInfo.VersionText}).", AppInfo.ProductName);
                return;
            }
            if (silent && AppSettings.Current.SkippedVersion == info.Tag) return;
            _pendingUpdate = info;
            UpdateText.Text = $"Version {info.Tag} is available (you have {AppInfo.VersionText}).";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            if (!silent) MessageBox.Show("Couldn't check for updates: " + ex.Message, AppInfo.ProductName);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(silent: false);

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        if (ModuleManager.Modules.Any(m => m.IsRunning) &&
            MessageBox.Show("Some diagnostics are still running and will be stopped by the update. Continue?", AppInfo.ProductName,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        UpdateNowButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateText.Text = $"Downloading {_pendingUpdate.Tag}…";
        try
        {
            await Updater.ApplyAsync(_pendingUpdate, new Progress<double>(p => UpdateProgress.Value = p));
            UpdateText.Text = $"{_pendingUpdate.Tag} installed. Restarting…";
            AppSettings.Save();
            ModuleManager.CloseAll();
            Updater.Restart();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            UpdateText.Text = "Update failed.";
            UpdateNowButton.IsEnabled = true;
            MessageBox.Show(ex.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(_pendingUpdate?.HtmlUrl ?? AppInfo.RepoUrl + "/releases");

    private void SkipVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate != null) AppSettings.Current.SkippedVersion = _pendingUpdate.Tag;
        AppSettings.Save();
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.RepoUrl);

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show($"{AppInfo.ProductName} {AppInfo.VersionText}\n\nPortable network scanner & diagnostics toolkit by {AppInfo.Brand}.\n\n" +
                        $"Running from: {AppInfo.AppDir}\nSettings & logs: {AppInfo.DataDir}\n\n" +
                        $"MAC vendor data: IEEE OUI registry ({OuiDatabase.Count:N0} entries)\nMaps: Leaflet, © OpenStreetMap contributors, © CARTO\nGeo-IP: ipwho.is\n\n{AppInfo.RepoUrl}",
            "About", MessageBoxButton.OK, MessageBoxImage.Information);

    protected override void OnClosing(CancelEventArgs e)
    {
        _scanCts?.Cancel();
        AppSettings.Current.Ports = PortsBox.Text.Trim();
        AppSettings.Save();
        ModuleManager.CloseAll();
        base.OnClosing(e);
    }
}
