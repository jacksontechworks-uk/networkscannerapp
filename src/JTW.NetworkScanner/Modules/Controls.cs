using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JTW.NetworkScanner.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JTW.NetworkScanner.Modules;

/// <summary>Lightweight live line graph (latency, throughput…). Null values are drawn as red "timeout" ticks.</summary>
public class LiveGraph : FrameworkElement
{
    private readonly List<double?> _values = new();
    public int MaxSamples { get; set; } = 150;
    public string Unit { get; set; } = "ms";
    public Brush LineBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x00, 0xB4, 0xFF));

    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x55));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x8F, 0xA3, 0xBF));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B));
    private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x1B, 0x2B));

    public void Add(double? v)
    {
        _values.Add(v);
        if (_values.Count > MaxSamples) _values.RemoveAt(0);
        InvalidateVisual();
    }

    public void Clear()
    {
        _values.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w < 20 || h < 20) return;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));

        double left = 46, top = 8, bottom = 18;
        double plotW = w - left - 8, plotH = h - top - bottom;
        var max = _values.Where(v => v.HasValue).Select(v => v.Value).DefaultIfEmpty(1).Max();
        max = NiceMax(Math.Max(max * 1.15, 1));

        var pen = new Pen(GridBrush, 1);
        for (int i = 0; i <= 4; i++)
        {
            var y = top + plotH * i / 4;
            dc.DrawLine(pen, new Point(left, y), new Point(left + plotW, y));
            var label = (max * (4 - i) / 4).ToString(max >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + Unit;
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, TextBrush, 1.0);
            dc.DrawText(ft, new Point(2, y - 7));
        }

        if (_values.Count < 1) return;
        double step = plotW / Math.Max(1, MaxSamples - 1);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool started = false;
            for (int i = 0; i < _values.Count; i++)
            {
                var x = left + step * (MaxSamples - _values.Count + i);
                var v = _values[i];
                if (!v.HasValue)
                {
                    started = false;
                    dc.DrawRectangle(BadBrush, null, new Rect(x - 1, top + plotH - 10, 2, 10));
                    continue;
                }
                var y = top + plotH - plotH * (v.Value / max);
                if (!started) { ctx.BeginFigure(new Point(x, y), false, false); started = true; }
                else ctx.LineTo(new Point(x, y), true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round }, geo);
    }

    private static double NiceMax(double v)
    {
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (v <= m * mag) return m * mag;
        return 10 * mag;
    }
}

public record MapPoint(double Lat, double Lon, string Label, string Detail, string Kind);

/// <summary>
/// World map (Leaflet in WebView2). Leaflet is embedded in the exe; map tiles are CARTO dark (needs internet).
/// </summary>
public class RouteMap : ContentControl
{
    private WebView2 _web;
    private bool _ready;
    private List<MapPoint> _pending;
    private readonly TextBlock _message;

    public RouteMap()
    {
        _message = new TextBlock
        {
            Text = "Map will appear here for external (internet) targets.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xA3, 0xBF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20)
        };
        Content = _message;
    }

    public void ShowMessage(string text)
    {
        _message.Text = text;
        if (_web == null) Content = _message;
    }

    private async Task EnsureAsync()
    {
        if (_web != null) return;
        try
        {
            var dataDir = Path.Combine(AppInfo.DataDir, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 15, 22, 35) };
            Content = _web;
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            var tcs = new TaskCompletionSource<bool>();
            _web.NavigationCompleted += (_, _) => tcs.TrySetResult(true);
            _web.NavigateToString(BuildHtml());
            await tcs.Task;
            _ready = true;
            if (_pending != null) await PushAsync(_pending);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _web = null;
            ShowMessage("The map needs the Microsoft Edge WebView2 Runtime (built into Windows 11 and most Windows 10 PCs).\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and reopen this module.");
        }
        catch (Exception ex)
        {
            Log.Error("Map init failed", ex);
            _web = null;
            ShowMessage("Map unavailable: " + ex.Message);
        }
    }

    public async Task SetRouteAsync(List<MapPoint> points)
    {
        _pending = points;
        await EnsureAsync();
        if (_ready) await PushAsync(points);
    }

    private async Task PushAsync(List<MapPoint> points)
    {
        try
        {
            var json = JsonSerializer.Serialize(points.Select(p => new { lat = p.Lat, lon = p.Lon, label = p.Label, detail = p.Detail, kind = p.Kind }));
            await _web.ExecuteScriptAsync($"setRoute({json});");
        }
        catch (Exception ex) { Log.Error("Map update failed", ex); }
    }

    private static string ReadResource(string name)
    {
        using var s = typeof(RouteMap).Assembly.GetManifestResourceStream(name);
        if (s == null) return "";
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static string BuildHtml() => MapHtmlTemplate
        .Replace("/*LEAFLET_CSS*/", ReadResource("leaflet.css"))
        .Replace("/*LEAFLET_JS*/", ReadResource("leaflet.js"));

    private const string MapHtmlTemplate = """
<!DOCTYPE html><html><head><meta charset="utf-8">
<style>/*LEAFLET_CSS*/
html,body,#map{height:100%;margin:0;background:#0F1623;font-family:Segoe UI,sans-serif}
.hop{background:#00B4FF;color:#0B1220;border-radius:50%;width:22px;height:22px;line-height:22px;text-align:center;font-weight:700;font-size:11px;border:2px solid #fff;box-shadow:0 0 8px #00B4FF}
.hop.src{background:#2ECC71;box-shadow:0 0 8px #2ECC71}.hop.dst{background:#F5A623;box-shadow:0 0 10px #F5A623}
.leaflet-popup-content{font-size:12px}
@keyframes dash{to{stroke-dashoffset:-40}} .live{animation:dash 1.2s linear infinite}
</style>
<script>/*LEAFLET_JS*/</script></head><body><div id="map"></div>
<script>
var map=L.map('map',{worldCopyJump:true}).setView([30,0],2);
L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',{maxZoom:19,subdomains:'abcd',
 attribution:'&copy; OpenStreetMap contributors &copy; CARTO'}).addTo(map);
var layer=L.layerGroup().addTo(map);
function setRoute(pts){
 layer.clearLayers(); if(!pts||!pts.length) return;
 var ll=pts.map(p=>[p.lat,p.lon]);
 L.polyline(ll,{color:'#00B4FF',weight:3,opacity:0.9,dashArray:'10 10',className:'live'}).addTo(layer);
 pts.forEach(function(p,i){
  var cls='hop'+(p.kind==='src'?' src':p.kind==='dst'?' dst':'');
  var txt=p.kind==='src'?'⌂':(p.kind==='dst'?'★':p.label);
  var m=L.marker([p.lat,p.lon],{icon:L.divIcon({className:'',html:'<div class="'+cls+'">'+txt+'</div>',iconSize:[22,22],iconAnchor:[11,11]})}).addTo(layer);
  m.bindPopup('<b>'+p.label+'</b><br>'+p.detail);
 });
 if(ll.length>1) map.fitBounds(L.latLngBounds(ll).pad(0.3)); else map.setView(ll[0],6);
}
</script></body></html>
""";
}

/// <summary>
/// ItemsControl that never treats its items as their own containers (the running-module list holds Window objects,
/// which can't be placed in a visual tree).
/// </summary>
public class ModuleListControl : ItemsControl
{
    protected override bool IsItemItsOwnContainerOverride(object item) => false;
    protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();
}
