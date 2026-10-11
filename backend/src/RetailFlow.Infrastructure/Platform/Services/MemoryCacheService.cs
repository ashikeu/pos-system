using Microsoft.Extensions.Caching.Memory;
using RetailFlow.Application.Platform.Abstractions;

namespace RetailFlow.Infrastructure.Platform.Services;

/// <summary>
/// In-process cache, the default for single-node deployments (ADR-0011). Redis replaces this
/// behind the same port when more than one instance runs. Only reference data belongs here —
/// stock, credit and payment state are always read from the database.
/// </summary>
internal sealed class MemoryCacheService : ICacheService
{
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;

    public MemoryCacheService(IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public Task<TValue?> GetAsync<TValue>(string key, CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Task.FromResult(_cache.Get<TValue>(key));
    }

    public Task SetAsync<TValue>(
        string key,
        TValue value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        _cache.Set(key, value, absoluteExpiration ?? DefaultExpiration);
        return Task.CompletedTask;
    }

    public async Task<TValue> GetOrAddAsync<TValue>(
        string key,
        Func<CancellationToken, Task<TValue>> factory,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        if (_cache.TryGetValue(key, out TValue? cached) && cached is not null)
        {
            return cached;
        }

        var value = await factory(cancellationToken).ConfigureAwait(false);
        _cache.Set(key, value, absoluteExpiration ?? DefaultExpiration);
        return value;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _cache.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Single-node lock using in-process semaphores. Enough for one API instance plus one worker on
/// the same host, which is the pilot topology; Redis is required before scaling out, and
/// <see cref="IDistributedLock"/> exists so that change is a registration, not a rewrite.
/// </summary>
internal sealed class InProcessDistributedLock : IDistributedLock
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string name,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var gate = Gates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));

        return await gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false)
            ? new Lease(gate)
            : null;
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private bool _released;

        public Lease(SemaphoreSlim gate) => _gate = gate;

        public ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                _gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
