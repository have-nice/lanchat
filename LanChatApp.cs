using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat;

public sealed class SendRequest
{
    public string To { get; set; } = "";
    public string? Text { get; set; }
}

public sealed class ConfigRequest
{
    public string? Name { get; set; }
    public string? Password { get; set; }
    public string? Theme { get; set; }
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
    private WebApplication? _web;
    private int _port;
    private readonly string _uiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public string Url => $"http://127.0.0.1:{_port}/";
    public ChatService Chat => _chat;
    public ConfigService ConfigService => _configService;

    public LanChatApp()
    {
        _configService = new ConfigService();
        _crypto = new CryptoService(() => _configService.SnapshotPassword());
        _hub = new EventHub();
        _peers = new PeerService(_configService, _hub);
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
    }

    public async Task StopAsync()
    {
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

    private void MapEndpoints(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            var remote = ctx.Connection.RemoteIpAddress;
            var local = remote != null &&
                        (IPAddress.IsLoopback(remote) || remote.Equals(ctx.Connection.LocalIpAddress));

            if (path.StartsWithSegments("/api/peer"))
            {
                if (ctx.Request.Headers["X-LanChat-Peer"].ToString() != "1")
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await next();
                return;
            }

            if (!local)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (path.StartsWithSegments("/api"))
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

            if (path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(stream);
                var html = await reader.ReadToEndAsync(ctx.RequestAborted);
                html = html.Replace("{{TOKEN}}", _uiToken, StringComparison.Ordinal);
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
                var message = _chat.SendText(request.To, request.Text ?? "");
                return Results.Ok(new { ok = true, message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

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
            try
            {
                var message = await _chat.UploadLocalAsync(to, name, mime, ctx.Request.Body,
                    ctx.RequestAborted);
                return Results.Ok(new { ok = true, id = message.Id, message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/file/{id}", (string id) =>
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
            _chat.UpdateConfig(request.Name, request.Password, request.Theme);
            return Results.Ok(_chat.PublicConfig());
        });

        app.MapDelete("/api/history/{key}", (string key) =>
        {
            _chat.ClearHistory(key);
            return Results.Ok();
        });

        app.MapPost("/api/peer/message", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            var (ok, error) = _chat.ReceiveText(body);
            return ok ? Results.Ok() : Results.BadRequest(new { error });
        });

        app.MapPost("/api/peer/file", async (HttpContext ctx) =>
        {
            var query = ctx.Request.Query;
            var encrypted = ctx.Request.Headers["X-LanChat-Encrypted"].ToString() == "1";
            long.TryParse(query["ts"].ToString(), out var ts);
            var (ok, error) = await _chat.ReceiveFileAsync(
                query["fromId"].ToString(),
                query["fromName"].ToString(),
                query["to"].ToString(),
                query["name"].ToString(),
                query["mime"].ToString(),
                query["msgId"].ToString(),
                ts,
                encrypted,
                ctx.Request.Body,
                ctx.RequestAborted);
            return ok ? Results.Ok() : Results.BadRequest(new { error });
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
