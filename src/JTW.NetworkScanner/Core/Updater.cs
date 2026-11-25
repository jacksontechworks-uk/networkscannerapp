using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace JTW.NetworkScanner.Core;

public class UpdateInfo
{
    public Version Version { get; set; }
    public string Tag { get; set; }
    public string Notes { get; set; }
    public string HtmlUrl { get; set; }
    public string ZipUrl { get; set; }
    public string ShaUrl { get; set; }
    public long Size { get; set; }
}

/// <summary>
/// Self-updater backed by GitHub Releases. The release zip is extracted over the app folder:
/// files that are in use (the running exe) are renamed to *.old first, which Windows allows,
/// and cleaned up on the next start.
/// </summary>
public static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"JTW-NetworkScanner/{AppInfo.Version.ToString(3)}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version ParseTag(string tag)
    {
        var t = (tag ?? "").Trim().TrimStart('v', 'V');
        var dash = t.IndexOf('-');
        if (dash > 0) t = t[..dash];
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Returns update info if a newer release exists, otherwise null.</summary>
    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        var version = ParseTag(tag);
        if (version == null || version <= AppInfo.Version) return null;

        var info = new UpdateInfo
        {
            Version = version,
            Tag = tag,
            Notes = root.TryGetProperty("body", out var b) ? b.GetString() : "",
            HtmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : AppInfo.RepoUrl + "/releases"
        };
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var dl = asset.GetProperty("browser_download_url").GetString();
            if (name.Equals(AppInfo.ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
            {
                info.ZipUrl = dl;
                info.Size = asset.GetProperty("size").GetInt64();
            }
            else if (name.Equals(AppInfo.ReleaseAssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                info.ShaUrl = dl;
        }
        return info.ZipUrl == null ? null : info;
    }

    /// <summary>Downloads, verifies and installs the update, then restarts the app.</summary>
    public static async Task ApplyAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "JTW-NetworkScanner-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, AppInfo.ReleaseAssetName);

        // Download with progress
        using (var resp = await Http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            long read = 0; int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        // Verify checksum when the release publishes one
        if (!string.IsNullOrEmpty(info.ShaUrl))
        {
            var expected = (await Http.GetStringAsync(info.ShaUrl, ct).ConfigureAwait(false)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            await using var fs = File.OpenRead(zipPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("Downloaded update failed its checksum check. Nothing was changed.");
        }

        var extractDir = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zipPath, extractDir, true);

        var appDir = AppInfo.AppDir;
        EnsureWritable(appDir);

        foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(extractDir, file);
            var target = Path.Combine(appDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                var old = target + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(target, old); // allowed even for the running exe
            }
            File.Copy(file, target, true);
        }

        Log.Info($"Updated to {info.Tag}");
        try { Directory.Delete(work, true); } catch { }
    }

    public static void Restart()
    {
        Process.Start(new ProcessStartInfo(AppInfo.ExePath) { UseShellExecute = true, WorkingDirectory = AppInfo.AppDir });
        System.Windows.Application.Current.Shutdown();
    }

    private static void EnsureWritable(string dir)
    {
        var probe = Path.Combine(dir, ".jtw-write-test");
        try { File.WriteAllText(probe, "ok"); File.Delete(probe); }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"The app folder ({dir}) isn't writable, so the update can't be installed. Move the app to a folder you own (e.g. Documents or Desktop) or download the release manually.", ex);
        }
    }

    public static void CleanupOldFiles()
    {
        try
        {
            foreach (var f in Directory.GetFiles(AppInfo.AppDir, "*.old", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* still locked; try next start */ }
        }
        catch { }
    }
}
