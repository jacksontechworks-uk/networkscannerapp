using System.Diagnostics;

namespace JTW.NetworkScanner.Core;

/// <summary>Opens the right local tool for a detected service (browser, SSH terminal, RDP, Explorer…).</summary>
public static class LaunchActions
{
    public static string Label(PortInfo p, string ip) => p.Action switch
    {
        PortAction.Http => $"Open http://{ip}{(p.Port == 80 ? "" : ":" + p.Port)} in browser",
        PortAction.Https => $"Open https://{ip}{(p.Port == 443 ? "" : ":" + p.Port)} in browser",
        PortAction.Ssh => $"SSH to {ip}{(p.Port == 22 ? "" : " -p " + p.Port)} (terminal)",
        PortAction.Telnet => $"Telnet to {ip}:{p.Port}",
        PortAction.Rdp => $"Remote Desktop to {ip}{(p.Port == 3389 ? "" : ":" + p.Port)}",
        PortAction.Smb => $"Browse shares \\\\{ip}",
        PortAction.Ftp => $"Open ftp://{ip}:{p.Port}",
        PortAction.Vnc => $"Open VNC viewer to {ip}:{p.Port}",
        _ => $"Port {p.Port} ({p.Name})"
    };

    public static void Launch(PortInfo p, string ip, string sshUser = null)
    {
        switch (p.Action)
        {
            case PortAction.Http:
                AppInfo.OpenUrl($"http://{ip}{(p.Port == 80 ? "" : ":" + p.Port)}/");
                break;
            case PortAction.Https:
                AppInfo.OpenUrl($"https://{ip}{(p.Port == 443 ? "" : ":" + p.Port)}/");
                break;
            case PortAction.Ssh:
                var target = string.IsNullOrWhiteSpace(sshUser) ? ip : $"{sshUser}@{ip}";
                var args = p.Port == 22 ? $"ssh {target}" : $"ssh -p {p.Port} {target}";
                OpenTerminal(args, $"SSH {ip}");
                break;
            case PortAction.Telnet:
                var telnet = Path.Combine(Environment.SystemDirectory, "telnet.exe");
                if (!File.Exists(telnet))
                    throw new InvalidOperationException("The Windows Telnet client isn't installed. Enable it under 'Turn Windows features on or off' → Telnet Client.");
                OpenTerminal($"telnet {ip} {p.Port}", $"Telnet {ip}");
                break;
            case PortAction.Rdp:
                Start("mstsc.exe", $"/v:{ip}{(p.Port == 3389 ? "" : ":" + p.Port)}");
                break;
            case PortAction.Smb:
                Start("explorer.exe", $"\\\\{ip}");
                break;
            case PortAction.Ftp:
                AppInfo.OpenUrl($"ftp://{ip}:{p.Port}/");
                break;
            case PortAction.Vnc:
                AppInfo.OpenUrl($"vnc://{ip}:{p.Port}");
                break;
        }
    }

    /// <summary>Runs a command in Windows Terminal if available, otherwise a classic console window.</summary>
    public static void OpenTerminal(string command, string title)
    {
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wt))
            Start(wt, $"new-tab --title \"{title}\" cmd /k {command}");
        else
            Start("cmd.exe", $"/k title {title} & {command}");
    }

    public static void Start(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Error($"Launch {exe} {args}", ex);
            throw new InvalidOperationException($"Couldn't start {exe}: {ex.Message}");
        }
    }
}
