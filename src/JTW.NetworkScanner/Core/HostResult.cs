using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace JTW.NetworkScanner.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum HostState { Pending, Scanning, Alive, Offline }

public class HostResult : ObservableObject
{
    public HostResult(IPAddress ip)
    {
        Address = ip;
        Ip = ip.ToString();
        IpSort = NetUtil.ToUInt(ip);
    }

    public IPAddress Address { get; }
    public string Ip { get; }
    public uint IpSort { get; }

    private HostState _state = HostState.Pending;
    public HostState State
    {
        get => _state;
        set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsAlive)); OnPropertyChanged(nameof(LatencyText)); } }
    }

    public bool IsAlive => _state == HostState.Alive;

    public string StatusText => _state switch
    {
        HostState.Alive => "Online",
        HostState.Offline => "Offline",
        HostState.Scanning => "Scanning…",
        _ => "—"
    };

    private string _hostname = "";
    public string Hostname { get => _hostname; set => Set(ref _hostname, value ?? ""); }

    private string _mac = "";
    public string Mac { get => _mac; set => Set(ref _mac, value ?? ""); }

    private string _vendor = "";
    public string Vendor { get => _vendor; set => Set(ref _vendor, value ?? ""); }

    private long? _latency;
    public long? LatencyMs
    {
        get => _latency;
        set { Set(ref _latency, value); OnPropertyChanged(nameof(LatencyText)); }
    }

    public string LatencyText => _latency.HasValue ? $"{_latency} ms" : (_state == HostState.Alive ? "no ICMP" : "");

    private string _detectedBy = "";
    /// <summary>How the host was found: ICMP, ARP, TCP.</summary>
    public string DetectedBy { get => _detectedBy; set => Set(ref _detectedBy, value ?? ""); }

    private List<int> _openPorts = new();
    public List<int> OpenPorts
    {
        get => _openPorts;
        set
        {
            _openPorts = value ?? new List<int>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(PortsText));
        }
    }

    public string PortsText => string.Join(", ", _openPorts.Select(PortCatalog.Describe));

    private bool _isLocal;
    public bool IsLocalMachine { get => _isLocal; set { if (Set(ref _isLocal, value)) OnPropertyChanged(nameof(Role)); } }

    private bool _isGateway;
    public bool IsGateway { get => _isGateway; set { if (Set(ref _isGateway, value)) OnPropertyChanged(nameof(Role)); } }

    public string Role => IsLocalMachine ? "This PC" : IsGateway ? "Gateway" : "";
}
