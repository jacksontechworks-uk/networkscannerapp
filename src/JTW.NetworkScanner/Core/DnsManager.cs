using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace JTW.NetworkScanner.Core;

public class DnsPreset
{
    public string Name { get; init; }
    public string Description { get; init; }
    public string[] IPv4 { get; init; } = Array.Empty<string>();
    public string[] IPv6 { get; init; } = Array.Empty<string>();
    public bool IsAutomatic { get; init; }
    public bool IsCustom { get; init; }
    public string ServersText => IsAutomatic ? "From your router / DHCP" : IsCustom ? "Enter your own servers" : string.Join(", ", IPv4);
}

public class DnsAdapter
{
    public string Name { get; set; }
    public string Description { get; set; }
    public string Id { get; set; }
    public int Index { get; set; }
    public bool IsAutomatic { get; set; }
    public List<string> Servers { get; set; } = new();
    public string ServersText => Servers.Count == 0 ? "(none)" : string.Join(", ", Servers);
    public string ModeText => IsAutomatic ? "Automatic (DHCP)" : "Manual";
    public string MatchedPreset { get; set; }
    public string Display => $"{Name}  —  {Description}";
    public override string ToString() => Display;
}

/// <summary>Reads and changes Windows DNS server settings per adapter. Changes need admin rights (UAC prompt).</summary>
public static class DnsManager
{
    public static readonly List<DnsPreset> Presets = new()
    {
        new() { Name = "Automatic (DHCP)", Description = "Use whatever DNS your router or network hands out. This is the Windows default.", IsAutomatic = true },
        new() { Name = "Quad9", Description = "Privacy-focused, non-profit (Switzerland). Blocks known malicious domains.",
                IPv4 = new[] { "9.9.9.9", "149.112.112.112" }, IPv6 = new[] { "2620:fe::fe", "2620:fe::9" } },
        new() { Name = "Quad9 (unfiltered)", Description = "Quad9 without the malware blocking.",
                IPv4 = new[] { "9.9.9.10", "149.112.112.10" }, IPv6 = new[] { "2620:fe::10", "2620:fe::fe:10" } },
        new() { Name = "Cloudflare", Description = "Fast, privacy-first resolver. No filtering.",
                IPv4 = new[] { "1.1.1.1", "1.0.0.1" }, IPv6 = new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" } },
        new() { Name = "Cloudflare (malware blocking)", Description = "Cloudflare for Families: blocks malware.",
                IPv4 = new[] { "1.1.1.2", "1.0.0.2" }, IPv6 = new[] { "2606:4700:4700::1112", "2606:4700:4700::1002" } },
        new() { Name = "Cloudflare (malware + adult)", Description = "Cloudflare for Families: blocks malware and adult content.",
                IPv4 = new[] { "1.1.1.3", "1.0.0.3" }, IPv6 = new[] { "2606:4700:4700::1113", "2606:4700:4700::1003" } },
        new() { Name = "Google Public DNS", Description = "Google's global resolver. No filtering.",
                IPv4 = new[] { "8.8.8.8", "8.8.4.4" }, IPv6 = new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" } },
        new() { Name = "OpenDNS (Cisco)", Description = "Cisco Umbrella home resolver, with phishing protection.",
                IPv4 = new[] { "208.67.222.222", "208.67.220.220" }, IPv6 = new[] { "2620:119:35::35", "2620:119:53::53" } },
        new() { Name = "AdGuard DNS", Description = "Blocks ads and trackers at DNS level.",
                IPv4 = new[] { "94.140.14.14", "94.140.15.15" }, IPv6 = new[] { "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff" } },
        new() { Name = "Custom", Description = "Enter your own primary and secondary DNS servers (e.g. a Pi-hole or domain controller).", IsCustom = true },
    };

    public static List<DnsAdapter> GetAdapters()
    {
        var list = new List<DnsAdapter>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            int index;
            try
            {
                props = ni.GetIPProperties();
                index = props.GetIPv4Properties()?.Index ?? -1;
            }
            catch { continue; }
            if (index < 0) continue;

            var a = new DnsAdapter
            {
                Name = ni.Name,
                Description = ni.Description,
                Id = ni.Id,
                Index = index,
                Servers = props.DnsAddresses.Where(d => !d.IsIPv6SiteLocal).Select(d => d.ToString()).ToList(),
                IsAutomatic = IsAutomatic(ni.Id)
            };
            a.MatchedPreset = a.IsAutomatic
                ? Presets[0].Name
                : Presets.FirstOrDefault(p => !p.IsAutomatic && !p.IsCustom && p.IPv4.Length > 0 && a.Servers.Contains(p.IPv4[0]))?.Name ?? "Custom";
            list.Add(a);
        }
        // Adapters with a default gateway first
        return list.OrderByDescending(a =>
            NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == a.Id)?.GetIPProperties().GatewayAddresses.Count > 0).ToList();
    }

