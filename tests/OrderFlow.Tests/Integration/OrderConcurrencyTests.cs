using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Tests.Support;
using Xunit;

namespace OrderFlow.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class OrderConcurrencyTests
{
    private readonly PostgresFixture _fixture;

    public OrderConcurrencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Order> CreatePendingOrderAsync()
    {
        var customer = await _fixture.CreateCustomerAsync();
        var product = await _fixture.CreateProductAsync(19.99m, stock: 50);

        await using var db = _fixture.CreateContext();
        var order = Order.Create(customer.Id, new[]
        {
            new OrderItem { ProductId = product.Id, Quantity = 1, UnitPrice = 19.99m }
        });

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    [PostgresFact]
    public async Task Two_writers_confirming_the_same_order_cannot_both_win()
    {
        var order = await CreatePendingOrderAsync();

        await using var first = _fixture.CreateContext();
        await using var second = _fixture.CreateContext();

        var firstCopy = await first.Orders.SingleAsync(o => o.Id == order.Id);
        var secondCopy = await second.Orders.SingleAsync(o => o.Id == order.Id);

        firstCopy.Confirm();
        await first.SaveChangesAsync();

        secondCopy.Confirm();
        var act = () => second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var assertDb = _fixture.CreateContext();
        var stored = await assertDb.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        stored.Status.Should().Be(OrderStatus.Confirmed);
        stored.Version.Should().Be(order.Version + 1);
    }

    [PostgresFact]
    public async Task Parallel_cancellations_settle_on_a_single_transition()
    {
        var order = await CreatePendingOrderAsync();

        async Task<bool> CancelAsync()
        {
            await using var db = _fixture.CreateContext();
            var copy = await db.Orders.SingleAsync(o => o.Id == order.Id);

            try
            {
                copy.Cancel();
                await db.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
            catch (InvalidStatusTransitionException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(CancelAsync(), CancelAsync(), CancelAsync());

        results.Count(success => success).Should().Be(1);

        await using var assertDb = _fixture.CreateContext();
        var stored = await assertDb.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        stored.Status.Should().Be(OrderStatus.Cancelled);
    }

    [PostgresFact]
    public async Task The_newest_order_of_a_customer_is_listed_first()
    {
        var customer = await _fixture.CreateCustomerAsync();
        var product = await _fixture.CreateProductAsync(10m, stock: 100);

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            await using var db = _fixture.CreateContext();
            var order = Order.Create(customer.Id, new[]
            {
                new OrderItem { ProductId = product.Id, Quantity = 1, UnitPrice = 10m }
            });

            db.Orders.Add(order);
            await db.SaveChangesAsync();
            ids.Add(order.Id);
        }

        await using var assertDb = _fixture.CreateContext();
        var ordered = await assertDb.Orders
            .AsNoTracking()
            .Where(o => o.CustomerId == customer.Id)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => o.Id)
            .ToListAsync();

        ordered.Should().Equal(ids.AsEnumerable().Reverse());
    }
}
