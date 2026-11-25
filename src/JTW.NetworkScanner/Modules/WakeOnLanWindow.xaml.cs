using System.Net;
using System.Windows;
using JTW.NetworkScanner.Core;

namespace JTW.NetworkScanner.Modules;

public partial class WakeOnLanWindow : ModuleWindowBase
{
    private readonly List<IList<string>> _sent = new();

    public WakeOnLanWindow()
    {
        InitializeComponent();
        ModuleName = "Wake-on-LAN";
    }

    public void Prefill(string mac, string name, string broadcast)
    {
        MacBox.Text = mac ?? "";
        NameBox.Text = name ?? "";
        if (!string.IsNullOrEmpty(broadcast)) BroadcastBox.Text = broadcast;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (NetUtil.ParseMac(MacBox.Text) == null) { ShowError("Enter a valid MAC address, e.g. AA:BB:CC:DD:EE:FF"); return; }
        if (!IPAddress.TryParse(BroadcastBox.Text.Trim(), out var bcast)) { ShowError("Invalid broadcast address."); return; }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) { ShowError("Invalid port."); return; }
        try
        {
            await WakeOnLan.SendAsync(MacBox.Text, bcast, port);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  sent to {MacBox.Text.Trim()} {NameBox.Text.Trim()} via {bcast}:{port}";
            HistoryList.Items.Insert(0, line);
            _sent.Add(new List<string> { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), MacBox.Text.Trim(), NameBox.Text.Trim(), $"{bcast}:{port}" });
            Summary = $"Woke {MacBox.Text.Trim()}";
        }
        catch (Exception ex) { ShowError("Couldn't send: " + ex.Message); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_sent.Count == 0) { ShowError("Nothing sent yet."); return; }
        try
        {
            var path = Exporter.ExportWithDialog("wake-on-lan", "Wake-on-LAN log", "", new[] { "Time", "MAC", "Name", "Sent via" }, _sent);
            if (path != null) AppInfo.OpenUrl(path);
        }
        catch (Exception ex) { ShowError("Export failed: " + ex.Message); }
    }
}
