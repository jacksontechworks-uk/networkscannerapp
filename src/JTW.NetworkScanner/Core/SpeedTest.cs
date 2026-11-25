using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace JTW.NetworkScanner.Core;

/// <summary>
/// Built-in LAN throughput test (iperf-style, own protocol). Run the server on one PC and the client on another.
/// Protocol: client sends 4-byte magic "JTW1", 1-byte mode ('U' upload / 'D' download), 4-byte duration (s, big-endian).
///  - Upload: client streams data for N seconds then half-closes; server replies with 8-byte total bytes received.
///  - Download: server streams for N seconds then closes.
/// </summary>
public static class SpeedTest
{
    private static readonly byte[] Magic = "JTW1"u8.ToArray();
    private const int BufferSize = 128 * 1024;

    public record Sample(double Seconds, double Mbps);

    // ---------------- Server ----------------

    public static async Task RunServerAsync(int port, Action<string> log, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        log($"Listening on TCP {port}. Allow it through Windows Firewall if prompted.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, log, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            log("Server stopped.");
        }
    }

    private static async Task HandleClientAsync(TcpClient client, Action<string> log, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString();
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var header = new byte[9];
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                if (!header.AsSpan(0, 4).SequenceEqual(Magic)) { log($"{remote}: not a JTW client, closing."); return; }
                char mode = (char)header[4];
                int seconds = Math.Clamp(BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)), 1, 120);

                if (mode == 'U')
                {
                    log($"{remote}: upload test started ({seconds}s).");
                    var buf = new byte[BufferSize];
                    long total = 0;
                    var sw = Stopwatch.StartNew();
                    int n;
                    while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0) total += n;
                    sw.Stop();
                    var reply = new byte[8];
                    BinaryPrimitives.WriteInt64BigEndian(reply, total);
                    await stream.WriteAsync(reply, ct).ConfigureAwait(false);
                    log($"{remote}: received {total / 1048576.0:0.0} MB → {total * 8 / sw.Elapsed.TotalSeconds / 1e6:0.0} Mbps");
                }
                else if (mode == 'D')
                {
                    log($"{remote}: download test started ({seconds}s).");
                    var buf = new byte[BufferSize];
                    Random.Shared.NextBytes(buf);
                    long total = 0;
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested)
                    {
                        await stream.WriteAsync(buf, ct).ConfigureAwait(false);
                        total += buf.Length;
                    }
                    log($"{remote}: sent {total / 1048576.0:0.0} MB → {total * 8 / sw.Elapsed.TotalSeconds / 1e6:0.0} Mbps");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log($"{remote}: {ex.Message}"); }
        }
    }

    // ---------------- Client ----------------

    /// <summary>Returns the overall Mbps. Reports a sample every ~0.5s.</summary>
    public static async Task<double> RunClientAsync(string host, int port, bool upload, int seconds,
        IProgress<Sample> progress, CancellationToken ct)
    {
        var ip = await NetUtil.ResolveAsync(host).ConfigureAwait(false);
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(5000);
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
        }
        var stream = client.GetStream();
        var header = new byte[9];
        Magic.CopyTo(header, 0);
        header[4] = (byte)(upload ? 'U' : 'D');
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), seconds);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);

        var buf = new byte[BufferSize];
        long total = 0, lastTotal = 0;
        var sw = Stopwatch.StartNew();
        double lastT = 0;

        void Tick()
        {
            var t = sw.Elapsed.TotalSeconds;
            if (t - lastT >= 0.5)
            {
                var mbps = (total - lastTotal) * 8 / (t - lastT) / 1e6;
                progress?.Report(new Sample(t, mbps));
                lastT = t; lastTotal = total;
            }
        }

        if (upload)
        {
            Random.Shared.NextBytes(buf);
            while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested)
            {
                await stream.WriteAsync(buf, ct).ConfigureAwait(false);
                total += buf.Length;
                Tick();
            }
            client.Client.Shutdown(SocketShutdown.Send);
            var reply = new byte[8];
            await stream.ReadExactlyAsync(reply, ct).ConfigureAwait(false);
            var serverTotal = BinaryPrimitives.ReadInt64BigEndian(reply);
            var elapsed = sw.Elapsed.TotalSeconds;
            return serverTotal * 8 / elapsed / 1e6;
        }
        else
        {
            int n;
            while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                Tick();
            }
            return total * 8 / sw.Elapsed.TotalSeconds / 1e6;
        }
    }
}
