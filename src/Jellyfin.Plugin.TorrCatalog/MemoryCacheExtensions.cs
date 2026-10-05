using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.TorrCatalog;

internal static class MemoryCacheExtensions
{
    /// <summary>
    /// Caches the in-flight task, so concurrent callers share one upstream request; failures are evicted.
    /// The fetch must not use the caller's token (other callers share it); callers stop waiting via WaitAsync.
    /// </summary>
    // ponytail: GetOrCreate isn't atomic, a rare race can issue a duplicate request. Keyed lock if that matters.
    public static async Task<T> GetOrFetchAsync<T>(this IMemoryCache cache, string key, TimeSpan ttl, Func<Task<T>> fetch, CancellationToken ct)
    {
        var lazy = cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = ttl;
            return new Lazy<Task<T>>(fetch);
        })!;

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
