using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LanChat;

public sealed class Message
{
    public string Id { get; set; } = "";
    public string FromId { get; set; } = "";
    public string FromName { get; set; } = "";
    public string ToId { get; set; } = "";
    public string ToName { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string? Text { get; set; }
    public string? FileId { get; set; }
    public string? FileName { get; set; }
    public string? Mime { get; set; }
    public long Size { get; set; }
    public long Ts { get; set; }

    /// <summary>queued | sent | delivered | read | failed (outgoing messages only).</summary>
    public string State { get; set; } = "";

    public string? ReplyToId { get; set; }
    public string? ReplyToName { get; set; }
    public string? ReplyToText { get; set; }
    public int Duration { get; set; }
    public bool Synced { get; set; }
}

public sealed class Envelope
{
    public int V { get; set; } = 2;
    public bool Enc { get; set; }
    public string FromId { get; set; } = "";
    public string FromName { get; set; } = "";
    public string To { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string? Text { get; set; }
    public long Ts { get; set; }
    public string MsgId { get; set; } = "";
    public string? Nonce { get; set; }
    public string? Data { get; set; }
    public string? ReplyToId { get; set; }
    public string? ReplyToName { get; set; }
    public string? ReplyToText { get; set; }
    public int Duration { get; set; }
}

public sealed class AckPayload
{
    public List<string> Ids { get; set; } = new();
    public string State { get; set; } = "delivered";
}

public sealed class TypingPayload
{
    public string FromId { get; set; } = "";
    public string FromName { get; set; } = "";
}

public sealed class SyncResponse
{
    public List<Message> Messages { get; set; } = new();
}

public sealed class SearchHit
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public long Ts { get; set; }
    public string Preview { get; set; } = "";
    public string FromName { get; set; } = "";
    public string Kind { get; set; } = "text";
}

public sealed class ChatService
{
    private readonly ConfigService _config;
    private readonly PeerService _peers;
    private readonly CryptoService _crypto;
    private readonly EventHub _hub;

    private readonly Dictionary<string, List<Message>> _history = new();
    private readonly Dictionary<string, Message> _byId = new();
    private readonly HashSet<string> _messageIds = new();
    private readonly object _historyLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastError = new();
    private readonly ConcurrentDictionary<string, DateTime> _typingSent = new();
    private readonly List<OutboxItem> _outbox = new();
    private readonly HashSet<string> _outboxKeys = new();
    private CancellationTokenSource? _cts;
    private bool _startWithWindows;

    private const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private sealed class OutboxItem
    {
        public PeerInfo Peer { get; set; } = new();
        public Envelope Payload { get; set; } = new();
        public string MsgId { get; set; } = "";
        public int Attempts { get; set; }
        public DateTime NextAttempt { get; set; }
    }

    public ChatService(ConfigService config, PeerService peers, CryptoService crypto, EventHub hub)
    {
        _config = config;
        _peers = peers;
        _crypto = crypto;
        _hub = hub;
        _startWithWindows = StartupService.IsEnabled();
        LoadHistory();
        _peers.PeerAppeared += OnPeerAppeared;
    }

    private string SelfId => _config.Config.Id;
    private string SelfName => _config.Config.Name;
    private string RoomName => string.IsNullOrWhiteSpace(_config.Config.Room) ? "Default" : _config.Config.Room;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => OutboxLoopAsync(_cts.Token));
        _ = Task.Run(() => MaintenanceLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        _peers.PeerAppeared -= OnPeerAppeared;
    }

    public object PublicConfig()
    {
        var cfg = _config.Config;
        var ip = NetUtil.LocalIPv4();
        return new
        {
            id = cfg.Id,
            name = cfg.Name,
            room = RoomName,
            theme = cfg.Theme,
            passwordSet = !string.IsNullOrEmpty(cfg.Password),
            port = cfg.HttpPort,
            udpPort = cfg.UdpPort,
            notifications = cfg.Notifications,
            sounds = cfg.Sounds,
            minimizeToTray = cfg.MinimizeToTray,
            startWithWindows = _startWithWindows,
            retentionDays = cfg.FileRetentionDays,
            allowLanWeb = cfg.AllowLanWeb,
            lanUrl = ip == null ? null : $"http://{ip}:{cfg.HttpPort}/",
            muted = cfg.Muted ?? new List<string>(),
            blocked = cfg.Blocked ?? new List<string>(),
            filesSize = FilesSize(),
            version = UpdateService.CurrentVersion,
            updateFeed = cfg.UpdateFeed,
            portable = AppPaths.IsPortable,
            dataDir = AppPaths.DataDir
        };
    }

