using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace LanChat;

/// <summary>
/// Signs and verifies peer-to-peer HTTP requests with a key derived from the room password.
/// A valid signature proves the sender knows the password, which is the handshake: there is no
/// separate session, so every request is authenticated on its own.
/// </summary>
public static class PeerAuth
{
    public const string PeerHeader = "X-LanChat-Peer";
    public const string FromHeader = "X-LanChat-From";
    public const string TsHeader = "X-LanChat-Ts";
    public const string SigHeader = "X-LanChat-Sig";
    public const string KeyHeader = "X-LanChat-Key";
    public const string EncryptedHeader = "X-LanChat-Encrypted";

    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static string Signature(CryptoService crypto, string fromId, string ts, string method, string pathAndQuery) =>
        crypto.Sign(fromId + "|" + ts + "|" + method + "|" + pathAndQuery);

    public static void Apply(HttpRequestMessage request, CryptoService crypto, string selfId, string pathAndQuery)
    {
        request.Headers.Remove(PeerHeader);
        request.Headers.Add(PeerHeader, "1");
        request.Headers.Add(FromHeader, selfId);
        request.Headers.Add(KeyHeader, crypto.KeyCheck());
        if (!crypto.Enabled) return;

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        request.Headers.Add(TsHeader, ts);
        request.Headers.Add(SigHeader, Signature(crypto, selfId, ts, request.Method.Method, pathAndQuery));
    }

    /// <summary>Validates the signature of an incoming peer request. Open mode (no password) accepts all.</summary>
    public static bool Validate(HttpRequest request, CryptoService crypto, out string fromId, out string? error)
    {
        fromId = request.Headers[FromHeader].ToString();
        error = null;

        if (!crypto.Enabled) return true;

        var ts = request.Headers[TsHeader].ToString();
        var sig = request.Headers[SigHeader].ToString();
        if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(sig))
        {
            error = "Peer authentication required.";
            return false;
        }

        if (!long.TryParse(ts, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sentMs))
        {
            error = "Bad timestamp.";
            return false;
        }

        var skew = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(sentMs);
        if (skew.Duration() > MaxSkew)
        {
            error = "Stale request. Check the clock on both computers.";
            return false;
        }

        var keyCheck = request.Headers[KeyHeader].ToString();
        if (!string.IsNullOrEmpty(keyCheck) &&
            !string.Equals(keyCheck, crypto.KeyCheck(), StringComparison.Ordinal))
        {
            error = "Wrong room password.";
            return false;
        }

        var pathAndQuery = (request.Path.Value ?? "/") + (request.QueryString.Value ?? "");
        var payload = fromId + "|" + ts + "|" + request.Method + "|" + pathAndQuery;
        if (!crypto.Verify(payload, sig))
        {
            error = "Wrong room password or tampered request.";
            return false;
        }

        return true;
    }
}

public sealed class RateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastMs;
        public int Failures;
        public long BannedUntilMs;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bucket> _buckets = new();

    public bool Allow(string key, int capacity = 240, double refillPerSecond = 4)
    {
        var now = Environment.TickCount64;
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = capacity, LastMs = now });
        lock (bucket)
        {
            if (bucket.BannedUntilMs > now) return false;
            if (bucket.LastMs == 0) bucket.LastMs = now;
            var elapsed = Math.Max(0, now - bucket.LastMs) / 1000.0;
            bucket.LastMs = now;
            bucket.Tokens = Math.Min(capacity, bucket.Tokens + elapsed * refillPerSecond);
            if (bucket.Tokens < 1) return false;
            bucket.Tokens -= 1;
            return true;
        }
    }

    public void RegisterAuthFailure(string key)
    {
        var now = Environment.TickCount64;
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket { LastMs = now });
        lock (bucket)
        {
            bucket.Failures++;
            if (bucket.Failures >= 8)
            {
                bucket.BannedUntilMs = now + 300_000;
                bucket.Failures = 0;
            }
        }
    }

    public void Reset(string key)
    {
        if (_buckets.TryGetValue(key, out var bucket))
        {
            lock (bucket) bucket.Failures = 0;
        }
    }
}
