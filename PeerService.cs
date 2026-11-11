using System.Net;
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
    public DateTime LastSeen { get; set; }
}

public sealed class DiscoveryMessage
{
    public string Type { get; set; } = "hello";
    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int HttpPort { get; set; }
}

public sealed class PeerService
{
    private readonly ConfigService _config;
    private readonly EventHub _hub;
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly object _lock = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private Task? _expireTask;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PeerService(ConfigService config, EventHub hub)
    {
        _config = config;
        _hub = hub;
    }

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
            _hub.Publish(new { type = "error", message = "LAN discovery could not start: " + ex.Message });
            return;
        }

        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        _expireTask = Task.Run(() => ExpireLoopAsync(_cts.Token));
        _ = Task.Run(() => SendHelloAsync());
    }

    public void Stop()
    {
        try
        {
            if (_udp != null)
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
    }

    public void Announce() => _ = Task.Run(() => SendHelloAsync());

    public IReadOnlyList<object> GetPeerDtos()
    {
        lock (_lock)
        {
            return _peers.Values
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => (object)new { id = p.Id, name = p.Name, ip = p.Ip, port = p.Port })
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

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var msg = JsonSerializer.Deserialize<DiscoveryMessage>(json, JsonOptions);
                if (msg == null || string.IsNullOrEmpty(msg.Id)) continue;
                if (msg.Id == _config.Config.Id) continue;

                if (msg.Type == "bye")
                {
                    if (RemovePeer(msg.Id)) PublishPeers();
                    continue;
                }

                if (msg.Type != "hello" || msg.HttpPort <= 0) continue;

                var isNew = UpsertPeer(msg, result.RemoteEndPoint.Address);
                if (isNew)
                {
                    _ = SendHelloToAsync(result.RemoteEndPoint.Address);
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

    private bool UpsertPeer(DiscoveryMessage msg, IPAddress address)
    {
        lock (_lock)
        {
            var ip = address.ToString();
            if (_peers.TryGetValue(msg.Id, out var existing))
            {
                var changed = existing.Name != msg.Name || existing.Port != msg.HttpPort || existing.Ip != ip;
                existing.Name = msg.Name;
                existing.Port = msg.HttpPort;
                existing.Ip = ip;
                existing.LastSeen = DateTime.UtcNow;
                return changed;
            }

            _peers[msg.Id] = new PeerInfo
            {
                Id = msg.Id,
                Name = msg.Name,
                Ip = ip,
                Port = msg.HttpPort,
                LastSeen = DateTime.UtcNow
            };
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

    private async Task SendHelloAsync(string type = "hello")
    {
        if (_udp == null) return;
        var cfg = _config.Config;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage
        {
            Type = type,
            Id = cfg.Id,
            Name = cfg.Name,
            HttpPort = cfg.HttpPort
        }, JsonOptions);

        foreach (var target in GetBroadcastAddresses())
        {
            try
            {
                await _udp.SendAsync(payload, new IPEndPoint(target, cfg.UdpPort));
            }
            catch
            {
            }
        }
    }

    private async Task SendHelloToAsync(IPAddress address)
    {
        if (_udp == null) return;
        var cfg = _config.Config;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage
        {
            Type = "hello",
            Id = cfg.Id,
            Name = cfg.Name,
            HttpPort = cfg.HttpPort
        }, JsonOptions);

        try
        {
            await _udp.SendAsync(payload, new IPEndPoint(address, cfg.UdpPort));
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
}
