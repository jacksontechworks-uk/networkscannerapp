using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace JTW.NetworkScanner.Core;

public static class AppInfo
{
    public const string ProductName = "JacksonTechWorks Network Scanner";
    public const string Brand = "JacksonTechWorks";
    public const string RepoOwner = "jacksontechworks-uk";
    public const string RepoName = "networkscannerapp";
    public const string ReleaseAssetName = "JTW-NetworkScanner-win-x64.zip";
    public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";

    public static Version Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string VersionText => "v" + Version.ToString(3);

    /// <summary>Full path of the running exe (works for single-file publish).</summary>
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "JTW-NetworkScanner.exe");

    /// <summary>Folder the portable app lives in.</summary>
    public static string AppDir => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    private static string _dataDir;

    /// <summary>Portable data folder next to the exe; falls back to %LOCALAPPDATA% if the folder is read-only.</summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;
            var portable = Path.Combine(AppDir, "JTW-Data");
            try
            {
                Directory.CreateDirectory(portable);
                var probe = Path.Combine(portable, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                _dataDir = portable;
            }
            catch
            {
                _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JacksonTechWorks", "NetworkScanner");
                Directory.CreateDirectory(_dataDir);
            }
            return _dataDir;
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("OpenUrl " + url, ex); }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    private static string LogPath => Path.Combine(AppInfo.DataDir, "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception ex = null) => Write("ERROR", message + (ex != null ? " :: " + ex : ""));

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}

public class AppSettings
{
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public string SshUser { get; set; } = "";
    public int PingTimeoutMs { get; set; } = 800;
    public int PortTimeoutMs { get; set; } = 400;
    public bool ScanPorts { get; set; } = true;
    public bool ResolveNames { get; set; } = true;
    public string Ports { get; set; } = PortCatalog.DefaultPortList;
    public int SpeedTestPort { get; set; } = 5201;
    public string LastRange { get; set; } = "";
    public string SkippedVersion { get; set; } = "";

    public static AppSettings Current { get; private set; } = new();

    private static string FilePath => Path.Combine(AppInfo.DataDir, "settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Settings load failed", ex);
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Error("Settings save failed", ex); }
    }
}
