using System.Collections.ObjectModel;
using System.Windows;
using JTW.NetworkScanner.Core;

namespace JTW.NetworkScanner.Modules;

public class SpeedResult
{
    public DateTime Time { get; set; }
    public string Server { get; set; }
    public string Direction { get; set; }
    public int Seconds { get; set; }
    public double Mbps { get; set; }
}

public partial class SpeedTestWindow : ModuleWindowBase
{
    private CancellationTokenSource _clientCts, _serverCts;
    private readonly ObservableCollection<SpeedResult> _history = new();
    private bool _clientRunning, _serverRunning;

    public SpeedTestWindow()
    {
        InitializeComponent();
        ModuleName = "LAN Speed Test";
        HistoryGrid.ItemsSource = _history;
        PortBox.Text = AppSettings.Current.SpeedTestPort.ToString();
        ServerPortBox.Text = AppSettings.Current.SpeedTestPort.ToString();
        var ips = NetUtil.GetAdapters().Select(a => $"{a.Address} ({a.Name})");
        LocalIpsText.Text = "This PC's addresses: " + string.Join("   •   ", ips);
    }

    public void SetTarget(string host) => HostBox.Text = host ?? "";

    public override void StopModule()
    {
        _clientCts?.Cancel();
        _serverCts?.Cancel();
    }

    private void UpdateRunning()
    {
        IsRunning = _clientRunning || _serverRunning;
        Summary = _serverRunning ? $"Server listening on {ServerPortBox.Text}" : _clientRunning ? $"Testing {HostBox.Text}…" : Summary;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_clientRunning) { _clientCts?.Cancel(); return; }
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { ShowError("Enter the IP of a PC running this app's speed test server."); return; }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) { ShowError("Invalid port."); return; }
        int secs = int.TryParse(SecondsBox.Text, out var s) ? Math.Clamp(s, 2, 60) : 10;

        var dirs = new List<bool>();
        if (DirBoth.IsChecked == true || DirUp.IsChecked == true) dirs.Add(true);
        if (DirBoth.IsChecked == true || DirDown.IsChecked == true) dirs.Add(false);

        _clientCts = new CancellationTokenSource();
        _clientRunning = true; UpdateRunning();
        RunButton.Content = "■  Stop";
        Title = $"LAN Speed Test — {host}";
        Graph.Clear();

        try
        {
            foreach (var upload in dirs)
            {
                var label = upload ? "Upload" : "Download";
                (upload ? UpText : DownText).Text = "testing…";
                var progress = new Progress<SpeedTest.Sample>(x =>
                {
                    Graph.Add(x.Mbps);
                    NowText.Text = $"{x.Mbps:0} Mbps";
                });
                var mbps = await Task.Run(() => SpeedTest.RunClientAsync(host, port, upload, secs, progress, _clientCts.Token));
                (upload ? UpText : DownText).Text = $"{mbps:0.0} Mbps";
                _history.Insert(0, new SpeedResult { Time = DateTime.Now, Server = $"{host}:{port}", Direction = label, Seconds = secs, Mbps = mbps });
                Summary = $"{host}: {label} {mbps:0} Mbps";
            }
            AppSettings.Current.SpeedTestPort = port;
        }
        catch (OperationCanceledException) { NowText.Text = "stopped"; }
        catch (Exception ex)
        {
            NowText.Text = "failed";
            ShowError($"Speed test failed: {ex.Message}\n\nCheck that the other PC is running the server (Server tab) on port {port} and that Windows Firewall allows it.");
        }
        finally
        {
            _clientRunning = false; UpdateRunning();
            RunButton.Content = "▶  Run test";
        }
    }

    private async void Server_Click(object sender, RoutedEventArgs e)
    {
        if (_serverRunning) { _serverCts?.Cancel(); return; }
        if (!int.TryParse(ServerPortBox.Text, out var port) || port is < 1 or > 65535) { ShowError("Invalid port."); return; }

        _serverCts = new CancellationTokenSource();
        _serverRunning = true; UpdateRunning();
        ServerButton.Content = "■  Stop server";
        try
        {
            await Task.Run(() => SpeedTest.RunServerAsync(port, msg => Dispatcher.Invoke(() =>
            {
                ServerLog.Items.Add($"{DateTime.Now:HH:mm:ss}  {msg}");
                ServerLog.ScrollIntoView(ServerLog.Items[^1]);
                Summary = msg;
            }), _serverCts.Token));
        }
        catch (Exception ex) { ShowError("Server error: " + ex.Message); }
        finally
        {
            _serverRunning = false; UpdateRunning();
            ServerButton.Content = "▶  Start server";
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) { ShowError("No results to export yet."); return; }
        var headers = new[] { "Time", "Server", "Direction", "Duration (s)", "Mbps" };
        var rows = _history.Select(r => (IList<string>)new List<string> { r.Time.ToString("yyyy-MM-dd HH:mm:ss"), r.Server, r.Direction, r.Seconds.ToString(), r.Mbps.ToString("0.0") }).ToList();
        try
        {
            var path = Exporter.ExportWithDialog("lan-speedtest", "LAN speed test results", $"From {Environment.MachineName}", headers, rows);
            if (path != null) AppInfo.OpenUrl(path);
        }
        catch (Exception ex) { ShowError("Export failed: " + ex.Message); }
    }
}
