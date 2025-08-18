using Microsoft.Extensions.Caching.Memory;

namespace ChatbotApi.Application.Common.Extensions;

public static class IMemoryCacheExtension
{
    public static async Task SetObjectAsync<T>(this IMemoryCache cache, string id, T value,
        double lifespan = 14400, bool sliding = true, bool preserve = true)
    {
        // For IMemoryCache, we don't have async methods, so we'll make this synchronous
        if (sliding)
        {
            cache.Set(id, value, TimeSpan.FromMinutes(lifespan));
        }
        else
        {
            cache.Set(id, value, TimeSpan.FromMinutes(lifespan));
        }
    }

    public static async Task SetObjectAsync<T>(this IMemoryCache cache, string id, T value,
        DateTimeOffset endofLife)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(id);
        cache.Set(id, value, endofLife);
    }

    public static async Task<T?> GetObjectAsync<T>(this IMemoryCache cache, string? id)
    {
        if (id == null)
        {
            return default;
        }

        try
        {
            if (cache.TryGetValue(id, out object? value))
            {
                return (T?)value;
            }
            return default;
        }
        catch (Exception)
        {
            // IMemoryCache doesn't have a RemoveAsync method, so we'll just return default
            return default;
        }
    }
}