namespace JTW.NetworkScanner.Core;

public enum PortAction
{
    None,
    Http,
    Https,
    Ssh,
    Telnet,
    Rdp,
    Smb,
    Ftp,
    Vnc
}

public record PortInfo(int Port, string Name, PortAction Action);

public static class PortCatalog
{
    public const string DefaultPortList =
        "21,22,23,25,53,80,81,88,110,135,139,143,389,443,445,548,554,587,631,993,995,1433,1883,1900,2049,3000,3306,3389,5000,5001,5060,5432,5900,5985,5986,6443,7547,8000,8006,8008,8080,8081,8088,8123,8443,8888,9000,9090,9100,9443,10000,32400,49152";

    private static readonly Dictionary<int, PortInfo> Known = new()
    {
        [20] = new(20, "FTP-data", PortAction.None),
        [21] = new(21, "FTP", PortAction.Ftp),
        [22] = new(22, "SSH", PortAction.Ssh),
        [23] = new(23, "Telnet", PortAction.Telnet),
        [25] = new(25, "SMTP", PortAction.None),
        [53] = new(53, "DNS", PortAction.None),
        [80] = new(80, "HTTP", PortAction.Http),
        [81] = new(81, "HTTP-alt", PortAction.Http),
        [88] = new(88, "Kerberos", PortAction.None),
        [110] = new(110, "POP3", PortAction.None),
        [135] = new(135, "MS-RPC", PortAction.None),
        [139] = new(139, "NetBIOS", PortAction.Smb),
        [143] = new(143, "IMAP", PortAction.None),
        [389] = new(389, "LDAP", PortAction.None),
        [443] = new(443, "HTTPS", PortAction.Https),
        [445] = new(445, "SMB", PortAction.Smb),
        [548] = new(548, "AFP", PortAction.None),
        [554] = new(554, "RTSP", PortAction.None),
        [587] = new(587, "SMTP-sub", PortAction.None),
        [631] = new(631, "IPP", PortAction.Http),
        [993] = new(993, "IMAPS", PortAction.None),
        [995] = new(995, "POP3S", PortAction.None),
        [1433] = new(1433, "MSSQL", PortAction.None),
        [1883] = new(1883, "MQTT", PortAction.None),
        [1900] = new(1900, "UPnP", PortAction.None),
        [2049] = new(2049, "NFS", PortAction.None),
        [3000] = new(3000, "HTTP-dev", PortAction.Http),
        [3306] = new(3306, "MySQL", PortAction.None),
        [3389] = new(3389, "RDP", PortAction.Rdp),
        [5000] = new(5000, "HTTP-5000", PortAction.Http),
        [5001] = new(5001, "HTTPS-5001", PortAction.Https),
        [5060] = new(5060, "SIP", PortAction.None),
        [5432] = new(5432, "PostgreSQL", PortAction.None),
        [5900] = new(5900, "VNC", PortAction.Vnc),
        [5985] = new(5985, "WinRM", PortAction.None),
        [5986] = new(5986, "WinRM-S", PortAction.None),
        [6443] = new(6443, "K8s API", PortAction.Https),
        [7547] = new(7547, "TR-069", PortAction.None),
        [8000] = new(8000, "HTTP-8000", PortAction.Http),
        [8006] = new(8006, "Proxmox", PortAction.Https),
        [8008] = new(8008, "HTTP-8008", PortAction.Http),
        [8080] = new(8080, "HTTP-proxy", PortAction.Http),
        [8081] = new(8081, "HTTP-8081", PortAction.Http),
        [8088] = new(8088, "HTTP-8088", PortAction.Http),
        [8123] = new(8123, "Home Assistant", PortAction.Http),
        [8443] = new(8443, "HTTPS-alt", PortAction.Https),
        [8888] = new(8888, "HTTP-8888", PortAction.Http),
        [9000] = new(9000, "HTTP-9000", PortAction.Http),
        [9090] = new(9090, "HTTP-9090", PortAction.Http),
        [9100] = new(9100, "JetDirect", PortAction.None),
        [9443] = new(9443, "HTTPS-9443", PortAction.Https),
        [10000] = new(10000, "Webmin", PortAction.Https),
        [32400] = new(32400, "Plex", PortAction.Http),
        [49152] = new(49152, "UPnP-dyn", PortAction.None),
    };

    public static PortInfo Get(int port) =>
        Known.TryGetValue(port, out var p) ? p : new PortInfo(port, port.ToString(), PortAction.None);

    public static string Describe(int port)
    {
        var p = Get(port);
        return p.Name == port.ToString() ? port.ToString() : $"{port} ({p.Name})";
    }

    /// <summary>Parses "22,80,8000-8010" into a sorted distinct list of ports.</summary>
    public static List<int> ParsePorts(string text)
    {
        var set = new SortedSet<int>();
        foreach (var raw in (text ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Contains('-'))
            {
                var ab = part.Split('-');
                if (int.TryParse(ab[0], out var a) && int.TryParse(ab[1], out var b))
                {
                    if (b < a) (a, b) = (b, a);
                    for (int i = Math.Max(1, a); i <= Math.Min(65535, b) && set.Count < 5000; i++) set.Add(i);
                }
            }
            else if (int.TryParse(part, out var p) && p is > 0 and <= 65535) set.Add(p);
        }
        return set.ToList();
    }
}
