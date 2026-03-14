using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Core.AI;

public sealed class AsyncCacheHelper(IMemoryCache memoryCache)
{
    private readonly IMemoryCache _memoryCache = memoryCache;

    public bool TryGetValue<T>(string key, out T? value)
    {
        if (_memoryCache.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public void Set<T>(string key, T value, TimeSpan ttl)
        => _memoryCache.Set(key, value, ttl);

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan baseTtl,
        int jitterMinutes = 0,
        CancellationToken cancellationToken = default)
    {
        var value = await _memoryCache.GetOrCreateAsync(key, async entry =>
        {
            var ttl = baseTtl;

            if (jitterMinutes > 0)
                ttl += TimeSpan.FromMinutes(Random.Shared.Next(0, jitterMinutes));

            entry.AbsoluteExpirationRelativeToNow = ttl;

            return await factory(cancellationToken);
        });

        return value!;
    }
}
