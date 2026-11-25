using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Input;
using JTW.NetworkScanner.Core;

namespace JTW.NetworkScanner.Modules;

public partial class TracerouteWindow : ModuleWindowBase
{
    private CancellationTokenSource _cts;
    private readonly ObservableCollection<HopInfo> _hops = new();
    private string _target = "";

    public TracerouteWindow()
    {
        InitializeComponent();
        ModuleName = "Traceroute";
        HopGrid.ItemsSource = _hops;
    }

    public void SetTarget(string target, bool pathPing, bool autoStart)
    {
        TargetBox.Text = target ?? "";
        ModePath.IsChecked = pathPing;
        ModeTrace.IsChecked = !pathPing;
        Title = pathPing ? "PathPing" : "Traceroute";
        if (autoStart && !string.IsNullOrWhiteSpace(target))
            Loaded += (_, _) => Start_Click(this, new RoutedEventArgs());
    }

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !IsRunning) Start_Click(sender, e);
    }

    public override void StopModule() => _cts?.Cancel();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning) { _cts?.Cancel(); return; }

        _target = TargetBox.Text.Trim();
        if (_target.Length == 0) { ShowError("Enter a target IP or hostname."); return; }
        IPAddress ip;
        try { ip = await NetUtil.ResolveAsync(_target); } catch { ip = null; }
        if (ip == null) { ShowError($"Couldn't resolve '{_target}'."); return; }

        int maxHops = int.TryParse(MaxHopsBox.Text, out var mh) ? Math.Clamp(mh, 1, 64) : 30;
        int probes = int.TryParse(ProbesBox.Text, out var pr) ? Math.Clamp(pr, 5, 500) : 50;
        bool pathPing = ModePath.IsChecked == true;

        _hops.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        StartButton.Content = "■  Stop";
        Title = $"{(pathPing ? "PathPing" : "Traceroute")} — {_target}";
        Progress.IsIndeterminate = true;

        try
        {
            StatusLine.Text = $"Tracing route to {_target} [{ip}] (max {maxHops} hops)…";
            Summary = "Tracing…";
            await Diagnostics.TracerouteAsync(ip, maxHops, 1000, hop => Dispatcher.Invoke(() =>
            {
                _hops.Add(hop);
                Summary = $"Hop {hop.Ttl}: {hop.Address}";
            }), ct);

            if (GeoCheck.IsChecked == true && !NetUtil.IsPrivate(ip))
            {
                foreach (var hop in _hops.Where(h => h.Address != "*" && !h.IsPrivate))
                {
                    if (ct.IsCancellationRequested) break;
                    var g = await GeoIp.LookupAsync(hop.Address);
                    if (g != null) { hop.Location = g.Label; hop.Org = g.Org; }
                }
            }

            if (pathPing && !ct.IsCancellationRequested)
            {
                StatusLine.Text = $"Computing statistics: {probes} probes per hop…";
                Summary = "Measuring per-hop loss…";
                await Diagnostics.PathPingAsync(_hops, probes, 250, 1000, ct);
                var worst = _hops.Where(h => h.Sent > 1).OrderByDescending(h => h.LossPercent).FirstOrDefault();
                StatusLine.Text = worst != null && worst.LossPercent > 0
                    ? $"Done. Highest loss at hop {worst.Ttl} ({worst.Address}): {worst.LossText}. Note: routers often de-prioritise ICMP, so loss that doesn't carry on to later hops is usually harmless."
                    : "Done. No packet loss detected on any hop.";
            }
            else if (!ct.IsCancellationRequested)
            {
                var reached = _hops.Any(h => h.Address == ip.ToString());
                StatusLine.Text = reached ? $"Trace complete: {_target} reached in {_hops.Count} hops." : $"Trace finished after {_hops.Count} hops without a reply from the target (it may block ICMP).";
            }
            Summary = StatusLine.Text;
        }
        catch (OperationCanceledException) { StatusLine.Text = "Stopped."; }
        catch (Exception ex) { StatusLine.Text = "Error: " + ex.Message; }
        finally
        {
            IsRunning = false;
            StartButton.Content = "▶  Start";
            Progress.IsIndeterminate = false;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_hops.Count == 0) { ShowError("Nothing to export yet."); return; }
        var headers = new[] { "Hop", "Address", "Hostname", "RTT", "Sent", "Loss", "Avg", "Min/Max", "Location", "Network" };
        var rows = _hops.Select(h => (IList<string>)new List<string> { h.Ttl.ToString(), h.Address, h.Hostname, h.RttText, h.Sent.ToString(), h.LossText, h.AvgText, h.MinMaxText, h.Location, h.Org }).ToList();
        try
        {
            var path = Exporter.ExportWithDialog(ModePath.IsChecked == true ? "pathping" : "traceroute", $"{(ModePath.IsChecked == true ? "PathPing" : "Traceroute")} — {_target}", StatusLine.Text, headers, rows);
            if (path != null) AppInfo.OpenUrl(path);
        }
        catch (Exception ex) { ShowError("Export failed: " + ex.Message); }
    }
}
