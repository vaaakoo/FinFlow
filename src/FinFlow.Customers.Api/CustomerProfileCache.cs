using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace FinFlow.Customers.Api;

public sealed class CustomerProfileCache
{
    private readonly IDistributedCache cache;
    private readonly ILogger<CustomerProfileCache> logger;

    public CustomerProfileCache(IDistributedCache cache, ILogger<CustomerProfileCache> logger)
    {
        this.cache = cache;
        this.logger = logger;
    }

    public async Task<CustomerView?> GetAsync(string subject, CancellationToken token)
    {
        try
        {
            var json = await cache.GetStringAsync(Key(subject), token);
            return json is null ? null : JsonSerializer.Deserialize<CustomerView>(json);
        }
        catch (Exception ex) when (ex is RedisException or JsonException)
        {
            logger.LogDebug("Profile cache read failed ({FailureType}).", ex.GetType().Name);
            return null;
        }
    }

    public async Task SetAsync(CustomerView profile, CancellationToken token)
    {
        try
        {
            await cache.SetStringAsync(Key(profile.Subject), JsonSerializer.Serialize(profile),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15) }, token);
        }
        catch (RedisException ex)
        {
            logger.LogDebug("Profile cache write failed ({FailureType}).", ex.GetType().Name);
        }
    }

    public async Task RemoveAsync(string subject, CancellationToken token)
    {
        try
        {
            await cache.RemoveAsync(Key(subject), token);
        }
        catch (RedisException ex)
        {
            logger.LogDebug("Profile cache invalidation failed ({FailureType}).", ex.GetType().Name);
        }
    }

    private static string Key(string subject) => "finflow:profile:" + subject;
}
