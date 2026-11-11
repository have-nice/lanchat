using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LanChat;

public sealed class CryptoService
{
    private readonly Func<string> _password;
    private readonly ConcurrentDictionary<string, byte[]> _keys = new();
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("LanChat.LAN.v1");

    public CryptoService(Func<string> password) => _password = password;

    public bool Enabled => !string.IsNullOrEmpty(_password());

    private byte[] Key()
    {
        var pw = _password();
        return _keys.GetOrAdd(pw, p => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(p), Salt, 120_000, HashAlgorithmName.SHA256, 32));
    }

    private static byte[] MacKey(byte[] key)
    {
        var combined = new byte[key.Length + 3];
        Buffer.BlockCopy(key, 0, combined, 0, key.Length);
        Encoding.UTF8.GetBytes("mac").CopyTo(combined, key.Length);
        return SHA256.HashData(combined);
    }

    public (string Nonce, string Data) EncryptText(string plain)
    {
        var key = Key();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plain);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, pt, ct, tag);
        }

        var combined = new byte[ct.Length + tag.Length];
        Buffer.BlockCopy(ct, 0, combined, 0, ct.Length);
        Buffer.BlockCopy(tag, 0, combined, ct.Length, tag.Length);
        return (Convert.ToBase64String(nonce), Convert.ToBase64String(combined));
    }

    public string? DecryptText(string nonceB64, string dataB64)
    {
        try
        {
            var key = Key();
            var nonce = Convert.FromBase64String(nonceB64);
            var combined = Convert.FromBase64String(dataB64);
            if (nonce.Length != 12 || combined.Length < 16) return null;
            var ct = new byte[combined.Length - 16];
            var tag = new byte[16];
            Buffer.BlockCopy(combined, 0, ct, 0, ct.Length);
            Buffer.BlockCopy(combined, ct.Length, tag, 0, 16);
            var pt = new byte[ct.Length];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Decrypt(nonce, ct, tag, pt);
            }

            return Encoding.UTF8.GetString(pt);
        }
        catch
        {
            return null;
        }
    }

    public void EncryptFile(string inputPath, string outputPath)
    {
        var key = Key();
        var macKey = MacKey(key);
        var iv = RandomNumberGenerator.GetBytes(16);

        using var input = File.OpenRead(inputPath);
        using var output = File.Create(outputPath);
        output.Write(iv, 0, iv.Length);

        using var hmac = new HMACSHA256(macKey);
        hmac.TransformBlock(iv, 0, iv.Length, null, 0);

        using (var hmacStream = new HmacWriteStream(output, hmac))
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            using var crypto = new CryptoStream(hmacStream, encryptor, CryptoStreamMode.Write, true);
            input.CopyTo(crypto);
        }

        hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        output.Write(hmac.Hash!, 0, hmac.Hash!.Length);
    }

    public bool DecryptFile(string inputPath, string outputPath)
    {
        try
        {
            var key = Key();
            var macKey = MacKey(key);

            using var input = File.OpenRead(inputPath);
            var length = input.Length;
            if (length < 16 + 32 + 16) return false;

            var iv = new byte[16];
            input.ReadExactly(iv);
            var cipherLength = length - 16 - 32;

            using var hmac = new HMACSHA256(macKey);
            hmac.TransformBlock(iv, 0, iv.Length, null, 0);

            var buffer = new byte[81920];
            long remaining = cipherLength;
            while (remaining > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) return false;
                hmac.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
            }

            hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var tag = new byte[32];
            input.ReadExactly(tag);
            if (!CryptographicOperations.FixedTimeEquals(hmac.Hash!, tag)) return false;

            input.Position = 16;
            using var bounded = new BoundedReadStream(input, cipherLength);
            using var output = File.Create(outputPath);
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            using var crypto = new CryptoStream(bounded, decryptor, CryptoStreamMode.Read, true);
            crypto.CopyTo(output);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
            catch
            {
            }

            return false;
        }
    }

    private sealed class HmacWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly HMACSHA256 _hmac;

        public HmacWriteStream(Stream inner, HMACSHA256 hmac)
        {
            _inner = inner;
            _hmac = hmac;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hmac.TransformBlock(buffer, offset, count, null, 0);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var tmp = buffer.ToArray();
            _hmac.TransformBlock(tmp, 0, tmp.Length, null, 0);
            _inner.Write(tmp, 0, tmp.Length);
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var toRead = (int)Math.Min(count, _remaining);
            var read = _inner.Read(buffer, offset, toRead);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
