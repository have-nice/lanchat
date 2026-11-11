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
}

public sealed class Envelope
{
    public int V { get; set; } = 1;
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
}

public sealed class ChatService
{
    private readonly ConfigService _config;
    private readonly PeerService _peers;
    private readonly CryptoService _crypto;
    private readonly EventHub _hub;

    private readonly Dictionary<string, List<Message>> _history = new();
    private readonly HashSet<string> _messageIds = new();
    private readonly object _historyLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastError = new();

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

    public ChatService(ConfigService config, PeerService peers, CryptoService crypto, EventHub hub)
    {
        _config = config;
        _peers = peers;
        _crypto = crypto;
        _hub = hub;
        LoadHistory();
    }

    private string SelfId => _config.Config.Id;
    private string SelfName => _config.Config.Name;

    public object PublicConfig()
    {
        var cfg = _config.Config;
        return new
        {
            id = cfg.Id,
            name = cfg.Name,
            theme = cfg.Theme,
            passwordSet = !string.IsNullOrEmpty(cfg.Password),
            port = cfg.HttpPort,
            udpPort = cfg.UdpPort
        };
    }

    public object GetState() => new
    {
        self = new { id = SelfId, name = SelfName, port = _config.Config.HttpPort },
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
        if (key == "group") return "LAN Room";
        foreach (var m in messages)
        {
            if (m.FromId == key && !string.IsNullOrWhiteSpace(m.FromName)) return m.FromName;
            if (m.ToId == key && !string.IsNullOrWhiteSpace(m.ToName)) return m.ToName;
        }

        return key;
    }

    public void UpdateConfig(string? name, string? password, string? theme)
    {
        _config.Update(cfg =>
        {
            if (!string.IsNullOrWhiteSpace(name)) cfg.Name = name.Trim();
            if (password != null) cfg.Password = password;
            if (theme is "light" or "dark") cfg.Theme = theme;
        });
        _peers.Announce();
        _hub.Publish(new { type = "config", config = PublicConfig() });
    }

    public Message SendText(string to, string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("Message is empty.");
        if (text.Length > 200_000) throw new ArgumentException("Message is too long.");

        var peerName = to == "group" ? "LAN Room" : _peers.NameOf(to);
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

        AddMessage(to == "group" ? "group" : to, message);

        var payload = new Envelope
        {
            FromId = SelfId,
            FromName = SelfName,
            To = to == "group" ? "group" : to,
            Kind = "text",
            Text = text,
            Ts = message.Ts,
            MsgId = message.Id
        };

        if (to == "group")
        {
            foreach (var peer in AllPeers())
            {
                _ = SendTextToPeerAsync(peer, payload, false);
            }
        }
        else if (_peers.TryGetPeer(to, out var peer))
        {
            _ = SendTextToPeerAsync(peer, payload, true);
        }
        else
        {
            PublishError("offline-" + to, "That person is no longer online.");
        }

        return message;
    }

    public async Task<Message> SendFileAsync(string to, string sourcePath, string fileName, string mime,
        CancellationToken ct)
    {
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "file";
        mime = GuessMime(fileName, mime);

        var msgId = NewId();
        var storedPath = Path.Combine(AppPaths.FilesDir, msgId + SafeExtension(fileName));
        File.Copy(sourcePath, storedPath, true);
        var size = new FileInfo(storedPath).Length;

        var kind = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image" : "file";
        var peerName = to == "group" ? "LAN Room" : _peers.NameOf(to);
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
            Ts = Now()
        };

        AddMessage(to == "group" ? "group" : to, message);

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

            var tasks = targets.Select(peer =>
                SendFileToPeerAsync(peer, message, storedPath, encryptedTemp, ct)).ToList();
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

