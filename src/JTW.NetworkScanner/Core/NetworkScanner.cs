using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace JTW.NetworkScanner.Core;

public class ScanOptions
{
    public int PingTimeoutMs { get; set; } = 800;
    public int PortTimeoutMs { get; set; } = 400;
    public bool ScanPorts { get; set; } = true;
    public bool ResolveNames { get; set; } = true;
    public List<int> Ports { get; set; } = new();
    public int MaxParallelHosts { get; set; } = 96;
    public AdapterInfo Adapter { get; set; }
}

public static class PortScanner
{
    public static async Task<bool> IsOpenAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch { return false; }
    }

    public static async Task<List<int>> ScanAsync(IPAddress ip, IEnumerable<int> ports, int timeoutMs, CancellationToken ct, int parallel = 32)
    {
        var open = new List<int>();
        using var gate = new SemaphoreSlim(parallel);
        var tasks = ports.Select(async p =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await IsOpenAsync(ip, p, timeoutMs, ct).ConfigureAwait(false))
                    lock (open) open.Add(p);
            }
            finally { gate.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (OperationCanceledException) { }
        open.Sort();
        return open;
    }
}

/// <summary>
/// Sweeps an address list: ICMP ping + ARP (local segment) + optional TCP port scan and reverse DNS.
/// Results are written straight into the supplied HostResult objects (UI binds to them).
/// </summary>
public class NetworkScanner
{
    public async Task ScanAsync(IReadOnlyList<HostResult> hosts, ScanOptions opt, IProgress<int> progress, CancellationToken ct)
    {
        OuiDatabase.EnsureLoaded();
        int done = 0;
        using var gate = new SemaphoreSlim(opt.MaxParallelHosts);
        var localIps = new HashSet<string>(NetUtil.GetAdapters().Select(a => a.Address.ToString()));
        var localMacs = NetUtil.GetAdapters().GroupBy(a => a.Address.ToString()).ToDictionary(g => g.Key, g => g.First().Mac, StringComparer.Ordinal);

        var tasks = hosts.Select(async h =>
        {
            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (ct.IsCancellationRequested) return;
                await ScanHostAsync(h, opt, localIps, localMacs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("Scan host " + h.Ip, ex); }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done));
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task ScanHostAsync(HostResult h, ScanOptions opt, HashSet<string> localIps,
        Dictionary<string, string> localMacs, CancellationToken ct)
    {
        h.State = HostState.Scanning;
        bool isLocal = localIps.Contains(h.Ip);
        h.IsLocalMachine = isLocal;
        h.IsGateway = opt.Adapter?.Gateway != null && opt.Adapter.Gateway.Equals(h.Address);

        // 1) ICMP
        long? rtt = null;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(h.Address, opt.PingTimeoutMs).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success) rtt = reply.RoundtripTime;
        }
        catch { /* ignore */ }

        ct.ThrowIfCancellationRequested();

        // 2) ARP — finds devices that drop ICMP (only meaningful on the local segment)
        string mac = null;
        if (isLocal)
            mac = localMacs.TryGetValue(h.Ip, out var m) ? m : null;
        else if (opt.Adapter != null && NetUtil.IsInSubnet(h.Address, opt.Adapter))
            mac = await Task.Run(() => NetUtil.ArpLookup(h.Address), ct).ConfigureAwait(false);

        bool alive = rtt.HasValue || mac != null || isLocal;
        var detected = new List<string>();
        if (rtt.HasValue) detected.Add("ICMP");
        if (mac != null && !isLocal) detected.Add("ARP");

        // 3) Off-subnet hosts with ICMP blocked: try a couple of common TCP ports as a liveness probe
        if (!alive && (opt.Adapter == null || !NetUtil.IsInSubnet(h.Address, opt.Adapter)))
        {
            foreach (var p in new[] { 443, 80, 22, 445, 3389 })
            {
                if (await PortScanner.IsOpenAsync(h.Address, p, opt.PortTimeoutMs, ct).ConfigureAwait(false))
                {
                    alive = true; detected.Add("TCP"); break;
                }
            }
        }

        if (!alive)
        {
            h.State = HostState.Offline;
            return;
        }

        h.LatencyMs = rtt;
        h.DetectedBy = isLocal ? "Local" : string.Join("+", detected);
        if (mac != null)
        {
            h.Mac = mac;
            h.Vendor = OuiDatabase.Lookup(mac);
        }
        h.State = HostState.Alive;

        // 4) Name + ports in parallel
        var nameTask = opt.ResolveNames ? NetUtil.ReverseDnsAsync(h.Address) : Task.FromResult<string>(null);
        var portTask = opt.ScanPorts && opt.Ports.Count > 0
            ? PortScanner.ScanAsync(h.Address, opt.Ports, opt.PortTimeoutMs, ct)
            : Task.FromResult(new List<int>());

        await Task.WhenAll(nameTask, portTask).ConfigureAwait(false);
        var name = await nameTask.ConfigureAwait(false);
        if (!string.IsNullOrEmpty(name)) h.Hostname = name;
        else if (isLocal) h.Hostname = Environment.MachineName;
        h.OpenPorts = await portTask.ConfigureAwait(false);
    }
}
