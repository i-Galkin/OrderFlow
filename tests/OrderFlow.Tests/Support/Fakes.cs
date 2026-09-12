using System.Collections.Concurrent;
using OrderFlow.Contracts.Dtos;
using OrderFlow.Contracts.Events;
using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Messaging;

namespace OrderFlow.Tests.Support;

/// <summary>Captures everything the code under test would have sent to Kafka.</summary>
public sealed class RecordingEventPublisher : IOrderEventPublisher
{
    public ConcurrentQueue<OrderEvent> Published { get; } = new();

    public ConcurrentQueue<OrderEvent> DeadLettered { get; } = new();

    public Task PublishAsync(OrderEvent orderEvent, CancellationToken ct = default)
    {
        Published.Enqueue(orderEvent);
        return Task.CompletedTask;
    }

    public Task PublishToDeadLetterAsync(OrderEvent orderEvent, string reason, CancellationToken ct = default)
    {
        orderEvent.Reason = reason;
        DeadLettered.Enqueue(orderEvent);
        return Task.CompletedTask;
    }

    public IReadOnlyList<OrderEvent> EventsOfType(string eventType)
        => Published.Where(e => e.EventType == eventType).ToList();
}

/// <summary>In-memory stand-in for the Redis product cache.</summary>
public sealed class InMemoryProductCache : IProductCache
{
    private readonly ConcurrentDictionary<Guid, ProductDto> _entries = new();

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public Task<ProductDto?> GetAsync(Guid productId, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(productId, out var product))
        {
            Hits++;
            return Task.FromResult<ProductDto?>(product);
        }

        Misses++;
        return Task.FromResult<ProductDto?>(null);
    }

    public Task SetAsync(ProductDto product, CancellationToken ct = default)
    {
        _entries[product.Id] = product;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid productId, CancellationToken ct = default)
    {
        _entries.TryRemove(productId, out _);
        return Task.CompletedTask;
    }

    public bool Contains(Guid productId) => _entries.ContainsKey(productId);
}
