using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Cifra segmentos de gravação em repouso (AES-256-GCM).
/// Formato v2 (streaming): magic "SPENC1" (6) + nonce 12 + 0x02 + frames
///   frame = u32be len + tag 16 + ciphertext.
/// Formato v1 (legado): magic + nonce + tag 16 + ciphertext monolítico.
/// Cifra/decifra em chunks (não carrega o arquivo inteiro na RAM).
/// </summary>
public sealed class RecordingCrypto
{
    public const string Extension = ".enc";
    private static readonly byte[] Magic = "SPENC1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int ChunkSize = 1 * 1024 * 1024;
    private const byte VersionFramed = 0x02;

    private readonly byte[] _key;
    private readonly ILogger<RecordingCrypto> _log;

    public RecordingCrypto(IDataProtectionProvider provider, ILogger<RecordingCrypto> log)
    {
        _log = log;
        var protector = provider.CreateProtector("SecurityPlatform.Recordings.v1");
        var material = protector.Protect("recording-master-key-v1");
        _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
    }

    public static bool IsEncryptedPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public string EncryptFile(string plainPath)
    {
        if (!File.Exists(plainPath))
            throw new FileNotFoundException("Segmento não encontrado para cifrar.", plainPath);

        var encPath = plainPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? plainPath
            : plainPath + Extension;

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);

        using (var input = File.OpenRead(plainPath))
        using (var output = File.Create(encPath))
        {
            output.Write(Magic);
            output.Write(nonce);
            output.WriteByte(VersionFramed);

            var buf = new byte[ChunkSize];
            var counter = 0;
            int read;
            while ((read = input.Read(buf, 0, buf.Length)) > 0)
            {
                var chunkNonce = DeriveChunkNonce(nonce, counter++);
                var plain = buf.AsSpan(0, read);
                var cipher = new byte[read];
                var tag = new byte[TagSize];
                using (var aes = new AesGcm(_key, TagSize))
                    aes.Encrypt(chunkNonce, plain, cipher, tag);

                WriteUInt32(output, (uint)read);
                output.Write(tag);
                output.Write(cipher, 0, read);
            }
        }

        try { File.Delete(plainPath); }
        catch (IOException e)
        {
            _log.LogWarning(e, "Cifrado {Enc} mas não removeu o MP4 claro {Plain}", encPath, plainPath);
        }

        return encPath;
    }

    public string DecryptToTemp(string encPath)
    {
        if (!File.Exists(encPath))
            throw new FileNotFoundException("Segmento cifrado não encontrado.", encPath);

        var tmp = Path.Combine(Path.GetTempPath(), $"sp_rec_{Guid.NewGuid():N}.mp4");
        DecryptToFile(encPath, tmp);
        return tmp;
    }

    public void DecryptToFile(string encPath, string plainPath)
    {
        using var input = File.OpenRead(encPath);
        var magic = new byte[Magic.Length];
        if (ReadExact(input, magic) != magic.Length || !magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Magic SPENC1 ausente — não é gravação cifrada da plataforma.");

        var nonce = new byte[NonceSize];
        if (ReadExact(input, nonce) != NonceSize)
            throw new InvalidDataException("Arquivo .enc truncado (nonce).");

        var versionOrFirst = input.ReadByte();
        if (versionOrFirst < 0)
            throw new InvalidDataException("Arquivo .enc truncado.");

        using var output = File.Create(plainPath);

        if (versionOrFirst == VersionFramed)
        {
            var frameIdx = 0;
            while (input.Position < input.Length)
            {
                var lenBuf = new byte[4];
                var n = ReadExact(input, lenBuf);
                if (n == 0) break;
                if (n < 4) throw new InvalidDataException("Frame header truncado.");
                var len = (int)ReadUInt32(lenBuf);
                if (len <= 0 || len > ChunkSize * 2)
                    throw new InvalidDataException($"Frame cifrado inválido (len={len}).");

                var tag = new byte[TagSize];
                if (ReadExact(input, tag) != TagSize)
                    throw new InvalidDataException("Tag GCM ausente.");

                var cipher = new byte[len];
                if (ReadExact(input, cipher) != len)
                    throw new InvalidDataException("Ciphertext truncado.");

                var plain = new byte[len];
                var chunkNonce = DeriveChunkNonce(nonce, frameIdx++);
                using (var aes = new AesGcm(_key, TagSize))
                    aes.Decrypt(chunkNonce, cipher, tag, plain);
                output.Write(plain, 0, plain.Length);
            }
            return;
        }

        // Legado v1: o byte lido é o primeiro do tag (16 bytes).
        var tagLegacy = new byte[TagSize];
        tagLegacy[0] = (byte)versionOrFirst;
        if (ReadExact(input, tagLegacy.AsSpan(1)) != TagSize - 1)
            throw new InvalidDataException("Tag GCM legado ausente.");

        var cipherLen = checked((int)(input.Length - input.Position));
        if (cipherLen < 0) throw new InvalidDataException("Ciphertext legado inválido.");

        // Streaming legado: lê em chunks na RAM só o restante (tipicamente já indexado).
        var cipherLegacy = new byte[cipherLen];
        if (ReadExact(input, cipherLegacy) != cipherLen)
            throw new InvalidDataException("Ciphertext legado truncado.");

        var plainLegacy = new byte[cipherLen];
        using (var aes = new AesGcm(_key, TagSize))
            aes.Decrypt(nonce, cipherLegacy, tagLegacy, plainLegacy);
        output.Write(plainLegacy, 0, plainLegacy.Length);
    }

    public byte[] DecryptBytes(string encPath)
    {
        var tmp = DecryptToTemp(encPath);
        try { return File.ReadAllBytes(tmp); }
        finally
        {
            try { File.Delete(tmp); } catch { /* */ }
        }
    }

    /// <summary>
    /// Path legível para FFmpeg. Cache <c>.plain.cache</c> ao lado do .enc
    /// evita re-decifrar a cada Range de playback.
    /// </summary>
    public (string Path, bool IsTemp) EnsurePlainPath(string path)
    {
        if (!IsEncryptedPath(path)) return (path, false);

        var cache = path + ".plain.cache";
        try
        {
            if (File.Exists(cache)
                && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(path))
                return (cache, false);
        }
        catch (IOException) { /* regenera */ }

        try
        {
            DecryptToFile(path, cache);
            return (cache, false);
        }
        catch
        {
            var tmp = DecryptToTemp(path);
            return (tmp, true);
        }
    }

    private static byte[] DeriveChunkNonce(byte[] baseNonce, int counter)
    {
        var n = new byte[NonceSize];
        Buffer.BlockCopy(baseNonce, 0, n, 0, NonceSize);
        n[8] ^= (byte)(counter >> 24);
        n[9] ^= (byte)(counter >> 16);
        n[10] ^= (byte)(counter >> 8);
        n[11] ^= (byte)counter;
        return n;
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static uint ReadUInt32(byte[] b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static int ReadExact(Stream s, byte[] buf) => ReadExact(s, buf.AsSpan());

    private static int ReadExact(Stream s, Span<byte> buf)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = s.Read(buf[off..]);
            if (n == 0) return off;
            off += n;
        }
        return off;
    }
}