    public object GetState() => new
    {
        self = new { id = SelfId, name = SelfName, port = _config.Config.HttpPort, room = RoomName },
        peers = _peers.GetPeerDtos(),
        config = PublicConfig()
    };

    public object GetHistory()
    {
        lock (_historyLock)
        {
            var conversations = _history.Select(kv => new
                {
                    id = kv.Key,
                    name = ConversationName(kv.Key, kv.Value),
                    messages = kv.Value.ToList(),
                    lastTs = kv.Value.Count == 0 ? 0 : kv.Value[^1].Ts
                })
                .OrderByDescending(c => c.id == "group")
                .ThenByDescending(c => c.lastTs)
                .Select(c => new { id = c.id, name = c.name, messages = c.messages })
                .ToList();

            return new { conversations };
        }
    }

    private string ConversationName(string key, List<Message> messages)
    {
        if (key == "group") return RoomName;
        foreach (var m in messages)
        {
            if (m.FromId == key && !string.IsNullOrWhiteSpace(m.FromName)) return m.FromName;
            if (m.ToId == key && !string.IsNullOrWhiteSpace(m.ToName)) return m.ToName;
        }

        return _peers.NameOf(key) is { Length: > 0 } name ? name : key;
    }

    public void UpdateSettings(Action<AppConfig> mutate)
    {
        _config.Update(mutate);
        _startWithWindows = StartupService.IsEnabled();
        _peers.Announce();
        _hub.Publish(new { type = "config", config = PublicConfig() });
    }

    public Message SendText(string to, string text, string? replyToId)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("Message is empty.");
        if (text.Length > 200_000) throw new ArgumentException("Message is too long.");

        var key = to == "group" ? "group" : to;
        var peerName = to == "group" ? RoomName : _peers.NameOf(to);
        var reply = string.IsNullOrEmpty(replyToId) ? null : FindById(replyToId);

        var message = new Message
        {
            Id = NewId(),
            FromId = SelfId,
            FromName = SelfName,
            ToId = to,
            ToName = peerName,
            Kind = "text",
            Text = text,
            Ts = Now()
        };

        if (reply != null)
        {
            message.ReplyToId = reply.Id;
            message.ReplyToName = reply.FromId == SelfId ? SelfName : reply.FromName;
            message.ReplyToText = Preview(reply);
        }

        AddMessage(key, message);

        var payload = new Envelope
        {
            FromId = SelfId,
            FromName = SelfName,
            To = key,
            Kind = "text",
            Text = text,
            Ts = message.Ts,
            MsgId = message.Id,
            ReplyToId = message.ReplyToId,
            ReplyToName = message.ReplyToName,
            ReplyToText = message.ReplyToText
        };

        if (to == "group")
        {
            foreach (var peer in AllPeers())
            {
                _ = DeliverAsync(peer, payload, message);
            }
        }
        else if (_peers.TryGetPeer(to, out var peer))
        {
            _ = DeliverAsync(peer, payload, message);
        }
        else
        {
            Enqueue(new PeerInfo { Id = to, Name = peerName, Ip = "", Port = 0 }, payload, message);
            PublishError("offline-" + to, $"{peerName} is offline. Your message will be sent when they return.");
        }