    /// <summary>Static DNS is stored in the NameServer registry value; empty means DHCP-supplied.</summary>
    private static bool IsAutomatic(string interfaceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{interfaceId}");
            var ns = key?.GetValue("NameServer") as string;
            return string.IsNullOrWhiteSpace(ns);
        }
        catch { return true; }
    }

    /// <summary>
    /// Applies DNS servers (or resets to DHCP when <paramref name="ipv4"/> is null) using an elevated PowerShell.
    /// Throws OperationCanceledException if the user declines the UAC prompt.
    /// </summary>
    public static async Task ApplyAsync(DnsAdapter adapter, string[] ipv4, string[] ipv6, bool includeIpv6)
    {
        var sb = new StringBuilder();
        var log = Path.Combine(Path.GetTempPath(), $"jtw-dns-{Guid.NewGuid():N}.log");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("try {");
        if (ipv4 == null)
        {
            sb.AppendLine($"  Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} -ResetServerAddresses");
        }
        else
        {
            foreach (var s in ipv4.Concat(ipv6 ?? Array.Empty<string>()))
                if (!IPAddress.TryParse(s, out _)) throw new ArgumentException($"'{s}' isn't a valid IP address.");
            sb.AppendLine($"  Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} -ServerAddresses @({Quote(ipv4)})");
            if (includeIpv6 && ipv6 is { Length: > 0 })
            {
                // IPv6 can be disabled on the adapter; don't fail the whole change for that.
                sb.AppendLine($"  try {{ Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} -ServerAddresses @({Quote(ipv4.Concat(ipv6))}) }} catch {{ }}");
            }
        }
        sb.AppendLine("  Clear-DnsClientCache");
        sb.AppendLine("  exit 0");
        sb.AppendLine($"}} catch {{ $_ | Out-String | Out-File -Encoding utf8 '{log}'; exit 1 }}");

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process p;
        try { p = Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Administrator permission was declined, so DNS wasn't changed.");
        }
        if (p == null) throw new InvalidOperationException("Couldn't start PowerShell.");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var msg = File.Exists(log) ? File.ReadAllText(log).Trim() : $"PowerShell exited with code {p.ExitCode}.";
            try { File.Delete(log); } catch { }
            throw new InvalidOperationException(msg);
        }
        Log.Info($"DNS on '{adapter.Name}' set to {(ipv4 == null ? "automatic" : string.Join(",", ipv4))}");
    }

    private static string Quote(IEnumerable<string> items) => string.Join(",", items.Select(i => $"'{i}'"));

    public static void FlushCache()
    {
        var psi = new ProcessStartInfo("ipconfig.exe", "/flushdns") { CreateNoWindow = true, UseShellExecute = false };
        using var p = Process.Start(psi);
        p?.WaitForExit(10000);
    }

    /// <summary>Times a real DNS lookup (A record for example.com) against a server. Best of 3, null on timeout.</summary>
    public static async Task<double?> MeasureAsync(string server, string domain = "example.com", int timeoutMs = 2000)
    {
        if (!IPAddress.TryParse(server, out var ip)) return null;
        double? best = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using var udp = new UdpClient(ip.AddressFamily);
                var query = BuildQuery(domain, (ushort)Random.Shared.Next(1, 65535));
                var sw = Stopwatch.StartNew();
                await udp.SendAsync(query, query.Length, new IPEndPoint(ip, 53));
                var receive = udp.ReceiveAsync();
                if (await Task.WhenAny(receive, Task.Delay(timeoutMs)) != receive) continue;
                await receive;
                sw.Stop();
                var ms = sw.Elapsed.TotalMilliseconds;
                if (best == null || ms < best) best = ms;
            }
            catch { /* blocked / unreachable */ }
        }
        return best;
    }

    private static byte[] BuildQuery(string domain, ushort id)
    {
        var b = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.'))
        {
            b.Add((byte)label.Length);
            b.AddRange(Encoding.ASCII.GetBytes(label));
        }
        b.AddRange(new byte[] { 0, 0, 1, 0, 1 }); // root, QTYPE=A, QCLASS=IN
        return b.ToArray();
    }
}
