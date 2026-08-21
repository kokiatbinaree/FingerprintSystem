using System.Security.Cryptography;

namespace OfflineFingerprint.Collector.Services;

public class LocalKeyService
{
    private readonly string _file;
    private readonly object _gate = new();
    public LocalKeyService(IHostEnvironment env)
    {
        string dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "storage.key");
    }
    public byte[] GetKey()
    {
        lock (_gate)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local fingerprint storage requires Windows.");
            if (!File.Exists(_file))
            {
                byte[] raw = RandomNumberGenerator.GetBytes(32);
                byte[] protectedData = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_file, protectedData);
                return raw;
            }
            return ProtectedData.Unprotect(File.ReadAllBytes(_file), null, DataProtectionScope.CurrentUser);
        }
    }
}
