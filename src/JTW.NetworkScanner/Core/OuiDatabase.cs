using System.IO.Compression;
using System.Reflection;

namespace JTW.NetworkScanner.Core;

/// <summary>
/// MAC vendor lookup. Uses the IEEE OUI list embedded in the exe; a newer list can be
/// dropped next to the app as JTW-Data\oui.txt ("AABBCC&lt;tab&gt;Vendor" per line) to override it.
/// </summary>
public static class OuiDatabase
{
    private static Dictionary<string, string> _map;
    private static readonly object Gate = new();

    public static int Count { get { EnsureLoaded(); return _map.Count; } }

    public static void EnsureLoaded()
    {
        if (_map != null) return;
        lock (Gate)
        {
            if (_map != null) return;
            var map = new Dictionary<string, string>(60000, StringComparer.OrdinalIgnoreCase);
            try
            {
                var overridePath = Path.Combine(AppInfo.DataDir, "oui.txt");
                if (File.Exists(overridePath))
                {
                    using var r = new StreamReader(overridePath);
                    Read(r, map);
                }
                else
                {
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("oui.txt.gz");
                    if (s != null)
                    {
                        using var gz = new GZipStream(s, CompressionMode.Decompress);
                        using var r = new StreamReader(gz);
                        Read(r, map);
                    }
                }
            }
            catch (Exception ex) { Log.Error("OUI load failed", ex); }
            _map = map;
        }
    }

    private static void Read(TextReader r, Dictionary<string, string> map)
    {
        string line;
        while ((line = r.ReadLine()) != null)
        {
            var tab = line.IndexOf('\t');
            if (tab != 6) continue;
            map[line.Substring(0, 6)] = line.Substring(7);
        }
    }

    public static string Lookup(string mac)
    {
        var bytes = NetUtil.ParseMac(mac);
        if (bytes == null) return "";
        EnsureLoaded();
        var key = $"{bytes[0]:X2}{bytes[1]:X2}{bytes[2]:X2}";
        if (_map.TryGetValue(key, out var vendor)) return Tidy(vendor);
        // Locally-administered bit set => randomised/private MAC (phones, Windows random HW addresses)
        if ((bytes[0] & 0x02) != 0) return "(Private / randomised MAC)";
        return "Unknown";
    }

    private static string Tidy(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "Unknown";
        // IEEE names are often SHOUTY; keep acronyms short, title-case long words.
        var words = v.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
            w.Length > 4 && w.All(c => !char.IsLetter(c) || char.IsUpper(c))
                ? char.ToUpper(w[0]) + w.Substring(1).ToLowerInvariant()
                : w);
        return string.Join(' ', words);
    }
}
