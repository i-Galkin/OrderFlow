using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderFlow.Contracts;
using OrderFlow.Contracts.Events;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Processing;

/// <summary>
/// Applies a consumed order event to the database. The worker owns everything that happens
/// after an order is confirmed: reserving stock, charging the customer and driving the
/// order through Processing -> Completed/Failed.
/// </summary>
public sealed class OrderEventProcessor
{
    private readonly OrderFlowDbContext _db;
    private readonly IInventoryService _inventory;
    private readonly IPaymentService _payments;
    private readonly IOrderEventPublisher _publisher;
    private readonly ILogger<OrderEventProcessor> _logger;

    public OrderEventProcessor(
        OrderFlowDbContext db,
        IInventoryService inventory,
        IPaymentService payments,
        IOrderEventPublisher publisher,
        ILogger<OrderEventProcessor> logger)
    {
        _db = db;
        _inventory = inventory;
        _payments = payments;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task ProcessAsync(OrderEvent orderEvent, CancellationToken ct)
    {
        var alreadyProcessed = await _db.ProcessedEvents
            .AsNoTracking()
            .AnyAsync(e => e.EventId == orderEvent.EventId, ct);

        if (alreadyProcessed)
        {
            _logger.LogInformation(
                "Skipping duplicate delivery of event {EventId} for order {OrderId}",
                orderEvent.EventId, orderEvent.OrderId);
            return;
        }

        switch (orderEvent.EventType)
        {
            case OrderEventTypes.OrderConfirmed:
                await HandleConfirmedAsync(orderEvent, ct);
                break;

            case OrderEventTypes.OrderCancelled:
                await HandleCancelledAsync(orderEvent, ct);
                break;

            default:
                _logger.LogDebug(
                    "Event type {EventType} needs no worker action (order {OrderId})",
                    orderEvent.EventType, orderEvent.OrderId);
                break;
        }

        await MarkProcessedAsync(orderEvent, ct);
    }

    private async Task HandleConfirmedAsync(OrderEvent orderEvent, CancellationToken ct)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderEvent.OrderId, ct)
            ?? throw new NotFoundException("Order", orderEvent.OrderId);

        if (order.Status is OrderStatus.Completed or OrderStatus.Cancelled)
        {
            _logger.LogInformation(
                "Order {OrderId} is already {Status}; nothing to process",
                order.Id, order.Status);
            return;
        }

        if (orderEvent.Attempt > 0 && await _inventory.HasReservationAsync(order.Id, ct))
        {
            // Stock was already taken during the first attempt. Resume the pipeline from
            // where it stopped instead of reserving the same units twice.
            _logger.LogInformation(
                "Resuming order {OrderId} on attempt {Attempt} with an existing reservation",
                order.Id, orderEvent.Attempt);

            if (order.Status == OrderStatus.Failed)
            {
                order.StartProcessing();
            }

            order.Complete();
            await _db.SaveChangesAsync(ct);
            await _publisher.PublishAsync(OrderEventFactory.From(order, OrderEventTypes.OrderCompleted), ct);
            return;
        }

        await _inventory.ReserveAsync(orderEvent, ct);

        order.StartProcessing();
        await _db.SaveChangesAsync(ct);
        await _publisher.PublishAsync(OrderEventFactory.From(order, OrderEventTypes.OrderProcessingStarted), ct);

        var payment = await _payments.ChargeAsync(order.Id, order.CustomerId, order.TotalAmount, ct);

        switch (payment.Outcome)
        {
            case PaymentOutcome.Success:
                order.Complete();
                await _db.SaveChangesAsync(ct);
                await _publisher.PublishAsync(OrderEventFactory.From(order, OrderEventTypes.OrderCompleted), ct);
                _logger.LogInformation(
                    "Order {OrderId} completed with authorization {AuthorizationCode}",
                    order.Id, payment.AuthorizationCode);
                break;

            case PaymentOutcome.TransientFailure:
                throw new TransientProcessingException(payment.Reason ?? "payment_transient_failure");

            default:
                order.Fail(payment.Reason ?? "payment_declined");
                await _db.SaveChangesAsync(ct);
                await _publisher.PublishAsync(
                    OrderEventFactory.From(order, OrderEventTypes.OrderFailed, orderEvent.Attempt, payment.Reason),
                    ct);
                _logger.LogInformation(
                    "Payment for order {OrderId} was declined: {Reason}",
                    order.Id, payment.Reason);
                break;
        }
    }

    private async Task HandleCancelledAsync(OrderEvent orderEvent, CancellationToken ct)
    {
        if (await _inventory.HasReservationAsync(orderEvent.OrderId, ct))
        {
            await _inventory.ReleaseAsync(orderEvent.OrderId, ct);
        }

        _logger.LogInformation("Handled cancellation for order {OrderId}", orderEvent.OrderId);
    }

    private async Task MarkProcessedAsync(OrderEvent orderEvent, CancellationToken ct)
    {
        _db.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = orderEvent.EventId,
            EventType = orderEvent.EventType,
            OrderId = orderEvent.OrderId,
            ProcessedAt = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The primary key on processed_events is the event id, so a clash simply means
            // another delivery of the same event beat us to it.
            _db.ChangeTracker.Clear();
            _logger.LogDebug("Event {EventId} was already recorded as processed", orderEvent.EventId);
        }
    }
}
