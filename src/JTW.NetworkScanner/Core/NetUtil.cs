using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace JTW.NetworkScanner.Core;

public class AdapterInfo
{
    public string Name { get; set; }
    public string Description { get; set; }
    public IPAddress Address { get; set; }
    public IPAddress Mask { get; set; }
    public IPAddress Gateway { get; set; }
    public string Mac { get; set; }
    public int PrefixLength { get; set; }
    public string Cidr => $"{NetUtil.NetworkAddress(Address, Mask)}/{PrefixLength}";
    public string Display => $"{Name}  —  {Address}/{PrefixLength}" + (Gateway != null ? $"  (gw {Gateway})" : "");
    public override string ToString() => Display;
}

public static class NetUtil
{
    public const int MaxHosts = 65536;

    public static List<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            var gw = props.GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA
                var prefix = ua.PrefixLength > 0 ? ua.PrefixLength : 24;
                list.Add(new AdapterInfo
                {
                    Name = ni.Name,
                    Description = ni.Description,
                    Address = ua.Address,
                    PrefixLength = prefix,
                    Mask = PrefixToMask(prefix),
                    Gateway = gw,
                    Mac = FormatMac(ni.GetPhysicalAddress().GetAddressBytes())
                });
            }
        }
        // Adapters with a default gateway first: that's almost always "the" LAN.
        return list.OrderByDescending(a => a.Gateway != null).ThenBy(a => a.Name).ToList();
    }

    public static IPAddress PrefixToMask(int prefix)
    {
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return ToIp(mask);
    }

    public static IPAddress NetworkAddress(IPAddress ip, IPAddress mask) => ToIp(ToUInt(ip) & ToUInt(mask));

    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress ToIp(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    /// <summary>
    /// Parses "192.168.1.0/24", "192.168.1.10-192.168.1.50", "192.168.1.10-50" or a single IP.
    /// Network and broadcast addresses are excluded for CIDR ranges smaller than /31.
    /// </summary>
    public static bool TryParseRange(string text, out List<IPAddress> addresses, out string error)
    {
        addresses = new List<IPAddress>();
        error = null;
        text = (text ?? "").Trim();
        if (text.Length == 0) { error = "Enter a range, e.g. 192.168.1.0/24"; return false; }

        uint start, end;
        try
        {
            if (text.Contains('/'))
            {
                var parts = text.Split('/');
                var ip = IPAddress.Parse(parts[0].Trim());
                var prefix = int.Parse(parts[1].Trim());
                if (prefix < 16 || prefix > 32) { error = "Prefix must be between /16 and /32."; return false; }
                var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
                var net = ToUInt(ip) & mask;
                var bcast = net | ~mask;
                if (prefix >= 31) { start = net; end = bcast; }
                else { start = net + 1; end = bcast - 1; }
            }
            else if (text.Contains('-'))
            {
                var parts = text.Split('-');
                var a = IPAddress.Parse(parts[0].Trim());
                var right = parts[1].Trim();
                IPAddress b;
                if (!right.Contains('.'))
                {
                    var ab = a.GetAddressBytes();
                    b = new IPAddress(new[] { ab[0], ab[1], ab[2], byte.Parse(right) });
                }
                else b = IPAddress.Parse(right);
                start = ToUInt(a); end = ToUInt(b);
                if (end < start) (start, end) = (end, start);
            }
            else
            {
                start = end = ToUInt(IPAddress.Parse(text));
            }
        }
        catch
        {
            error = "Couldn't understand that range. Try 192.168.1.0/24 or 192.168.1.10-192.168.1.50";
            return false;
        }

        if ((ulong)end - start + 1 > MaxHosts) { error = $"Range too large (max {MaxHosts} addresses)."; return false; }
        for (ulong v = start; v <= end; v++) addresses.Add(ToIp((uint)v));
        return true;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // CGNAT
            || (b[0] == 169 && b[1] == 254)
            || b[0] == 127;
    }

    public static bool IsInSubnet(IPAddress ip, AdapterInfo adapter)
    {
        if (adapter == null) return false;
        var m = ToUInt(adapter.Mask);
        return (ToUInt(ip) & m) == (ToUInt(adapter.Address) & m);
    }

    public static string FormatMac(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 6 || bytes.All(b => b == 0)) return "";
        return string.Join(":", bytes.Take(6).Select(b => b.ToString("X2")));
    }

    public static byte[] ParseMac(string mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        return Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
    }

    // ---- ARP (works even when hosts block ICMP) ----

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physicalAddrLen);

    /// <summary>Returns the MAC for an IP on the local segment, or null. Blocking (up to ~1s for absent hosts).</summary>
    public static string ArpLookup(IPAddress ip)
    {
        try
        {
            var mac = new byte[6];
            int len = mac.Length;
            uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            int r = SendARP(dest, 0, mac, ref len);
            if (r != 0 || len < 6) return null;
            var s = FormatMac(mac);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch { return null; }
    }

    public static async Task<string> ReverseDnsAsync(IPAddress ip, int timeoutMs = 1500)
    {
        try
        {
            var task = Dns.GetHostEntryAsync(ip);
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (done != task) return null;
            var name = (await task.ConfigureAwait(false)).HostName;
            return name == ip.ToString() ? null : name;
        }
        catch { return null; }
    }

    public static async Task<IPAddress> ResolveAsync(string hostOrIp)
    {
        hostOrIp = (hostOrIp ?? "").Trim();
        if (IPAddress.TryParse(hostOrIp, out var ip)) return ip;
        var entries = await Dns.GetHostAddressesAsync(hostOrIp).ConfigureAwait(false);
        return entries.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? entries.FirstOrDefault();
    }

    public static IPAddress GetBroadcast(AdapterInfo adapter)
    {
        if (adapter == null) return IPAddress.Broadcast;
        var m = ToUInt(adapter.Mask);
        return ToIp((ToUInt(adapter.Address) & m) | ~m);
    }
}
