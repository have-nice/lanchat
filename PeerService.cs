using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LanChat;

public sealed class PeerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public bool KeyOk { get; set; } = true;
    public DateTime LastSeen { get; set; }
}

public sealed class DiscoveryMessage
{
    public string Type { get; set; } = "hello";
    public int V { get; set; } = 2;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int HttpPort { get; set; }
    public string Room { get; set; } = "";
    public string KeyCheck { get; set; } = "";
}

public sealed class PeerService
{
    private readonly ConfigService _config;
    private readonly EventHub _hub;
    private readonly CryptoService _crypto;
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly object _lock = new();
    private UdpClient? _udp;
    private UdpClient? _udp6;
    private CancellationTokenSource? _cts;
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Raised when a peer we did not know about becomes visible.</summary>
    public event Action<PeerInfo>? PeerAppeared;

    public PeerService(ConfigService config, EventHub hub, CryptoService crypto)
    {
        _config = config;
        _hub = hub;
        _crypto = crypto;
    }

    private string Room => (_config.Config.Room ?? "").Trim();

    public void Start()
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _config.Config.UdpPort));
            _udp.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            _udp = null;
            _hub.Publish(new { type = "error", message = "LAN discovery could not start: " + ex.Message });
        }

        try
        {
            _udp6 = new UdpClient(AddressFamily.InterNetworkV6);
            _udp6.Client.DualMode = false;
            _udp6.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, _config.Config.UdpPort));
        }
        catch
        {
            _udp6 = null;
        }

        if (_udp == null && _udp6 == null) return;

        _cts = new CancellationTokenSource();
        if (_udp != null) _ = Task.Run(() => ReceiveLoopAsync(_udp, _cts.Token));
        if (_udp6 != null) _ = Task.Run(() => ReceiveLoopAsync(_udp6, _cts.Token));
        _ = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        _ = Task.Run(() => ExpireLoopAsync(_cts.Token));
        _ = Task.Run(() => SendHelloAsync());
    }

    public void Stop()
    {
        try
        {
            if (_udp != null || _udp6 != null)
            {
                SendHelloAsync("bye").Wait(500);
            }
        }
        catch
        {
        }

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _udp?.Dispose();
        }
        catch
        {
        }

        try
        {
            _udp6?.Dispose();
        }
        catch
        {
        }
    }

    public void Announce() => _ = Task.Run(() => SendHelloAsync());

    public bool IsBlocked(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_lock)
        {
            return (_config.Config.Blocked ?? new List<string>()).Contains(id);
        }
    }

    public void SetBlocked(string id, bool blocked)
    {
        if (string.IsNullOrEmpty(id)) return;
        _config.Update(cfg =>
        {
            cfg.Blocked ??= new List<string>();
            cfg.Blocked.RemoveAll(x => x == id);
            if (blocked) cfg.Blocked.Add(id);
        });
        if (blocked) RemovePeer(id);
        PublishPeers();
    }

    public IReadOnlyList<object> GetPeerDtos()
    {
        lock (_lock)
        {
            return _peers.Values
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => (object)new
                {
                    id = p.Id,
                    name = p.Name,
                    ip = p.Ip,
                    port = p.Port,
                    keyOk = p.KeyOk
                })
                .ToList();
        }
    }

    public bool TryGetPeer(string id, out PeerInfo peer)
    {
        lock (_lock)
        {
            return _peers.TryGetValue(id, out peer!);
        }
    }

    public List<PeerInfo> GetPeers()
    {
        lock (_lock)
        {
            return _peers.Values.ToList();
        }
    }

    public string NameOf(string id)
    {
        lock (_lock)
        {
            return _peers.TryGetValue(id, out var p) ? p.Name : "";
        }
    }

    /// <summary>Manual peer add by "ip", "ip:port", "[ipv6]:port" or a bare IPv6 address.</summary>
    public async Task<(bool Ok, string? Error)> AddPeerAsync(string input, CancellationToken ct)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) return (false, "Enter an address.");

        var host = input;
        int? port = null;
        if (input.StartsWith('['))
        {
            var close = input.IndexOf(']');
            if (close < 0) return (false, "Bad IPv6 address.");
            host = input.Substring(1, close - 1);
            var rest = input.Substring(close + 1).TrimStart(':');
            if (rest.Length > 0)
            {
                if (!int.TryParse(rest, out var parsed)) return (false, "Bad port.");
                port = parsed;
            }
        }
        else
        {
            var parts = input.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var parsed) && parts[0].Length > 0)
            {
                host = parts[0];
                port = parsed;
            }
        }

        if (host.Length == 0) return (false, "Enter an address.");
        var targetPort = port ?? _config.Config.HttpPort;
        var urlHost = host.Contains(':') ? "[" + host + "]" : host;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"http://{urlHost}:{targetPort}/api/peer/info");
            request.Headers.Add(PeerAuth.PeerHeader, "1");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return (false, "That computer answered with " + (int)response.StatusCode + ".");

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var room = root.TryGetProperty("room", out var roomEl) ? roomEl.GetString() : null;
            var keyCheck = root.TryGetProperty("keyCheck", out var keyEl) ? keyEl.GetString() : null;
            var remotePort = root.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out var p) ? p : targetPort;

            if (string.IsNullOrEmpty(id)) return (false, "That computer is not running LAN Chat.");
            if (id == _config.Config.Id) return (false, "That is this computer.");
            if (!string.Equals((room ?? "").Trim(), Room, StringComparison.OrdinalIgnoreCase))
                return (false, $"That computer is in a different room ({(string.IsNullOrWhiteSpace(room) ? "Default" : room)}).");
            if (IsBlocked(id)) return (false, "That peer is blocked.");

            var keyOk = string.Equals(keyCheck ?? "open", _crypto.KeyCheck(), StringComparison.Ordinal);
            var isNew = UpsertPeer(new DiscoveryMessage
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? host : name,
                HttpPort = remotePort,
                Room = room ?? "",
                KeyCheck = keyCheck ?? ""
            }, host, keyOk, out var peer);

            PublishPeers();
            if (isNew && peer != null) PeerAppeared?.Invoke(peer);
            return keyOk || !_crypto.Enabled
                ? (true, null)
                : (true, "Password mismatch: messages will be rejected until both sides use the same room password.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Timed out reaching that address.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                if (json.Length > 4096) continue;
                var msg = JsonSerializer.Deserialize<DiscoveryMessage>(json, JsonOptions);
                if (msg == null || string.IsNullOrEmpty(msg.Id)) continue;
                if (msg.Id == _config.Config.Id) continue;
                if (IsBlocked(msg.Id)) continue;

                if (msg.Type == "bye")
                {
                    if (RemovePeer(msg.Id)) PublishPeers();
                    continue;
                }

                if (msg.Type != "hello" || msg.HttpPort <= 0) continue;
                if (!string.Equals((msg.Room ?? "").Trim(), Room, StringComparison.OrdinalIgnoreCase)) continue;

                var keyOk = string.Equals(msg.KeyCheck ?? "open", _crypto.KeyCheck(), StringComparison.Ordinal);
                var isNew = UpsertPeer(msg, result.RemoteEndPoint.Address.ToString(), keyOk, out var peer);
                if (isNew)
                {
                    _ = SendHelloToAsync(result.RemoteEndPoint.Address);
                    if (peer != null) PeerAppeared?.Invoke(peer);
                }

                if (isNew) PublishPeers();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private bool UpsertPeer(DiscoveryMessage msg, string ip, bool keyOk, out PeerInfo? peer)
    {
        lock (_lock)
        {
            if (_peers.TryGetValue(msg.Id, out var existing))
            {
                var changed = existing.Name != msg.Name || existing.Port != msg.HttpPort ||
                              existing.Ip != ip || existing.KeyOk != keyOk;
                existing.Name = msg.Name;
                existing.Port = msg.HttpPort;
                existing.Ip = ip;
                existing.KeyOk = keyOk;
                existing.LastSeen = DateTime.UtcNow;
                peer = changed ? existing : null;
                return false;
            }

            var info = new PeerInfo
            {
                Id = msg.Id,
                Name = msg.Name,
                Ip = ip,
                Port = msg.HttpPort,
                KeyOk = keyOk,
                LastSeen = DateTime.UtcNow
            };
            _peers[msg.Id] = info;
            peer = info;
            return true;
        }
    }

    private bool RemovePeer(string id)
    {
        lock (_lock)
        {
            return _peers.Remove(id);
        }
    }

    private void PublishPeers() => _hub.Publish(new { type = "peers", peers = GetPeerDtos() });

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
                await SendHelloAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private async Task ExpireLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                var removed = false;
                lock (_lock)
                {
                    var cutoff = DateTime.UtcNow.AddSeconds(-11);
                    foreach (var id in _peers.Where(p => p.Value.LastSeen < cutoff).Select(p => p.Key).ToList())
                    {
                        _peers.Remove(id);
                        removed = true;
                    }
                }

                if (removed) PublishPeers();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private byte[] HelloPayload(string type) => JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage
    {
        Type = type,
        Id = _config.Config.Id,
        Name = _config.Config.Name,
        HttpPort = _config.Config.HttpPort,
        Room = Room,
        KeyCheck = _crypto.KeyCheck()
    }, JsonOptions);

    private async Task SendHelloAsync(string type = "hello")
    {
        var payload = HelloPayload(type);

        if (_udp != null)
        {
            foreach (var target in GetBroadcastAddresses())
            {
                try
                {
                    await _udp.SendAsync(payload, new IPEndPoint(target, _config.Config.UdpPort));
                }
                catch
                {
                }
            }
        }

        if (_udp6 != null)
        {
            foreach (var target in GetIPv6MulticastTargets())
            {
                try
                {
                    await _udp6.SendAsync(payload, new IPEndPoint(target, _config.Config.UdpPort));
                }
                catch
                {
                }
            }
        }
    }

    private async Task SendHelloToAsync(IPAddress address)
    {
        var payload = HelloPayload("hello");
        try
        {
            var socket = address.AddressFamily == AddressFamily.InterNetworkV6 ? _udp6 : _udp;
            if (socket == null) return;
            await socket.SendAsync(payload, new IPEndPoint(address, _config.Config.UdpPort));
        }
        catch
        {
        }
    }

    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = unicast.IPv4Mask;
                    if (mask == null) continue;
                    var addr = unicast.Address.GetAddressBytes();
                    var m = mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++) bc[i] = (byte)(addr[i] | ~m[i]);
                    var broadcast = new IPAddress(bc);
                    if (!broadcast.Equals(IPAddress.Any) && !broadcast.Equals(IPAddress.Broadcast))
                    {
                        targets.Add(broadcast);
                    }
                }
            }
        }
        catch
        {
        }

        return targets;
    }

    private static IEnumerable<IPAddress> GetIPv6MulticastTargets()
    {
        var targets = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var hasV6 = nic.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6);
                if (!hasV6) continue;
                int index;
                try
                {
                    index = nic.GetIPProperties().GetIPv6Properties().Index;
                }
                catch
                {
                    continue;
                }

                var group = IPAddress.Parse("ff02::1");
                group.ScopeId = index;
                targets.Add(group);
            }
        }
        catch
        {
        }

        return targets;
    }
}
