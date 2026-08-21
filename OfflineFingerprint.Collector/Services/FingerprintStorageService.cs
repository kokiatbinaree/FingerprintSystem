using System.Security.Cryptography;

namespace OfflineFingerprint.Collector.Services;

public sealed class FingerprintStorageService
{
    private readonly string _root;
    private readonly LocalKeyService _keys;
    public FingerprintStorageService(IHostEnvironment env, LocalKeyService keys)
    {
        _keys = keys;
        _root = Path.Combine(env.ContentRootPath, "data", "fingerprints");
        Directory.CreateDirectory(_root);
    }
    public async Task<(string FileName, int Width, int Height)> SaveGrayAsync(Guid personId, string finger, string position, byte[] gray, int width, int height, CancellationToken ct)
    {
        string rel = Path.Combine(personId.ToString("N"), finger, position);
        string dir = Path.Combine(_root, rel);
        Directory.CreateDirectory(dir);
        string name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bin";
        string full = Path.Combine(dir, name);
        byte[] png = PngEncoder.EncodeGrayscale(gray, width, height);
        byte[] encrypted = Encrypt(png, _keys.GetKey());
        await File.WriteAllBytesAsync(full, encrypted, ct);
        return (Path.Combine(rel, name), width, height);
    }
    public async Task<byte[]> ReadDecryptedAsync(string relativeName, CancellationToken ct)
    {
        string full = Path.Combine(_root, relativeName);
        if (!File.Exists(full)) throw new FileNotFoundException("Fingerprint file not found.", full);
        byte[] encrypted = await File.ReadAllBytesAsync(full, ct);
        return Decrypt(encrypted, _keys.GetKey());
    }
    private static byte[] Encrypt(byte[] plain, byte[] key)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] cipher = new byte[plain.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        byte[] all = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, all, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, all, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, all, nonce.Length + tag.Length, cipher.Length);
        return all;
    }
    private static byte[] Decrypt(byte[] encrypted, byte[] key)
    {
        if (encrypted.Length < 28) throw new InvalidDataException("Encrypted fingerprint file is invalid.");
        byte[] nonce = encrypted[..12];
        byte[] tag = encrypted[12..28];
        byte[] cipher = encrypted[28..];
        byte[] plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
