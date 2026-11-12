using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace LanChat;

public sealed class UpdateCheck
{
    public bool Available { get; set; }
    public string? Version { get; set; }
    public string? Url { get; set; }
    public string? Notes { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Minimal update flow: a JSON manifest ({ "version": "1.2.0", "url": "...", "notes": "..." })
/// is compared with the running build, the new executable is downloaded, and a helper script
/// swaps it in after this process exits.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static string CurrentVersion =>
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0)).ToString(3);

    public static async Task<UpdateCheck> CheckAsync(string feedUrl, CancellationToken ct)
    {
        var result = new UpdateCheck();
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            result.Error = "No update feed is configured.";
            return result;
        }

        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            result.Error = "The update feed is not a valid http(s) URL.";
            return result;
        }

        try
        {
            using var response = await Http.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                result.Error = "The update feed returned " + (int)response.StatusCode + ".";
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() : null;

            result.Version = version;
            result.Url = url;
            result.Notes = notes;

            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url))
            {
                result.Error = "The update feed is missing a version or download URL.";
                return result;
            }

            if (!Version.TryParse(version, out var latest))
            {
                result.Error = "The update feed has an unreadable version string.";
                return result;
            }

            result.Available = latest > (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    public static async Task<(string? Path, string? Error)> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (null, "Bad download URL.");
            var target = Path.Combine(Path.GetTempPath(), "lanchat-update-" + Guid.NewGuid().ToString("N") + ".exe");
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return (null, "Download failed with " + (int)response.StatusCode + ".");
            await using (var file = File.Create(target))
            {
                await response.Content.CopyToAsync(file, ct);
            }

            var info = new FileInfo(target);
            if (info.Length < 100_000)
            {
                try
                {
                    File.Delete(target);
                }
                catch
                {
                }

                return (null, "The downloaded file is too small to be LAN Chat.");
            }

            return (target, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public static bool ApplyAndRestart(string stagedPath)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var script = Path.Combine(Path.GetTempPath(), "lanchat-update-" + Guid.NewGuid().ToString("N") + ".cmd");
            var content = "@echo off\r\n" +
                          "ping 127.0.0.1 -n 4 >nul\r\n" +
                          "move /y \"" + stagedPath + "\" \"" + exe + "\" >nul\r\n" +
                          "start \"\" \"" + exe + "\"\r\n" +
                          "del \"%~f0\"\r\n";
            File.WriteAllText(script, content);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            Application.Exit();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
