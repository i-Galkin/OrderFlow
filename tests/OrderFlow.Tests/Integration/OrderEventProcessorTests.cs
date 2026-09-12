using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Contracts;
using OrderFlow.Contracts.Events;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Persistence;
using OrderFlow.Infrastructure.Processing;
using OrderFlow.Tests.Support;
using StackExchange.Redis;
using Xunit;
using Order = OrderFlow.Infrastructure.Domain.Order;

namespace OrderFlow.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class OrderEventProcessorTests
{
    private readonly PostgresFixture _fixture;

    public OrderEventProcessorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static IConnectionMultiplexer ConnectRedis()
    {
        var options = ConfigurationOptions.Parse(TestInfrastructure.RedisConfiguration);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    }

    private static OrderEventProcessor CreateProcessor(
        OrderFlowDbContext db,
        IConnectionMultiplexer redis,
        RecordingEventPublisher publisher)
    {
        var inventory = new FakeInventoryService(db, redis, NullLogger<FakeInventoryService>.Instance);
        var payments = new FakePaymentService(NullLogger<FakePaymentService>.Instance);

        return new OrderEventProcessor(db, inventory, payments, publisher, NullLogger<OrderEventProcessor>.Instance);
    }

    private async Task<Order> CreateConfirmedOrderAsync(decimal unitPrice, int quantity, int stock)
    {
        var customer = await _fixture.CreateCustomerAsync();
        var product = await _fixture.CreateProductAsync(unitPrice, stock);

        await using var db = _fixture.CreateContext();
        var order = Order.Create(customer.Id, new[]
        {
            new OrderItem { ProductId = product.Id, Quantity = quantity, UnitPrice = unitPrice }
        });
        order.Confirm();

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static OrderEvent ConfirmedEvent(Order order, int attempt = 0) => new()
    {
        EventId = Guid.NewGuid(),
        EventType = OrderEventTypes.OrderConfirmed,
        OrderId = order.Id,
        CustomerId = order.CustomerId,
        TotalAmount = order.TotalAmount,
        Attempt = attempt,
        Items = order.Items.Select(i => new OrderEventItem
        {
            ProductId = i.ProductId,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice
        }).ToList()
    };

    [PostgresRedisFact]
    public async Task A_confirmed_order_is_reserved_charged_and_completed()
    {
        var order = await CreateConfirmedOrderAsync(unitPrice: 25.00m, quantity: 2, stock: 10);
        var publisher = new RecordingEventPublisher();

        using var redis = ConnectRedis();
        await using (var db = _fixture.CreateContext())
        {
            var processor = CreateProcessor(db, redis, publisher);
            await processor.ProcessAsync(ConfirmedEvent(order), CancellationToken.None);
        }

        await using var assertDb = _fixture.CreateContext();
        var stored = await assertDb.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        stored.Status.Should().Be(OrderStatus.Completed);

        var productId = order.Items[0].ProductId;
        var product = await assertDb.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        product.StockQuantity.Should().Be(8);

        publisher.EventsOfType(OrderEventTypes.OrderProcessingStarted).Should().ContainSingle();
        publisher.EventsOfType(OrderEventTypes.OrderCompleted).Should().ContainSingle();
    }

    [PostgresRedisFact]
    public async Task A_declined_payment_fails_the_order()
    {
        var order = await CreateConfirmedOrderAsync(unitPrice: 100.13m, quantity: 1, stock: 10);
        var publisher = new RecordingEventPublisher();

        using var redis = ConnectRedis();
        await using (var db = _fixture.CreateContext())
        {
            var processor = CreateProcessor(db, redis, publisher);
            await processor.ProcessAsync(ConfirmedEvent(order), CancellationToken.None);
        }

        await using var assertDb = _fixture.CreateContext();
        var stored = await assertDb.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        stored.Status.Should().Be(OrderStatus.Failed);
        stored.FailureReason.Should().Be("card_declined");

        publisher.EventsOfType(OrderEventTypes.OrderFailed).Should().ContainSingle();
    }

    [PostgresRedisFact]
    public async Task A_transient_payment_failure_is_surfaced_for_retry()
    {
        var order = await CreateConfirmedOrderAsync(unitPrice: 100.77m, quantity: 1, stock: 10);
        var publisher = new RecordingEventPublisher();

        using var redis = ConnectRedis();
        await using var db = _fixture.CreateContext();
        var processor = CreateProcessor(db, redis, publisher);

        var act = () => processor.ProcessAsync(ConfirmedEvent(order), CancellationToken.None);

        await act.Should().ThrowAsync<TransientProcessingException>();
    }

    [PostgresRedisFact]
    public async Task Redelivery_of_the_same_event_is_ignored()
    {
        var order = await CreateConfirmedOrderAsync(unitPrice: 15.00m, quantity: 1, stock: 10);
        var publisher = new RecordingEventPublisher();
        var orderEvent = ConfirmedEvent(order);

        using var redis = ConnectRedis();
        await using (var db = _fixture.CreateContext())
        {
            var processor = CreateProcessor(db, redis, publisher);
            await processor.ProcessAsync(orderEvent, CancellationToken.None);
        }

        await using (var db = _fixture.CreateContext())
        {
            var processor = CreateProcessor(db, redis, publisher);
            await processor.ProcessAsync(orderEvent, CancellationToken.None);
        }

        await using var assertDb = _fixture.CreateContext();
        var productId = order.Items[0].ProductId;
        var product = await assertDb.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        product.StockQuantity.Should().Be(9);

        var reservations = await assertDb.InventoryReservations
            .AsNoTracking()
            .CountAsync(r => r.OrderId == order.Id);
        reservations.Should().Be(1);
    }

    [PostgresRedisFact]
    public async Task Stock_that_does_not_cover_the_order_is_a_permanent_failure()
    {
        var order = await CreateConfirmedOrderAsync(unitPrice: 20.00m, quantity: 5, stock: 2);
        var publisher = new RecordingEventPublisher();

        using var redis = ConnectRedis();
        await using var db = _fixture.CreateContext();
        var processor = CreateProcessor(db, redis, publisher);

        var act = () => processor.ProcessAsync(ConfirmedEvent(order), CancellationToken.None);

        await act.Should().ThrowAsync<InsufficientStockException>();
    }

    [PostgresRedisFact]
    public async Task Cancelling_an_order_releases_its_reservation()
    {
        var order = await CreateConfirmedOrderAsync(unitPrice: 30.00m, quantity: 3, stock: 20);
        var publisher = new RecordingEventPublisher();
        var productId = order.Items[0].ProductId;

        using var redis = ConnectRedis();
        await using (var db = _fixture.CreateContext())
        {
            var processor = CreateProcessor(db, redis, publisher);
            await processor.ProcessAsync(ConfirmedEvent(order), CancellationToken.None);
        }

        await using (var db = _fixture.CreateContext())
        {
            var processor = CreateProcessor(db, redis, publisher);
            await processor.ProcessAsync(new OrderEvent
            {
                EventId = Guid.NewGuid(),
                EventType = OrderEventTypes.OrderCancelled,
                OrderId = order.Id,
                CustomerId = order.CustomerId,
                TotalAmount = order.TotalAmount
            }, CancellationToken.None);
        }

        await using var assertDb = _fixture.CreateContext();
        var product = await assertDb.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        product.StockQuantity.Should().Be(20);
        (await assertDb.InventoryReservations.AsNoTracking().AnyAsync(r => r.OrderId == order.Id))
            .Should().BeFalse();
    }
}