    public async Task<Message> UploadLocalAsync(string to, string fileName, string mime, Stream body,
        CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "lanchat-up-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = File.Create(temp))
            {
                await body.CopyToAsync(fs, ct);
            }

            return await SendFileAsync(to, temp, fileName, mime, ct);
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

    private async Task SendTextToPeerAsync(PeerInfo peer, Envelope payload, bool reportErrors)
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
                    v = 1,
                    enc = true,
                    fromId = payload.FromId,
                    fromName = payload.FromName,
                    to = payload.To,
                    nonce,
                    data
                };
            }
            else
            {
                envelope = new
                {
                    v = 1,
                    enc = false,
                    fromId = payload.FromId,
                    fromName = payload.FromName,
                    to = payload.To,
                    kind = payload.Kind,
                    text = payload.Text,
                    ts = payload.Ts,
                    msgId = payload.MsgId
                };
            }

            using var content = new StringContent(JsonSerializer.Serialize(envelope, JsonOptions),
                Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"http://{peer.Ip}:{peer.Port}/api/peer/message");
            request.Content = content;
            request.Headers.Add("X-LanChat-Peer", "1");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await Http.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            if (reportErrors)
            {
                PublishError("send-" + peer.Id, $"Could not reach {peer.Name}.");
            }
        }
    }

    private async Task SendFileToPeerAsync(PeerInfo peer, Message message, string storedPath,
        string? encryptedPath, CancellationToken ct)
    {
        try
        {
            var uploadPath = encryptedPath ?? storedPath;
            var query = new StringBuilder("?")
                .Append("fromId=").Append(Uri.EscapeDataString(message.FromId))
                .Append("&fromName=").Append(Uri.EscapeDataString(message.FromName))
                .Append("&to=").Append(Uri.EscapeDataString(message.ToId))
                .Append("&name=").Append(Uri.EscapeDataString(message.FileName ?? "file"))
                .Append("&mime=").Append(Uri.EscapeDataString(message.Mime ?? "application/octet-stream"))
                .Append("&msgId=").Append(Uri.EscapeDataString(message.Id))
                .Append("&ts=").Append(message.Ts)
                .ToString();

            await using var fs = File.OpenRead(uploadPath);
            using var content = new StreamContent(fs);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"http://{peer.Ip}:{peer.Port}/api/peer/file{query}");
            request.Content = content;
            request.Headers.Add("X-LanChat-Peer", "1");
            request.Headers.Add("X-LanChat-Encrypted", encryptedPath != null ? "1" : "0");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(30));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            if (message.ToId != "group")
            {
                PublishError("file-" + peer.Id, $"Could not send the file to {peer.Name}.");
            }
        }
    }

    public (bool Ok, string? Error) ReceiveText(string bodyJson)
    {
        try
        {
            if (bodyJson.Length > 1_000_000) return (false, "Message is too large.");
            var envelope = JsonSerializer.Deserialize<Envelope>(bodyJson, JsonOptions);
            if (envelope == null || string.IsNullOrEmpty(envelope.FromId)) return (false, "Bad envelope.");

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
            if (payload.To != "group" && payload.To != SelfId) return (true, null);
            if ((payload.Text?.Length ?? 0) > 200_000) return (false, "Message is too long.");

            var key = payload.To == "group" ? "group" : payload.FromId;
            var message = new Message
            {
                Id = string.IsNullOrEmpty(payload.MsgId) ? NewId() : payload.MsgId,
                FromId = payload.FromId,
                FromName = string.IsNullOrWhiteSpace(payload.FromName) ? "Unknown" : payload.FromName,
                ToId = payload.To,
                ToName = payload.To == "group" ? "LAN Room" : SelfName,
                Kind = "text",
                Text = payload.Text ?? "",
                Ts = payload.Ts > 0 ? payload.Ts : Now()
            };

            AddMessage(key, message);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string? Error)> ReceiveFileAsync(string fromId, string fromName, string to,
        string name, string mime, string msgId, long ts, bool encrypted, Stream body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(msgId)) return (false, "Bad file metadata.");
        if (to != "group" && to != SelfId) return (true, null);

        var temp = Path.Combine(Path.GetTempPath(), "lanchat-in-" + Guid.NewGuid().ToString("N"));
        var finalPath = Path.Combine(AppPaths.FilesDir, msgId + SafeExtension(name));
        try
        {
            await using (var fs = File.Create(temp))
            {
                await body.CopyToAsync(fs, ct);
            }

            if (encrypted)
            {
                if (!_crypto.Enabled) return (false, "Encrypted file but no room password is set.");
                if (!_crypto.DecryptFile(temp, finalPath))
                {
                    _hub.Publish(new
                    {
                        type = "error",
                        message = $"A file from {fromName} could not be decrypted. Check the room password."
                    });
                    return (false, "Could not decrypt file.");
                }
            }
            else
            {
                File.Move(temp, finalPath, true);
            }

            var size = new FileInfo(finalPath).Length;
            mime = GuessMime(name, mime);
            var kind = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image" : "file";
            var key = to == "group" ? "group" : fromId;
            var message = new Message
            {
                Id = msgId,
                FromId = fromId,
                FromName = string.IsNullOrWhiteSpace(fromName) ? "Unknown" : fromName,
                ToId = to,
                ToName = to == "group" ? "LAN Room" : SelfName,
                Kind = kind,
                FileId = msgId,
                FileName = Path.GetFileName(name),
                Mime = mime,
                Size = size,
                Ts = ts > 0 ? ts : Now()
            };

            AddMessage(key, message);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
            }
        }
    }

    public (string Path, string Mime, string Name)? FindFile(string fileId)
    {
        lock (_historyLock)
        {
            foreach (var list in _history.Values)
            {
                var message = list.FirstOrDefault(m => m.FileId == fileId);
                if (message != null)
                {
                    var path = Path.Combine(AppPaths.FilesDir, fileId + SafeExtension(message.FileName ?? ""));
                    if (File.Exists(path))
                    {
                        return (path, message.Mime ?? "application/octet-stream", message.FileName ?? "file");
                    }
                }
            }
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(AppPaths.FilesDir, fileId + ".*"))
            {
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
                    if (!string.IsNullOrEmpty(m.FileId))
                    {
                        try
                        {
                            var path = Path.Combine(AppPaths.FilesDir,
                                m.FileId + SafeExtension(m.FileName ?? ""));
                            if (File.Exists(path)) File.Delete(path);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            SaveHistoryLocked();
        }

        _hub.Publish(new { type = "cleared", key });
    }

    private List<PeerInfo> AllPeers() => _peers.GetPeers();

    private void AddMessage(string key, Message message)
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
                if (list.Count > 2000)
                {
                    foreach (var old in list.Take(list.Count - 2000)) _messageIds.Remove(old.Id);
                    list.RemoveRange(0, list.Count - 2000);
                }

                SaveHistoryLocked();
            }
        }

        if (added)
        {
            _hub.Publish(new { type = "message", key, message });
        }
    }

    private void PublishError(string throttleKey, string text)
    {
        var now = DateTime.UtcNow;
        if (_lastError.TryGetValue(throttleKey, out var last) && (now - last).TotalSeconds < 5) return;
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
                foreach (var m in list) _messageIds.Add(m.Id);
            }
        }
        catch
        {
        }
    }

    public void SaveHistory()
    {
        lock (_historyLock)
        {
            SaveHistoryLocked();
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

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
