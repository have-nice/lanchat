using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace LanChat;

public sealed class SendRequest
{
    public string To { get; set; } = "";
    public string? Text { get; set; }
    public string? ReplyToId { get; set; }
}

public sealed class ConfigRequest
{
    public string? Name { get; set; }
    public string? Password { get; set; }
    public string? Theme { get; set; }
    public string? Room { get; set; }
    public bool? Notifications { get; set; }
    public bool? Sounds { get; set; }
    public bool? MinimizeToTray { get; set; }
    public bool? StartWithWindows { get; set; }
    public bool? AllowLanWeb { get; set; }
    public int? FileRetentionDays { get; set; }
    public string? UpdateFeed { get; set; }
}

public sealed class TypingRequest
{
    public string? To { get; set; }
}

public sealed class LoginRequest
{
    public string? Password { get; set; }
}

public sealed class AddPeerRequest
{
    public string? Address { get; set; }
}

public sealed class AckRequest
{
    public List<string>? Ids { get; set; }
    public string? State { get; set; }
}

public sealed class TypingPayloadIn
{
    public string? FromId { get; set; }
    public string? FromName { get; set; }
}

public sealed class LanChatApp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConfigService _configService;
    private readonly CryptoService _crypto;
    private readonly EventHub _hub;
    private readonly PeerService _peers;
    private readonly ChatService _chat;
    private readonly RateLimiter _rate = new();
    private WebApplication? _web;
    private int _port;
    private readonly string _uiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public string Url => $"http://127.0.0.1:{_port}/";
    public ChatService Chat => _chat;
    public PeerService Peers => _peers;
    public ConfigService ConfigService => _configService;

    public LanChatApp()
    {
        _configService = new ConfigService();
        _crypto = new CryptoService(() => _configService.SnapshotPassword());
        _hub = new EventHub();
        _peers = new PeerService(_configService, _hub, _crypto);
        _chat = new ChatService(_configService, _peers, _crypto, _hub);
    }

    public async Task StartAsync()
    {
        _port = PortUtil.FindFreePort(_configService.Config.HttpPort);
        _configService.Update(cfg => cfg.HttpPort = _port);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_port);
            options.Limits.MaxRequestBodySize = 4L * 1024 * 1024 * 1024;
            options.Limits.MaxRequestLineSize = 32 * 1024;
        });

        _web = builder.Build();
        MapEndpoints(_web);
        await _web.StartAsync();
        _peers.Start();
        _chat.Start();
    }

    public async Task StopAsync()
    {
        _chat.Stop();
        _peers.Stop();
        _chat.SaveHistory();
        if (_web != null)
        {
            try
            {
                await _web.StopAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            await _web.DisposeAsync();
        }
    }

    private bool IsLocal(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        return remote != null &&
               (IPAddress.IsLoopback(remote) || remote.Equals(ctx.Connection.LocalIpAddress));
    }

    private void MapEndpoints(WebApplication app)
    {
        // Peer-to-peer API: authenticated with a password-derived signature on every request.
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api/peer"))
            {
                await next();
                return;
            }

            if (ctx.Request.Headers[PeerAuth.PeerHeader].ToString() != "1")
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rate.Allow("peer:" + remote, 300, 5))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            if (ctx.Request.Path.StartsWithSegments("/api/peer/info"))
            {
                await next();
                return;
            }

            if (!PeerAuth.Validate(ctx.Request, _crypto, out var peerId, out var error))
            {
                _rate.RegisterAuthFailure("peer:" + remote);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error }, JsonOptions));
                return;
            }

            if (!string.IsNullOrEmpty(peerId) && _peers.IsBlocked(peerId))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            ctx.Items["peerId"] = peerId ?? "";
            _rate.Reset("peer:" + remote);
            await next();
        });

        // UI and API: loopback uses the per-run token, LAN browsers use a password-backed cookie.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api/peer"))
            {
                // Already authenticated by the peer signature middleware above.
                await next();
                return;
            }

            var isApi = path.StartsWithSegments("/api");
            var local = IsLocal(ctx);

            if (local)
            {
                if (isApi)
                {
                    var token = ctx.Request.Headers["X-LanChat-Token"].ToString();
                    if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["token"].ToString();
                    if (!string.Equals(token, _uiToken, StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }

                await next();
                return;
            }

            if (!isApi)
            {
                if (!_configService.Config.AllowLanWeb)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await next();
                return;
            }

            if (path.StartsWithSegments("/api/login") || path.StartsWithSegments("/api/lanweb/info"))
            {
                await next();
                return;
            }

            if (_configService.Config.AllowLanWeb &&
                _crypto.VerifyWebSession(ctx.Request.Cookies["lanchat_web"]))
            {
                await next();
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });

        app.Use(async (ctx, next) =>
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) ||
                ctx.Request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

            var path = ctx.Request.Path.Value ?? "/";
            if (path == "/") path = "/index.html";

            string resourceName;
            if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                resourceName = "LanChat.icon.ico";
            }
            else
            {
                resourceName = "LanChat.wwwroot." + path.TrimStart('/').Replace('/', '.');
            }

            var assembly = typeof(LanChatApp).Assembly;
            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            ctx.Response.ContentType = MimeType(path);
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";

            if (path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(stream);
                var html = await reader.ReadToEndAsync(ctx.RequestAborted);
                // The local token must never be handed to a browser on the network.
                html = html.Replace("{{TOKEN}}", IsLocal(ctx) ? _uiToken : "", StringComparison.Ordinal);
                await ctx.Response.WriteAsync(html, ctx.RequestAborted);
                return;
            }

            await using (stream)
            {
                await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
        });

        app.MapGet("/api/state", () => Results.Json(_chat.GetState()));
        app.MapGet("/api/history", () => Results.Json(_chat.GetHistory()));

        app.MapPost("/api/send", async (HttpContext ctx) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<SendRequest>(JsonOptions);
            if (request == null || string.IsNullOrWhiteSpace(request.To))
            {
                return Results.BadRequest(new { error = "Missing recipient." });
            }

            try
            {
                var message = _chat.SendText(request.To, request.Text ?? "", request.ReplyToId);
                return Results.Ok(new { ok = true, message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/typing", async (HttpContext ctx) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<TypingRequest>(JsonOptions);
            if (request?.To is { Length: > 0 }) _chat.SendTyping(request.To);
            return Results.Ok();
        });

        app.MapPost("/api/read/{key}", (string key) =>
        {
            _chat.MarkRead(key);
            return Results.Ok();
        });

        app.MapGet("/api/search", (string? q) => Results.Json(_chat.Search(q ?? "")));

        app.MapPost("/api/upload", async (HttpContext ctx) =>
        {
            var query = ctx.Request.Query;
            var to = query["to"].ToString();
            if (string.IsNullOrWhiteSpace(to))
            {
                return Results.BadRequest(new { error = "Missing recipient." });
            }

            var name = query["name"].ToString();
            var mime = query["mime"].ToString();
            int.TryParse(query["duration"].ToString(), out var duration);
            try
            {
                var message = await _chat.UploadLocalAsync(to, name, mime, duration, ctx.Request.Body,
                    ctx.RequestAborted);
                return Results.Ok(new { ok = true, id = message.Id, message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/file/{id}", IResult (string id) =>
        {
            var file = _chat.FindFile(id);
            if (file == null) return Results.NotFound();
            return Results.File(file.Value.Path, file.Value.Mime, enableRangeProcessing: true);
        });

        app.MapPost("/api/open/{id}", (string id) =>
        {
            _chat.OpenFile(id);
            return Results.Ok();
        });

        app.MapPost("/api/reveal/{id}", (string id) =>
        {
            _chat.RevealFile(id);
            return Results.Ok();
        });

        app.MapPost("/api/config", async (HttpContext ctx) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<ConfigRequest>(JsonOptions);
            if (request == null) return Results.BadRequest();

            var passwordAfter = request.Password ?? _configService.Config.Password;
            if (request.AllowLanWeb == true && string.IsNullOrEmpty(passwordAfter))
            {
                return Results.BadRequest(new { error = "Set a room password before turning on browser access." });
            }

            _chat.UpdateSettings(cfg =>
            {
                if (!string.IsNullOrWhiteSpace(request.Name)) cfg.Name = request.Name!.Trim();
                if (request.Password != null) cfg.Password = request.Password;
                if (request.Theme is "light" or "dark") cfg.Theme = request.Theme;
                if (!string.IsNullOrWhiteSpace(request.Room)) cfg.Room = request.Room!.Trim();
                if (request.Notifications.HasValue) cfg.Notifications = request.Notifications.Value;
                if (request.Sounds.HasValue) cfg.Sounds = request.Sounds.Value;
                if (request.MinimizeToTray.HasValue) cfg.MinimizeToTray = request.MinimizeToTray.Value;
                if (request.AllowLanWeb.HasValue) cfg.AllowLanWeb = request.AllowLanWeb.Value;
                if (request.FileRetentionDays.HasValue)
                    cfg.FileRetentionDays = Math.Clamp(request.FileRetentionDays.Value, 0, 3650);
                if (request.UpdateFeed != null) cfg.UpdateFeed = request.UpdateFeed.Trim();
            });

            if (request.StartWithWindows.HasValue)
            {
                var applied = StartupService.Set(request.StartWithWindows.Value);
                if (!applied)
                {
                    return Results.BadRequest(new { error = "Windows did not accept the startup setting." });
                }

                _chat.UpdateSettings(_ => { });
            }

            return Results.Ok(_chat.PublicConfig());
        });

        app.MapPost("/api/block/{id}", (string id) =>
        {
            var blocked = _peers.IsBlocked(id);
            _peers.SetBlocked(id, !blocked);
            _hub.Publish(new { type = "config", config = _chat.PublicConfig() });
            return Results.Ok(new { blocked = !blocked });
        });

        app.MapPost("/api/mute/{key}", (string key) =>
        {
            var muted = false;
            _chat.UpdateSettings(cfg =>
            {
                cfg.Muted ??= new List<string>();
                if (cfg.Muted.Contains(key))
                {
                    cfg.Muted.RemoveAll(x => x == key);
                }
                else
                {
                    cfg.Muted.Add(key);
                    muted = true;
                }
            });
            return Results.Ok(new { muted });
        });

        app.MapDelete("/api/history/{key}", (string key) =>
        {
            _chat.ClearHistory(key);
            return Results.Ok();
        });

        app.MapPost("/api/files/cleanup", () =>
        {
            var removed = _chat.PruneFiles(_configService.Config.FileRetentionDays);
            return Results.Ok(new { removed, filesSize = _chat.FilesSize() });
        });

        app.MapPost("/api/peers/add", async (HttpContext ctx) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<AddPeerRequest>(JsonOptions);
            var (ok, error) = await _peers.AddPeerAsync(request?.Address ?? "", ctx.RequestAborted);
            return ok ? Results.Ok(new { ok = true, warning = error }) : Results.BadRequest(new { error });
        });

        app.MapGet("/api/lanweb/info", () => Results.Json(new
        {
            room = _configService.Config.Room,
            name = _configService.Config.Name,
            browserAccess = _configService.Config.AllowLanWeb
        }));

        app.MapGet("/api/lanweb/qr", IResult () =>
        {
            var ip = NetUtil.LocalIPv4();
            if (ip == null) return Results.NotFound(new { error = "No network connection." });
            var url = $"http://{ip}:{_port}/";
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
            var png = new PngByteQRCode(data).GetGraphic(8);
            return Results.File(png, "image/png");
        });

        app.MapPost("/api/login", async (HttpContext ctx) =>
        {
            var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rate.Allow("login:" + remote, 12, 0.15))
            {
                return Results.Json(new { error = "Too many attempts. Wait a minute." }, statusCode: 429);
            }

            var request = await ctx.Request.ReadFromJsonAsync<LoginRequest>(JsonOptions);
            if (!_configService.Config.AllowLanWeb)
            {
                return Results.Json(new { error = "Browser access is turned off on that computer." }, statusCode: 403);
            }

            if (!_crypto.PasswordMatches(request?.Password))
            {
                _rate.RegisterAuthFailure("login:" + remote);
                return Results.Json(new { error = "Wrong room password." }, statusCode: 401);
            }

            ctx.Response.Cookies.Append("lanchat_web", _crypto.WebSessionToken(), new CookieOptions
            {
                HttpOnly = true,
                Path = "/",
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(7)
            });
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/update/check", async (HttpContext ctx) =>
        {
            var check = await UpdateService.CheckAsync(_configService.Config.UpdateFeed, ctx.RequestAborted);
            return Results.Json(check);
        });

        app.MapPost("/api/update/apply", async (HttpContext ctx) =>
        {
            var check = await UpdateService.CheckAsync(_configService.Config.UpdateFeed, ctx.RequestAborted);
            if (!check.Available || string.IsNullOrWhiteSpace(check.Url))
            {
                return Results.BadRequest(new { error = check.Error ?? "You are already on the latest version." });
            }

            var (path, error) = await UpdateService.DownloadAsync(check.Url!, ctx.RequestAborted);
            if (path == null) return Results.BadRequest(new { error });
            return UpdateService.ApplyAndRestart(path)
                ? Results.Ok(new { ok = true })
                : Results.BadRequest(new { error = "Could not start the updater." });
        });

        app.MapGet("/api/peer/info", () => Results.Json(new
        {
            id = _configService.Config.Id,
            name = _configService.Config.Name,
            room = _configService.Config.Room,
            port = _configService.Config.HttpPort,
            udpPort = _configService.Config.UdpPort,
            keyCheck = _crypto.KeyCheck(),
            passwordSet = !string.IsNullOrEmpty(_configService.Config.Password),
            version = UpdateService.CurrentVersion
        }));

        app.MapPost("/api/peer/message", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            var expected = ctx.Items["peerId"] as string ?? "";
            var (ok, error) = _chat.ReceiveText(body, expected);
            return ok ? Results.Ok() : Results.BadRequest(new { error });
        });

        app.MapGet("/api/peer/file/status", (HttpContext ctx) =>
        {
            var msgId = ctx.Request.Query["msgId"].ToString();
            return Results.Ok(new { received = _chat.ReceivedBytesFor(msgId) });
        });

        app.MapPost("/api/peer/file", async (HttpContext ctx) =>
        {
            var query = ctx.Request.Query;
            var encrypted = ctx.Request.Headers[PeerAuth.EncryptedHeader].ToString() == "1";
            long.TryParse(query["ts"].ToString(), out var ts);
            int.TryParse(query["duration"].ToString(), out var duration);
            long.TryParse(ctx.Request.Headers["X-LanChat-Offset"].ToString(), out var offset);
            long.TryParse(ctx.Request.Headers["X-LanChat-Total"].ToString(), out var total);
            var (ok, error) = await _chat.ReceiveFileAsync(
                query["fromId"].ToString(),
                query["fromName"].ToString(),
                query["to"].ToString(),
                query["name"].ToString(),
                query["mime"].ToString(),
                query["msgId"].ToString(),
                ts,
                duration,
                encrypted,
                offset,
                total,
                ctx.Request.Body,
                ctx.RequestAborted);
            return ok ? Results.Ok() : Results.BadRequest(new { error });
        });

        app.MapPost("/api/peer/ack", async (HttpContext ctx) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<AckRequest>(JsonOptions);
            _chat.ApplyAck(request?.Ids, request?.State);
            return Results.Ok();
        });

        app.MapPost("/api/peer/typing", async (HttpContext ctx) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<TypingPayloadIn>(JsonOptions);
            var expected = ctx.Items["peerId"] as string ?? "";
            if (request == null) return Results.Ok();
            if (!string.IsNullOrEmpty(expected) && request.FromId != expected)
            {
                return Results.BadRequest(new { error = "Sender mismatch." });
            }

            _chat.HandleTyping(request.FromId ?? "", request.FromName ?? "");
            return Results.Ok();
        });

        app.MapPost("/api/peer/sync", (HttpContext ctx) =>
        {
            long.TryParse(ctx.Request.Query["since"].ToString(), out var since);
            int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit);
            var peerId = ctx.Items["peerId"] as string ?? "";
            return Results.Json(_chat.BuildSync(peerId, since, limit));
        });

        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            var (id, reader) = _hub.Subscribe();
            try
            {
                await ctx.Response.WriteAsync("retry: 2000\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    string? message = null;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                        timeout.CancelAfter(TimeSpan.FromSeconds(20));
                        if (await reader.WaitToReadAsync(timeout.Token))
                        {
                            reader.TryRead(out message);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (ctx.RequestAborted.IsCancellationRequested) break;
                    }

                    if (message != null)
                    {
                        await ctx.Response.WriteAsync("data: " + message + "\n\n", ctx.RequestAborted);
                    }
                    else
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ctx.RequestAborted);
                    }

                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _hub.Unsubscribe(id);
            }
        });
    }

    private static string MimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }
}
