using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace OfflineFingerprint.Collector.Services;

public sealed class TokenService
{
    private readonly ConcurrentDictionary<string, TokenInfo> _tokens = new();

    public string Issue(Guid userId, string role)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = new TokenInfo(userId, role, DateTime.UtcNow.AddHours(8));
        return token;
    }

    public bool TryGet(string token, out TokenInfo info)
    {
        if (_tokens.TryGetValue(token, out info!) && info.ExpiresAtUtc > DateTime.UtcNow) return true;
        _tokens.TryRemove(token, out _);
        info = null!;
        return false;
    }

    public sealed record TokenInfo(Guid UserId, string Role, DateTime ExpiresAtUtc);
}
