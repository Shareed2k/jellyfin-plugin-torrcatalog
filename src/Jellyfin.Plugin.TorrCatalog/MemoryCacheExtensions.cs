using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.TorrCatalog;

internal static class MemoryCacheExtensions
{
    /// <summary>
    /// Caches the in-flight task, so concurrent callers share one upstream request; failures are evicted.
    /// The fetch must not use the caller's token (other callers share it); callers stop waiting via WaitAsync.
    /// </summary>
    // GetOrCreate alone isn't atomic: a details page and /Items?ids= arriving together did start the same
    // Prowlarr search twice. The lock only covers the in-memory lookup (the fetch runs outside it).
    static readonly Lock CreateLock = new();

    public static async Task<T> GetOrFetchAsync<T>(this IMemoryCache cache, string key, TimeSpan ttl, Func<Task<T>> fetch, CancellationToken ct)
    {
        Lazy<Task<T>> lazy;
        lock (CreateLock)
        {
            lazy = cache.GetOrCreate(key, e =>
            {
                e.AbsoluteExpirationRelativeToNow = ttl;
                return new Lazy<Task<T>>(fetch);
            })!;
        }

        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            cache.Remove(key);
            throw;
        }
    }
}
