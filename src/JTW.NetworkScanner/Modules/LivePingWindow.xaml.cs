using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JTW.NetworkScanner.Core;

namespace JTW.NetworkScanner.Modules;

public partial class LivePingWindow : ModuleWindowBase
{
    private CancellationTokenSource _cts;
    private readonly ObservableCollection<HopInfo> _hops = new();
    private readonly List<(DateTime Time, long? Rtt)> _samples = new();
    private int _sent, _recv;
    private long _min = long.MaxValue, _max, _sum;
    private double _jitterSum; private int _jitterN; private long? _prev;
    private IPAddress _targetIp;
    private string _targetText;

    public LivePingWindow()
    {
        InitializeComponent();
        ModuleName = "Live Ping";
        HopChain.ItemsSource = _hops;
        HopGrid.ItemsSource = _hops;
    }

    public void SetTarget(string target, bool autoStart)
    {
        TargetBox.Text = target ?? "";
        if (autoStart && !string.IsNullOrWhiteSpace(target))
            Loaded += (_, _) => Start_Click(this, new RoutedEventArgs());
    }

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !IsRunning) Start_Click(sender, e);
    }

    public override void StopModule() => _cts?.Cancel();

    private int IntervalMs => int.TryParse((IntervalCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var v) ? v : 1000;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning) { _cts?.Cancel(); return; }

        _targetText = TargetBox.Text.Trim();
        if (_targetText.Length == 0) { ShowError("Enter an IP address or hostname."); return; }
        try { _targetIp = await NetUtil.ResolveAsync(_targetText); }
        catch { _targetIp = null; }
        if (_targetIp == null) { ShowError($"Couldn't resolve '{_targetText}'."); return; }

        ResetStats();
        Title = $"Live Ping — {_targetText}";
        StartButton.Content = "■  Stop";
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        AddLog($"Pinging {_targetText} [{_targetIp}] every {IntervalMs} ms");

        bool external = !NetUtil.IsPrivate(_targetIp);
        Map.ShowMessage(external ? "Locating route…" : "Target is on a private/local network — the world map is used for internet targets. See the Route tab for the hop-by-hop view.");

        if (TraceCheck.IsChecked == true) _ = MapRouteAsync(external, ct);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IntervalMs));
            do
            {
                var target = Diagnostics.PingOnceAsync(_targetIp, Math.Max(1000, IntervalMs));
                var hopPings = _hops.Where(h => h.Address != "*" && h.Address != _targetIp.ToString())
                    .Select(async h => (h, await Diagnostics.PingOnceAsync(IPAddress.Parse(h.Address), 1000)))
                    .ToList();
                var rtt = await target;
                RecordSample(rtt);
                foreach (var t in hopPings)
                {
                    var (hop, r) = await t;
                    hop.AddSample(r);
                }
                var last = _hops.LastOrDefault(h => h.Address == _targetIp.ToString());
                last?.AddSample(rtt);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("Error: " + ex.Message); }
        finally
        {
            IsRunning = false;
            StartButton.Content = "▶  Start";
            AddLog("Stopped.");
            StatusLine.Text = "Stopped. " + StatsSummary();
        }
    }

    private void ResetStats()
    {
        _sent = _recv = 0; _min = long.MaxValue; _max = _sum = 0;
        _jitterSum = 0; _jitterN = 0; _prev = null;
        _samples.Clear(); _hops.Clear(); Graph.Clear(); LogList.Items.Clear();
    }

    private void RecordSample(long? rtt)
    {
        _sent++;
        _samples.Add((DateTime.Now, rtt));
        if (rtt.HasValue)
        {
            _recv++; _sum += rtt.Value;
            _min = Math.Min(_min, rtt.Value); _max = Math.Max(_max, rtt.Value);
            if (_prev.HasValue) { _jitterSum += Math.Abs(rtt.Value - _prev.Value); _jitterN++; }
            _prev = rtt;
            AddLog($"{DateTime.Now:HH:mm:ss}  reply from {_targetIp}: time={rtt} ms");
        }
        else AddLog($"{DateTime.Now:HH:mm:ss}  request timed out");

        Graph.Add(rtt);
        SentText.Text = _sent.ToString();
        RecvText.Text = _recv.ToString();
        var loss = 100.0 * (_sent - _recv) / _sent;
        LossText.Text = $"{loss:0.#}%";
        LossText.Foreground = (System.Windows.Media.Brush)FindResource(loss > 0 ? "BadBrush" : "GoodBrush");
        LastText.Text = rtt.HasValue ? $"{rtt} ms" : "timeout";
        MinAvgMaxText.Text = _recv > 0 ? $"{_min} / {(double)_sum / _recv:0} / {_max} ms" : "—";
        JitterText.Text = _jitterN > 0 ? $"{_jitterSum / _jitterN:0.0} ms" : "—";
        Summary = $"{_targetText}: {LastText.Text}, loss {loss:0.#}%";
        StatusLine.Text = StatsSummary();
    }

    private string StatsSummary() =>
        _sent == 0 ? "" : $"{_targetText}: sent {_sent}, received {_recv}, loss {100.0 * (_sent - _recv) / _sent:0.#}%" +
                          (_recv > 0 ? $", min {_min} ms, avg {(double)_sum / _recv:0.0} ms, max {_max} ms" : "");

    private void AddLog(string line)
    {
        LogList.Items.Add(line);
        if (LogList.Items.Count > 1000) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(line);
    }

    // ---------------------------------------------------------------- Route mapping

    private async Task MapRouteAsync(bool external, CancellationToken ct)
    {
        try
        {
            StatusLine.Text = "Tracing route…";
            await Diagnostics.TracerouteAsync(_targetIp, 30, 1000,
                hop => Dispatcher.Invoke(() => _hops.Add(hop)), ct);
            AddLog($"Route traced: {_hops.Count} hops.");
            if (!external || ct.IsCancellationRequested) return;

            // Geolocate public hops + our own public IP as the starting point
            var points = new List<MapPoint>();
            var self = await GeoIp.LookupAsync(null);
            if (self != null) points.Add(new MapPoint(self.Lat, self.Lon, "You", $"{self.Ip}<br>{self.Label}<br>{self.Org}", "src"));

            foreach (var hop in _hops.Where(h => h.Address != "*" && !h.IsPrivate))
            {
                if (ct.IsCancellationRequested) return;
                var g = await GeoIp.LookupAsync(hop.Address);
                if (g == null) continue;
                hop.Lat = g.Lat; hop.Lon = g.Lon;
                hop.Location = g.Label;
                hop.Org = g.Org;
                bool isDst = hop.Address == _targetIp.ToString();
                // Skip consecutive hops in the same spot so the line stays readable
                if (points.Count > 0 && Math.Abs(points[^1].Lat - g.Lat) < 0.01 && Math.Abs(points[^1].Lon - g.Lon) < 0.01 && !isDst) continue;
                points.Add(new MapPoint(g.Lat, g.Lon, isDst ? _targetText : $"Hop {hop.Ttl}",
                    $"{hop.Address}<br>{hop.Hostname}<br>{g.Label}<br>{g.Org}", isDst ? "dst" : "hop"));
            }
            if (!_hops.Any(h => h.Address == _targetIp.ToString()))
            {
                var g = await GeoIp.LookupAsync(_targetIp.ToString());
                if (g != null) points.Add(new MapPoint(g.Lat, g.Lon, _targetText, $"{_targetIp}<br>{g.Label}<br>{g.Org}", "dst"));
            }

            if (points.Count == 0) { Map.ShowMessage("Couldn't geolocate this route (no internet access to the geo-IP service?)."); return; }
            await Map.SetRouteAsync(points);
            AddLog("World map updated (approximate locations from geo-IP data).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("Route mapping error: " + ex.Message); }
    }

    // ---------------------------------------------------------------- Export

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_sent == 0) { ShowError("Nothing to export yet."); return; }
        var choice = MessageBox.Show("Export the per-hop route table?\n\nYes = route table, No = ping samples", "Export",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        try
        {
            string path;
            if (choice == MessageBoxResult.Yes)
            {
                var headers = new[] { "Hop", "Address", "Hostname", "Last", "Avg", "Min/Max", "Sent", "Loss", "Location", "Network" };
                var rows = _hops.Select(h => (IList<string>)new List<string> { h.Ttl.ToString(), h.Address, h.Hostname, h.RttText, h.AvgText, h.MinMaxText, h.Sent.ToString(), h.LossText, h.Location, h.Org }).ToList();
                path = Exporter.ExportWithDialog("live-ping-route", $"Live ping route — {_targetText}", StatsSummary(), headers, rows);
            }
            else
            {
                var headers = new[] { "Time", "Target", "Result", "RTT (ms)" };
                var rows = _samples.Select(s => (IList<string>)new List<string> { s.Time.ToString("yyyy-MM-dd HH:mm:ss"), _targetIp?.ToString(), s.Rtt.HasValue ? "Reply" : "Timeout", s.Rtt?.ToString() ?? "" }).ToList();
                path = Exporter.ExportWithDialog("live-ping", $"Live ping — {_targetText}", StatsSummary(), headers, rows);
            }
            if (path != null) AppInfo.OpenUrl(path);
        }
        catch (Exception ex) { ShowError("Export failed: " + ex.Message); }
    }
}