        return message;
    }

    public async Task<Message> SendFileAsync(string to, string sourcePath, string fileName, string mime,
        CancellationToken ct, int duration = 0)
    {
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "file";
        mime = GuessMime(fileName, mime);

        var msgId = NewId();
        var storedPath = Path.Combine(AppPaths.FilesDir, msgId + SafeExtension(fileName));
        File.Copy(sourcePath, storedPath, true);
        var size = new FileInfo(storedPath).Length;

        var kind = KindFor(mime);
        var key = to == "group" ? "group" : to;
        var peerName = to == "group" ? RoomName : _peers.NameOf(to);
        var message = new Message
        {
            Id = msgId,
            FromId = SelfId,
            FromName = SelfName,
            ToId = to,
            ToName = peerName,
            Kind = kind,
            FileId = msgId,
            FileName = fileName,
            Mime = mime,
            Size = size,
            Ts = Now(),
            Duration = duration
        };

        AddMessage(key, message);

        var targets = to == "group"
            ? AllPeers()
            : _peers.TryGetPeer(to, out var one) ? new List<PeerInfo> { one } : new List<PeerInfo>();

        string? encryptedTemp = null;
        try
        {
            if (_crypto.Enabled && targets.Count > 0)
            {
                encryptedTemp = Path.Combine(Path.GetTempPath(), "lanchat-" + msgId + ".enc");
                _crypto.EncryptFile(storedPath, encryptedTemp);
            }

            var tasks = targets.Select(peer => SendFileToPeerAsync(peer, message, encryptedTemp ?? storedPath,
                encryptedTemp != null, ct)).ToList();
            if (tasks.Count > 0) await Task.WhenAll(tasks);
        }
        finally
        {
            if (encryptedTemp != null)
            {
                try
                {
                    File.Delete(encryptedTemp);
                }
                catch
                {
                }
            }
        }

        return message;
    }

    public async Task<Message> UploadLocalAsync(string to, string fileName, string mime, int duration,
        Stream body, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "lanchat-up-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = File.Create(temp))
            {
                await body.CopyToAsync(fs, ct);
            }

            return await SendFileAsync(to, temp, fileName, mime, ct, duration);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
            }
        }
    }

    private async Task DeliverAsync(PeerInfo peer, Envelope payload, Message message)
    {
        if (string.IsNullOrEmpty(peer.Ip) || peer.Port <= 0)
        {
            Enqueue(peer, payload, message);
            return;
        }

        var ok = await SendEnvelopeAsync(peer, payload);
        if (ok)
        {
            RemoveOutbox(peer.Id, message.Id);
            SetState(message, "sent");
        }
        else
        {
            Enqueue(peer, payload, message);
            PublishError("send-" + peer.Id, $"Could not reach {peer.Name}. Retrying automatically.");
        }
    }

    private async Task<bool> SendEnvelopeAsync(PeerInfo peer, Envelope payload)
    {
        try
        {
            object envelope;
            if (_crypto.Enabled)
            {
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                var (nonce, data) = _crypto.EncryptText(json);
                envelope = new
                {
                    v = payload.V,
                    enc = true,
                    fromId = payload.FromId,
                    fromName = payload.FromName,
                    to = payload.To,
                    ts = payload.Ts,
                    msgId = payload.MsgId,
                    nonce,
                    data
                };
            }
            else
            {
                envelope = new
                {
                    v = payload.V,
                    enc = false,
                    fromId = payload.FromId,
                    fromName = payload.FromName,
                    to = payload.To,
                    kind = payload.Kind,
                    text = payload.Text,
                    ts = payload.Ts,
                    msgId = payload.MsgId,
                    replyToId = payload.ReplyToId,
                    replyToName = payload.ReplyToName,
                    replyToText = payload.ReplyToText,
                    duration = payload.Duration
                };
            }

            const string pathAndQuery = "/api/peer/message";
            using var content = new StringContent(JsonSerializer.Serialize(envelope, JsonOptions),
                Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl(peer) + pathAndQuery);
            request.Content = content;
            PeerAuth.Apply(request, _crypto, SelfId, pathAndQuery);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await Http.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task SendFileToPeerAsync(PeerInfo peer, Message message, string uploadPath, bool encrypted,
        CancellationToken ct)
    {
        var total = new FileInfo(uploadPath).Length;
        var query = new StringBuilder("?")
            .Append("fromId=").Append(Uri.EscapeDataString(message.FromId))
            .Append("&fromName=").Append(Uri.EscapeDataString(message.FromName))
            .Append("&to=").Append(Uri.EscapeDataString(message.ToId))
            .Append("&name=").Append(Uri.EscapeDataString(message.FileName ?? "file"))
            .Append("&mime=").Append(Uri.EscapeDataString(message.Mime ?? "application/octet-stream"))
            .Append("&msgId=").Append(Uri.EscapeDataString(message.Id))
            .Append("&ts=").Append(message.Ts)
            .Append("&duration=").Append(message.Duration)
            .ToString();
        var uploadPathAndQuery = "/api/peer/file" + query;
        var statusPathAndQuery = "/api/peer/file/status?msgId=" + Uri.EscapeDataString(message.Id);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                var offset = await QueryReceivedAsync(peer, statusPathAndQuery, ct);
                if (offset > total) offset = 0;
                if (offset == total && total > 0)
                {
                    SetState(message, "sent");
                    return;
                }

                await using var fs = File.OpenRead(uploadPath);
                fs.Seek(offset, SeekOrigin.Begin);
                using var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl(peer) + uploadPathAndQuery);
                request.Content = content;
                PeerAuth.Apply(request, _crypto, SelfId, uploadPathAndQuery);
                request.Headers.Add(PeerAuth.EncryptedHeader, encrypted ? "1" : "0");
                request.Headers.Add("X-LanChat-Offset", offset.ToString());
                request.Headers.Add("X-LanChat-Total", total.ToString());
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(30));
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    SetState(message, "sent");
                    return;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    SetState(message, "failed");
                    PublishError("file-auth-" + peer.Id, $"{peer.Name} rejected the transfer: check the room password.");
                    return;
                }
            }
            catch
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        SetState(message, "failed");
        if (message.ToId != "group")
        {
            PublishError("file-" + peer.Id, $"Could not send the file to {peer.Name}.");
        }
    }

    private async Task<long> QueryReceivedAsync(PeerInfo peer, string pathAndQuery, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl(peer) + pathAndQuery);
            PeerAuth.Apply(request, _crypto, SelfId, pathAndQuery);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return 0;
            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("received", out var el) && el.TryGetInt64(out var value) ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    public (bool Ok, string? Error) ReceiveText(string bodyJson, string expectedFromId)
    {
        try
        {
            if (bodyJson.Length > 1_000_000) return (false, "Message is too large.");
            var envelope = JsonSerializer.Deserialize<Envelope>(bodyJson, JsonOptions);
            if (envelope == null || string.IsNullOrEmpty(envelope.FromId)) return (false, "Bad envelope.");
            if (!string.IsNullOrEmpty(expectedFromId) &&
                !string.Equals(envelope.FromId, expectedFromId, StringComparison.Ordinal))
            {
                return (false, "Sender does not match the authenticated peer.");
            }

            if (IsBlocked(envelope.FromId)) return (true, null);

            Envelope payload;
            if (envelope.Enc)
            {
                if (!_crypto.Enabled) return (false, "Encrypted message but no room password is set.");
                if (string.IsNullOrEmpty(envelope.Nonce) || string.IsNullOrEmpty(envelope.Data))
                    return (false, "Bad encrypted envelope.");
                var plain = _crypto.DecryptText(envelope.Nonce, envelope.Data);
                if (plain == null)
                {
                    _hub.Publish(new
                    {
                        type = "error",
                        message = $"A message from {envelope.FromName} could not be decrypted. Check the room password."
                    });
                    return (false, "Could not decrypt.");
                }

                payload = JsonSerializer.Deserialize<Envelope>(plain, JsonOptions) ?? new Envelope();
            }
            else
            {
                payload = envelope;
            }

            if (string.IsNullOrEmpty(payload.FromId)) return (false, "Bad payload.");
            if (!string.IsNullOrEmpty(expectedFromId) &&
                !string.Equals(payload.FromId, expectedFromId, StringComparison.Ordinal))
            {
                return (false, "Sender does not match the authenticated peer.");
            }

            if (payload.To != "group" && payload.To != SelfId) return (true, null);
            if ((payload.Text?.Length ?? 0) > 200_000) return (false, "Message is too long.");
            if (payload.FromId == SelfId) return (true, null);

            var key = payload.To == "group" ? "group" : payload.FromId;
            var message = new Message
            {
                Id = string.IsNullOrEmpty(payload.MsgId) ? NewId() : payload.MsgId,
                FromId = payload.FromId,
                FromName = string.IsNullOrWhiteSpace(payload.FromName) ? "Unknown" : payload.FromName,
                ToId = payload.To,
                ToName = payload.To == "group" ? RoomName : SelfName,
                Kind = "text",
                Text = payload.Text ?? "",
                Ts = payload.Ts > 0 ? payload.Ts : Now(),
                ReplyToId = payload.ReplyToId,
                ReplyToName = payload.ReplyToName,
                ReplyToText = payload.ReplyToText
            };

            if (AddMessage(key, message))
            {
                if (_peers.TryGetPeer(payload.FromId, out var peer))
                {
                    _ = SendAckAsync(peer, new[] { message.Id }, "delivered");
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string? Error)> ReceiveFileAsync(string fromId, string fromName, string to,
        string name, string mime, string msgId, long ts, int duration, bool encrypted, long offset, long total,
        Stream body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(msgId)) return (false, "Bad file metadata.");
        if (IsBlocked(fromId)) return (true, null);

        var safeId = SafeId(msgId);
        if (safeId.Length == 0) return (false, "Bad file id.");
        if (total > MaxFileBytes) return (false, "That file is larger than the 4 GB limit.");
        if (to != "group" && to != SelfId) return (true, null);

        var ext = SafeExtension(name);
        var partPath = Path.Combine(AppPaths.FilesDir, safeId + ext + ".part");
        var finalPath = Path.Combine(AppPaths.FilesDir, safeId + ext);

        try
        {
            if (File.Exists(finalPath))
            {
                return (true, null);
            }

            var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (existing != offset)
            {
                // The sender and receiver disagree about how much arrived; start clean and let it retry.
                try
                {
                    if (File.Exists(partPath)) File.Delete(partPath);
                }
                catch
                {
                }

                return (false, "Transfer offset mismatch, retrying from the start.");
            }

            await using (var fs = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            {
                await body.CopyToAsync(fs, ct);
            }

            var received = new FileInfo(partPath).Length;
            if (total > 0 && received < total) return (true, null);

            string finalSource;
            if (encrypted)
            {
                if (!_crypto.Enabled)
                {
                    TryDelete(partPath);
                    return (false, "Encrypted file but no room password is set.");
                }

                if (!_crypto.DecryptFile(partPath, finalPath))
                {
                    TryDelete(partPath);
                    _hub.Publish(new
                    {
                        type = "error",
                        message = $"A file from {fromName} could not be decrypted. Check the room password."
                    });
                    return (false, "Could not decrypt file.");
                }

                TryDelete(partPath);
                finalSource = finalPath;
            }
            else
            {
                File.Move(partPath, finalPath, true);
                finalSource = finalPath;
            }

            var size = new FileInfo(finalSource).Length;
            mime = GuessMime(name, mime);
            var key = to == "group" ? "group" : fromId;
            var message = new Message
            {
                Id = safeId,
                FromId = fromId,
                FromName = string.IsNullOrWhiteSpace(fromName) ? "Unknown" : fromName,
                ToId = to,
                ToName = to == "group" ? RoomName : SelfName,
                Kind = KindFor(mime),
                FileId = safeId,
                FileName = Path.GetFileName(name),
                Mime = mime,
                Size = size,
                Ts = ts > 0 ? ts : Now(),
                Duration = duration
            };

            if (AddMessage(key, message) && _peers.TryGetPeer(fromId, out var peer))
            {
                _ = SendAckAsync(peer, new[] { message.Id }, "delivered");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public long ReceivedBytesFor(string msgId)
    {
        var safeId = SafeId(msgId);
        if (safeId.Length == 0) return 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(AppPaths.FilesDir, safeId + "*"))
            {
                if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return new FileInfo(path).Length;
                return new FileInfo(path).Length;
            }
        }
        catch
        {
        }

        return 0;
    }

    public void ApplyAck(List<string>? ids, string? state)
    {
        if (ids == null || ids.Count == 0) return;
        var target = state is "read" or "delivered" ? state : "delivered";
        foreach (var id in ids.Take(500))
        {
            var message = FindById(id);
            if (message != null) SetState(message, target);
        }
    }

    private async Task SendAckAsync(PeerInfo peer, IEnumerable<string> ids, string state)
    {
        var list = ids.Where(i => !string.IsNullOrEmpty(i)).Distinct().Take(200).ToList();
        if (list.Count == 0) return;
        try
        {
            const string pathAndQuery = "/api/peer/ack";
            var body = JsonSerializer.Serialize(new AckPayload { Ids = list, State = state }, JsonOptions);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl(peer) + pathAndQuery);
            request.Content = content;
            PeerAuth.Apply(request, _crypto, SelfId, pathAndQuery);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await Http.SendAsync(request, cts.Token);
        }
        catch
        {
        }
    }

    public void MarkRead(string key)
    {
        if (string.IsNullOrEmpty(key) || key == "group") return;
        if (!_peers.TryGetPeer(key, out var peer)) return;

        List<string> ids;
        lock (_historyLock)
        {
            if (!_history.TryGetValue(key, out var list)) return;
            ids = list.Where(m => m.FromId == key && m.State != "read").Select(m => m.Id).ToList();
            foreach (var m in list)
            {
                if (m.FromId == key) m.State = "read";
            }
        }

        if (ids.Count > 0)
        {
            SaveHistory();
            _ = SendAckAsync(peer, ids, "read");
        }
    }

    public void SendTyping(string to)
    {
        if (string.IsNullOrEmpty(to) || to == "group") return;
        if (!_peers.TryGetPeer(to, out var peer)) return;

        var now = DateTime.UtcNow;
        if (_typingSent.TryGetValue(to, out var last) && (now - last).TotalMilliseconds < 2200) return;
        _typingSent[to] = now;
        _ = SendTypingAsync(peer);
    }

    private async Task SendTypingAsync(PeerInfo peer)
    {
        try
        {
            const string pathAndQuery = "/api/peer/typing";
            var body = JsonSerializer.Serialize(new TypingPayload { FromId = SelfId, FromName = SelfName }, JsonOptions);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl(peer) + pathAndQuery);
            request.Content = content;
            PeerAuth.Apply(request, _crypto, SelfId, pathAndQuery);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var response = await Http.SendAsync(request, cts.Token);
        }
        catch
        {
        }
    }

    public void HandleTyping(string fromId, string fromName)
    {
        if (string.IsNullOrEmpty(fromId) || fromId == SelfId) return;
        if (IsBlocked(fromId)) return;
        _hub.Publish(new
        {
            type = "typing",
            key = fromId,
            name = string.IsNullOrWhiteSpace(fromName) ? "Someone" : fromName
        });
    }

    public SyncResponse BuildSync(string peerId, long since, int limit)
    {
        if (limit <= 0 || limit > 1000) limit = 400;
        var response = new SyncResponse();
        lock (_historyLock)
        {
            if (!_history.TryGetValue("group", out var list)) return response;
            response.Messages = list
                .Where(m => m.ToId == "group" && m.FromId != peerId && m.Ts > since)
                .OrderBy(m => m.Ts)
                .TakeLast(limit)
                .Select(m => new Message
                {
                    Id = m.Id,
                    FromId = m.FromId,
                    FromName = m.FromName,
                    ToId = m.ToId,
                    ToName = m.ToName,
                    Kind = m.Kind,
                    Text = m.Text,
                    FileId = m.FileId,
                    FileName = m.FileName,
                    Mime = m.Mime,
                    Size = m.Size,
                    Ts = m.Ts,
                    ReplyToId = m.ReplyToId,
                    ReplyToName = m.ReplyToName,
                    ReplyToText = m.ReplyToText,
                    Duration = m.Duration,
                    Synced = true
                })
                .ToList();
        }

        return response;
    }

    private void OnPeerAppeared(PeerInfo peer)
    {
        lock (_outbox)
        {
            foreach (var item in _outbox.Where(i => i.Peer.Id == peer.Id))
            {
                item.Peer = peer;
                item.Attempts = 0;
                item.NextAttempt = DateTime.UtcNow;
            }
        }

        _ = RequestSyncAsync(peer);
    }

    public async Task RequestSyncAsync(PeerInfo peer)
    {
        try
        {
            if (string.IsNullOrEmpty(peer.Ip) || peer.Port <= 0) return;
            var since = LastGroupTsFor(peer.Id);
            var pathAndQuery = $"/api/peer/sync?since={since}&limit=400";
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl(peer) + pathAndQuery);
            request.Content = content;
            PeerAuth.Apply(request, _crypto, SelfId, pathAndQuery);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var data = JsonSerializer.Deserialize<SyncResponse>(json, JsonOptions);
            if (data?.Messages == null) return;

            foreach (var message in data.Messages)
            {
                if (message == null || string.IsNullOrEmpty(message.Id)) continue;
                if (message.FromId == SelfId) continue;
                if (IsBlocked(message.FromId)) continue;
                message.Synced = true;
                message.State = "";
                AddMessage("group", message);
            }
        }
        catch
        {
        }
    }

    private long LastGroupTsFor(string peerId)
    {
        lock (_historyLock)
        {
            if (!_history.TryGetValue("group", out var list)) return 0;
            return list.Where(m => m.FromId != peerId).Select(m => m.Ts).DefaultIfEmpty(0).Max();
        }
    }

    public object Search(string query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new { results = new List<SearchHit>() };

        var hits = new List<SearchHit>();
        lock (_historyLock)
        {
            foreach (var kv in _history)
            {
                var name = ConversationName(kv.Key, kv.Value);
                foreach (var m in kv.Value)
                {
                    var haystack = (m.Text ?? "") + " " + (m.FileName ?? "");
                    if (haystack.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hits.Add(new SearchHit
                    {
                        Key = kv.Key,
                        Name = name,
                        Id = m.Id,
                        Ts = m.Ts,
                        Preview = Preview(m),
                        FromName = m.FromName,
                        Kind = m.Kind
                    });
                }
            }
        }

        return new { results = hits.OrderByDescending(h => h.Ts).Take(300).ToList() };
    }

    public long FilesSize()
    {
        long total = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(AppPaths.FilesDir))
            {
                try
                {
                    total += new FileInfo(path).Length;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return total;
    }

    /// <summary>Deletes attachments older than <paramref name="days"/> and their messages. Returns files removed.</summary>
    public int PruneFiles(int days)
    {
        if (days <= 0) return 0;
        var cutoffMs = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        var removed = 0;

        lock (_historyLock)
        {
            foreach (var list in _history.Values)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var message = list[i];
                    if (string.IsNullOrEmpty(message.FileId)) continue;
                    if (message.Ts >= cutoffMs) continue;
                    DeleteBlob(message);
                    _messageIds.Remove(message.Id);
                    _byId.Remove(message.Id);
                    list.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0) SaveHistoryLocked();
        }

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var path in Directory.EnumerateFiles(AppPaths.FilesDir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                    File.Delete(path);
                    removed++;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        if (removed > 0) _hub.Publish(new { type = "refresh" });
        return removed;
    }

    private static void DeleteBlob(Message message)
    {
        try
        {
            if (string.IsNullOrEmpty(message.FileId)) return;
            foreach (var path in Directory.EnumerateFiles(AppPaths.FilesDir,
                         message.FileId + "*"))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public (string Path, string Mime, string Name)? FindFile(string fileId)
    {
        var safeId = SafeId(fileId);
        if (safeId.Length == 0) return null;

        lock (_historyLock)
        {
            foreach (var list in _history.Values)
            {
                var message = list.FirstOrDefault(m => m.FileId == safeId);
                if (message != null)
                {
                    var path = Path.Combine(AppPaths.FilesDir, safeId + SafeExtension(message.FileName ?? ""));
                    if (File.Exists(path))
                    {
                        return (path, message.Mime ?? "application/octet-stream", message.FileName ?? "file");
                    }
                }
            }
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(AppPaths.FilesDir, safeId + ".*"))
            {
                if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                return (path, "application/octet-stream", Path.GetFileName(path));
            }
        }
        catch
        {
        }

        return null;
    }

    public void OpenFile(string fileId)
    {
        var file = FindFile(fileId);
        if (file == null) return;
        Process.Start(new ProcessStartInfo(file.Value.Path) { UseShellExecute = true });
    }

    public void RevealFile(string fileId)
    {
        var file = FindFile(fileId);
        if (file == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Value.Path}\"")
        {
            UseShellExecute = true
        });
    }

    public void ClearHistory(string key)
    {
        lock (_historyLock)
        {
            if (_history.Remove(key, out var list))
            {
                foreach (var m in list)
                {
                    _messageIds.Remove(m.Id);
                    _byId.Remove(m.Id);
                    DeleteBlob(m);
                }
            }

            RebuildIndexesLocked();
            SaveHistoryLocked();
        }

        _hub.Publish(new { type = "cleared", key });
    }

    public void SaveHistory()
    {
        lock (_historyLock)
        {
            SaveHistoryLocked();
        }
    }

    private List<PeerInfo> AllPeers() => _peers.GetPeers();

    private bool IsBlocked(string id) => _peers.IsBlocked(id);

    private bool AddMessage(string key, Message message)
    {
        bool added;
        lock (_historyLock)
        {
            added = _messageIds.Add(message.Id);
            if (added)
            {
                if (!_history.TryGetValue(key, out var list))
                {
                    list = new List<Message>();
                    _history[key] = list;
                }

                list.Add(message);
                _byId[message.Id] = message;
                if (list.Count > 2000)
                {
                    foreach (var old in list.Take(list.Count - 2000))
                    {
                        _messageIds.Remove(old.Id);
                        _byId.Remove(old.Id);
                    }

                    list.RemoveRange(0, list.Count - 2000);
                }

                SaveHistoryLocked();
            }
        }

        if (added)
        {
            _hub.Publish(new { type = "message", key, message });
        }

        return added;
    }

    private Message? FindById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_historyLock)
        {
            return _byId.TryGetValue(id, out var message) ? message : null;
        }
    }

    private static int Rank(string state) => state switch
    {
        "failed" => 5,
        "read" => 4,
        "delivered" => 3,
        "sent" => 2,
        "queued" => 1,
        _ => 0
    };

    private void SetState(Message message, string state)
    {
        if (string.IsNullOrEmpty(state)) return;
        if (Rank(state) <= Rank(message.State)) return;
        message.State = state;
        SaveHistory();
        _hub.Publish(new { type = "state", id = message.Id, state });
    }

    private void Enqueue(PeerInfo peer, Envelope payload, Message message)
    {
        var key = peer.Id + "|" + message.Id;
        lock (_outbox)
        {
            if (!_outboxKeys.Add(key)) return;
            _outbox.Add(new OutboxItem
            {
                Peer = peer,
                Payload = payload,
                MsgId = message.Id,
                Attempts = 0,
                NextAttempt = DateTime.UtcNow.AddSeconds(3)
            });
        }

        if (Rank(message.State) < Rank("queued")) SetState(message, "queued");
    }

    private void RemoveOutbox(string peerId, string msgId)
    {
        lock (_outbox)
        {
            var key = peerId + "|" + msgId;
            if (!_outboxKeys.Remove(key)) return;
            _outbox.RemoveAll(i => i.Peer.Id == peerId && i.MsgId == msgId);
        }
    }

    private async Task OutboxLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            List<OutboxItem> due;
            lock (_outbox)
            {
                due = _outbox.Where(i => i.NextAttempt <= DateTime.UtcNow).ToList();
            }

            foreach (var item in due)
            {
                if (ct.IsCancellationRequested) return;
                if (!_peers.TryGetPeer(item.Peer.Id, out var peer) || string.IsNullOrEmpty(peer.Ip))
                {
                    item.NextAttempt = DateTime.UtcNow.AddSeconds(15);
                    continue;
                }

                var ok = await SendEnvelopeAsync(peer, item.Payload);
                if (ok)
                {
                    RemoveOutbox(peer.Id, item.MsgId);
                    var message = FindById(item.MsgId);
                    if (message != null) SetState(message, "sent");
                    continue;
                }

                item.Attempts++;
                if (item.Attempts >= 8)
                {
                    RemoveOutbox(peer.Id, item.MsgId);
                    var message = FindById(item.MsgId);
                    if (message != null) SetState(message, "failed");
                    PublishError("outbox-" + peer.Id, $"Could not deliver to {peer.Name}. Give up after 8 attempts.");
                }
                else
                {
                    item.NextAttempt = DateTime.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, item.Attempts)));
                }
            }
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        try
        {
            PruneFiles(_config.Config.FileRetentionDays);
        }
        catch
        {
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(6), ct);
                PruneFiles(_config.Config.FileRetentionDays);
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

    private void PublishError(string throttleKey, string text)
    {
        var now = DateTime.UtcNow;
        if (_lastError.TryGetValue(throttleKey, out var last) && (now - last).TotalSeconds < 8) return;
        _lastError[throttleKey] = now;
        _hub.Publish(new { type = "error", message = text });
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(AppPaths.HistoryFile)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, List<Message>>>(
                File.ReadAllText(AppPaths.HistoryFile), JsonOptions);
            if (data == null) return;
            foreach (var kv in data)
            {
                var list = kv.Value.Where(m => m != null && !string.IsNullOrEmpty(m.Id)).ToList();
                if (list.Count > 2000) list = list.Skip(list.Count - 2000).ToList();
                _history[kv.Key] = list;
                foreach (var m in list)
                {
                    _messageIds.Add(m.Id);
                    _byId[m.Id] = m;
                }
            }
        }
        catch
        {
        }
    }

    private void RebuildIndexesLocked()
    {
        _messageIds.Clear();
        _byId.Clear();
        foreach (var list in _history.Values)
        {
            foreach (var m in list)
            {
                _messageIds.Add(m.Id);
                _byId[m.Id] = m;
            }
        }
    }

    private void SaveHistoryLocked()
    {
        try
        {
            var tmp = AppPaths.HistoryFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_history, JsonOptions));
            File.Move(tmp, AppPaths.HistoryFile, true);
        }
        catch
        {
        }
    }

    private static string BaseUrl(PeerInfo peer) =>
        $"http://{(peer.Ip.Contains(':') ? "[" + peer.Ip + "]" : peer.Ip)}:{peer.Port}";

    private static string Preview(Message message)
    {
        if (message.Kind == "image") return "Photo";
        if (message.Kind == "voice") return "Voice message";
        if (message.Kind == "file") return "File: " + (message.FileName ?? "file");
        var text = (message.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 140 ? text.Substring(0, 140) + "…" : text;
    }

    private static string KindFor(string mime)
    {
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "image";
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "voice";
        return "file";
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string SafeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return "";
        foreach (var c in id)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return "";
        }

        return id;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static string GuessMime(string fileName, string mime)
    {
        if (!string.IsNullOrWhiteSpace(mime) &&
            !mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return mime;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".webm" => "audio/webm",
            ".ogg" or ".oga" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private static string SafeExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || ext.Length > 16) return "";
        return ext.All(c => char.IsLetterOrDigit(c) || c == '.') ? ext : "";
    }
}
