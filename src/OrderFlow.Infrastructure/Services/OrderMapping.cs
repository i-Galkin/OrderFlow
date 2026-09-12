using OrderFlow.Contracts.Dtos;
using OrderFlow.Infrastructure.Domain;

namespace OrderFlow.Infrastructure.Services;

public static class OrderMapping
{
    public static OrderDto ToDto(this Order order)
    {
        return new OrderDto
        {
            Id = order.Id,
            CustomerId = order.CustomerId,
            CustomerEmail = order.Customer?.Email ?? string.Empty,
            Status = order.Status.ToString(),
            TotalAmount = order.TotalAmount,
            FailureReason = order.FailureReason,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            Version = order.Version,
            Items = order.Items.Select(i => new OrderItemDto
            {
                Id = i.Id,
                ProductId = i.ProductId,
                ProductName = i.Product?.Name ?? string.Empty,
                Sku = i.Product?.Sku ?? string.Empty,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                LineTotal = i.LineTotal
            }).ToList()
        };
    }

    public static OrderSummaryDto ToSummaryDto(this Order order)
    {
        return new OrderSummaryDto
        {
            Id = order.Id,
            CustomerId = order.CustomerId,
            Status = order.Status.ToString(),
            TotalAmount = order.TotalAmount,
            ItemCount = order.Items.Count,
            CreatedAt = order.CreatedAt
        };
    }

    public static ProductDto ToDto(this Product product)
    {
        return new ProductDto
        {
            Id = product.Id,
            Sku = product.Sku,
            Name = product.Name,
            Price = product.Price,
            StockQuantity = product.StockQuantity,
            Version = product.Version
        };
    }
}
