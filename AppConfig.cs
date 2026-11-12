using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanChat;

/// <summary>A private group: the creator plus the peers they picked.</summary>
public sealed class GroupInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Every member, including this computer and the creator.</summary>
    public List<string> Members { get; set; } = new();

    public string CreatedBy { get; set; } = "";
    public long CreatedAt { get; set; }
}

public sealed class AppConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Environment.UserName;
    public int HttpPort { get; set; } = 47821;
    public int UdpPort { get; set; } = 47822;
    public string Password { get; set; } = "";
    public string Theme { get; set; } = "dark";

    /// <summary>Named room / join code. Peers in a different room are ignored.</summary>
    public string Room { get; set; } = "Default";

    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool Notifications { get; set; } = true;
    public bool Sounds { get; set; } = true;

    /// <summary>Days to keep received files; 0 keeps them forever.</summary>
    public int FileRetentionDays { get; set; } = 7;

    /// <summary>Serve the UI to other devices on the LAN (requires a room password).</summary>
    public bool AllowLanWeb { get; set; }

    public List<string> Blocked { get; set; } = new();
    public List<string> Muted { get; set; } = new();
    public List<GroupInfo> Groups { get; set; } = new();

    /// <summary>Optional JSON update manifest URL (see UpdateService).</summary>
    public string UpdateFeed { get; set; } = "";
}

public static class AppPaths
{
    private static readonly bool Portable = ComputePortable();
    private static readonly string? OverrideDir = Environment.GetEnvironmentVariable("LANCHAT_DATA_DIR");

    private static bool ComputePortable()
    {
        try
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPortable => Portable;

    public static string DataDir => !string.IsNullOrWhiteSpace(OverrideDir)
        ? OverrideDir!
        : Portable
            ? Path.Combine(AppContext.BaseDirectory, "LanChatData")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanChat");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string FilesDir => Path.Combine(DataDir, "files");
    public static string WebViewDir => Path.Combine(DataDir, "WebView2");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(WebViewDir);
    }
}

public sealed class ConfigService
{
    private readonly object _lock = new();
    public AppConfig Config { get; }

    public ConfigService()
    {
        Config = Load();
    }

    private static AppConfig Load()
    {
        try
        {
            AppPaths.Ensure();
            if (File.Exists(AppPaths.ConfigFile))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile));
                if (cfg != null)
                {
                    if (string.IsNullOrWhiteSpace(cfg.Id)) cfg.Id = Guid.NewGuid().ToString("N");
                    if (string.IsNullOrWhiteSpace(cfg.Name)) cfg.Name = Environment.UserName;
                    if (cfg.HttpPort <= 0) cfg.HttpPort = 47821;
                    if (cfg.UdpPort <= 0) cfg.UdpPort = 47822;
                    if (cfg.Theme != "light") cfg.Theme = "dark";
                    if (string.IsNullOrWhiteSpace(cfg.Room)) cfg.Room = "Default";
                    cfg.Room = cfg.Room.Trim();
                    if (cfg.FileRetentionDays < 0) cfg.FileRetentionDays = 0;
                    if (cfg.FileRetentionDays > 3650) cfg.FileRetentionDays = 3650;
                    cfg.Blocked ??= new List<string>();
                    cfg.Muted ??= new List<string>();
                    cfg.Groups ??= new List<GroupInfo>();
                    cfg.Groups.RemoveAll(g => g == null || string.IsNullOrEmpty(g.Id));
                    foreach (var group in cfg.Groups)
                    {
                        group.Members ??= new List<string>();
                        group.Members.RemoveAll(string.IsNullOrEmpty);
                        if (string.IsNullOrWhiteSpace(group.Name)) group.Name = "Group";
                    }
                    cfg.UpdateFeed ??= "";
                    cfg.Password = UnprotectPassword(cfg.Password);
                    return cfg;
                }
            }
        }
        catch
        {
        }

        var fresh = new AppConfig();
        Save(fresh);
        return fresh;
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            AppPaths.Ensure();
            var onDisk = new AppConfig
            {
                Id = cfg.Id,
                Name = cfg.Name,
                HttpPort = cfg.HttpPort,
                UdpPort = cfg.UdpPort,
                Theme = cfg.Theme,
                Password = ProtectPassword(cfg.Password),
                Room = cfg.Room,
                MinimizeToTray = cfg.MinimizeToTray,
                StartWithWindows = cfg.StartWithWindows,
                Notifications = cfg.Notifications,
                Sounds = cfg.Sounds,
                FileRetentionDays = cfg.FileRetentionDays,
                AllowLanWeb = cfg.AllowLanWeb,
                Blocked = new List<string>(cfg.Blocked ?? new List<string>()),
                Muted = new List<string>(cfg.Muted ?? new List<string>()),
                Groups = new List<GroupInfo>(cfg.Groups ?? new List<GroupInfo>()),
                UpdateFeed = cfg.UpdateFeed ?? ""
            };
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(onDisk,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, AppPaths.ConfigFile, true);
        }
        catch
        {
        }
    }

    private static string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        try
        {
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null,
                DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return password;
        }
    }

    private static string UnprotectPassword(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored;
        try
        {
            var bytes = Convert.FromBase64String(stored.Substring(6));
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return "";
        }
    }

    public void Update(Action<AppConfig> mutate)
    {
        lock (_lock)
        {
            mutate(Config);
            Save(Config);
        }
    }

    public string SnapshotPassword()
    {
        lock (_lock) return Config.Password;
    }
}

public static class PortUtil
{
    public static int FindFreePort(int start)
    {
        for (var port = start; port < start + 100; port++)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
            }
        }

        var fallback = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        fallback.Start();
        var p = ((System.Net.IPEndPoint)fallback.LocalEndpoint).Port;
        fallback.Stop();
        return p;
    }
}

public static class NetUtil
{
    /// <summary>Best-effort first non-loopback IPv4 address of this computer.</summary>
    public static string? LocalIPv4()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var text = unicast.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    return text;
                }
            }
        }
        catch
        {
        }

        return null;
    }
}
