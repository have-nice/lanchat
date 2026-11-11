using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanChat;

public sealed class AppConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Environment.UserName;
    public int HttpPort { get; set; } = 47821;
    public int UdpPort { get; set; } = 47822;
    public string Password { get; set; } = "";
    public string Theme { get; set; } = "dark";
}

public static class AppPaths
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanChat");

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
                Password = ProtectPassword(cfg.Password)
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
