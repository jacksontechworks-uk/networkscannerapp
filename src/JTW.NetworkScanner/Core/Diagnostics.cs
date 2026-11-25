using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace JTW.NetworkScanner.Core;

/// <summary>One hop on a route. Also carries live/path-ping statistics.</summary>
public class HopInfo : ObservableObject
{
    public int Ttl { get; set; }

    private string _address = "*";
    public string Address { get => _address; set => Set(ref _address, value); }

    private string _hostname = "";
    public string Hostname { get => _hostname; set => Set(ref _hostname, value ?? ""); }

    private long? _rtt;
    public long? RttMs { get => _rtt; set { Set(ref _rtt, value); OnPropertyChanged(nameof(RttText)); } }
    public string RttText => _rtt.HasValue ? $"{_rtt} ms" : "*";

    private int _sent, _received;
    public int Sent { get => _sent; set { Set(ref _sent, value); OnPropertyChanged(nameof(LossText)); OnPropertyChanged(nameof(LossPercent)); } }
    public int Received { get => _received; set { Set(ref _received, value); OnPropertyChanged(nameof(LossText)); OnPropertyChanged(nameof(LossPercent)); } }
    public double LossPercent => _sent == 0 ? 0 : 100.0 * (_sent - _received) / _sent;
    public string LossText => _sent == 0 ? "" : $"{LossPercent:0}%";

    private long _min = long.MaxValue, _max, _sum;
    public long? MinMs => _received == 0 ? null : _min;
    public long? MaxMs => _received == 0 ? null : _max;
    public double? AvgMs => _received == 0 ? null : (double)_sum / _received;
    public string AvgText => AvgMs.HasValue ? $"{AvgMs:0.0} ms" : "";
    public string MinMaxText => _received == 0 ? "" : $"{_min} / {_max} ms";

    public void AddSample(long? rtt)
    {
        Sent++;
        if (rtt.HasValue)
        {
            _min = Math.Min(_min, rtt.Value);
            _max = Math.Max(_max, rtt.Value);
            _sum += rtt.Value;
            Received++;
            RttMs = rtt;
        }
        else RttMs = null;
        OnPropertyChanged(nameof(AvgText));
        OnPropertyChanged(nameof(MinMaxText));
        OnPropertyChanged(nameof(AvgMs));
    }

    public bool IsPrivate => IPAddress.TryParse(_address, out var ip) && NetUtil.IsPrivate(ip);

    // Geo (filled for public hops)
    public double? Lat { get; set; }
    public double? Lon { get; set; }

    private string _location = "";
    public string Location { get => _location; set => Set(ref _location, value ?? ""); }

    private string _org = "";
    public string Org { get => _org; set => Set(ref _org, value ?? ""); }
}

public static class Diagnostics
{
    public static async Task<long?> PingOnceAsync(IPAddress ip, int timeoutMs = 1000, int ttl = 128)
    {
        try
        {
            using var p = new Ping();
            var reply = await p.SendPingAsync(ip, timeoutMs, new byte[32], new PingOptions(ttl, true)).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch { return null; }
    }

    /// <summary>ICMP traceroute. Reports each hop as it's discovered.</summary>
    public static async Task<List<HopInfo>> TracerouteAsync(IPAddress target, int maxHops, int timeoutMs,
        Action<HopInfo> onHop, CancellationToken ct, bool resolveNames = true)
    {
        var hops = new List<HopInfo>();
        var buffer = Encoding.ASCII.GetBytes("JTW-TRACE-JTW-TRACE-JTW-TRACE-32");
        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            var hop = new HopInfo { Ttl = ttl };
            IPAddress replyFrom = null;
            long? best = null;
            // Up to 3 probes per hop (stop early on a reply)
            for (int attempt = 0; attempt < 3 && replyFrom == null; attempt++)
            {
                try
                {
                    using var ping = new Ping();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(target, timeoutMs, buffer, new PingOptions(ttl, true)).ConfigureAwait(false);
                    sw.Stop();
                    if (reply.Status is IPStatus.TtlExpired or IPStatus.Success or IPStatus.TimeExceeded)
                    {
                        replyFrom = reply.Address;
                        best = reply.Status == IPStatus.Success ? reply.RoundtripTime : sw.ElapsedMilliseconds;
                    }
                }
                catch { /* timeout */ }
            }

            if (replyFrom != null)
            {
                hop.Address = replyFrom.ToString();
                hop.AddSample(best);
            }
            else
            {
                hop.AddSample(null);
            }
            hops.Add(hop);
            onHop?.Invoke(hop);

            if (replyFrom != null && resolveNames)
            {
                var h = hop;
                var addr = replyFrom;
                _ = Task.Run(async () => h.Hostname = await NetUtil.ReverseDnsAsync(addr).ConfigureAwait(false));
            }

            if (replyFrom != null && replyFrom.Equals(target)) break;
        }
        return hops;
    }

    /// <summary>
    /// PathPing-style statistics: send <paramref name="probes"/> pings to every responding hop (in parallel per hop).
    /// </summary>
    public static async Task PathPingAsync(IList<HopInfo> hops, int probes, int intervalMs, int timeoutMs, CancellationToken ct)
    {
        var tasks = hops.Where(h => IPAddress.TryParse(h.Address, out _)).Select(async hop =>
        {
            var ip = IPAddress.Parse(hop.Address);
            for (int i = 0; i < probes && !ct.IsCancellationRequested; i++)
            {
                var r = await PingOnceAsync(ip, timeoutMs).ConfigureAwait(false);
                hop.AddSample(r);
                try { await Task.Delay(intervalMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

public static class WakeOnLan
{
    public static async Task SendAsync(string mac, IPAddress broadcast = null, int port = 9)
    {
        var macBytes = NetUtil.ParseMac(mac) ?? throw new ArgumentException("Invalid MAC address: " + mac);
        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 1; i <= 16; i++) Buffer.BlockCopy(macBytes, 0, packet, i * 6, 6);

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var targets = new List<IPAddress> { broadcast ?? IPAddress.Broadcast };
        if (!targets.Contains(IPAddress.Broadcast)) targets.Add(IPAddress.Broadcast);
        foreach (var t in targets)
        {
            // Send a few times: UDP is fire-and-forget.
            for (int i = 0; i < 3; i++)
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(t, port)).ConfigureAwait(false);
        }
        Log.Info($"WoL magic packet sent to {mac} via {string.Join(",", targets)}:{port}");
    }
}

public class GeoResult
{
    public string Ip { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string City { get; set; }
    public string Country { get; set; }
    public string Org { get; set; }
    public string Label => string.Join(", ", new[] { City, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>IP geolocation via ipwho.is (free, HTTPS, no key). Results cached for the session.</summary>
public static class GeoIp
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, GeoResult> Cache = new();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"JTW-NetworkScanner/{AppInfo.Version.ToString(3)}");
        return c;
    }

    /// <summary>Pass null to locate this machine's public IP.</summary>
    public static async Task<GeoResult> LookupAsync(string ip)
    {
        var key = ip ?? "self";
        lock (Cache) if (Cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var json = await Http.GetStringAsync("https://ipwho.is/" + (ip ?? "")).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            var g = new GeoResult
            {
                Ip = r.GetProperty("ip").GetString(),
                Lat = r.GetProperty("latitude").GetDouble(),
                Lon = r.GetProperty("longitude").GetDouble(),
                City = r.TryGetProperty("city", out var c) ? c.GetString() : "",
                Country = r.TryGetProperty("country", out var co) ? co.GetString() : "",
                Org = r.TryGetProperty("connection", out var con) && con.TryGetProperty("org", out var org) ? org.GetString() : ""
            };
            lock (Cache) Cache[key] = g;
            return g;
        }
        catch (Exception ex)
        {
            Log.Error("GeoIP lookup failed for " + key, ex);
            return null;
        }
    }
}
