using OrderFlow.Contracts.Dtos;

namespace OrderFlow.Infrastructure.Caching;

public interface IProductCache
{
    Task<ProductDto?> GetAsync(Guid productId, CancellationToken ct = default);

    Task SetAsync(ProductDto product, CancellationToken ct = default);

    Task RemoveAsync(Guid productId, CancellationToken ct = default);
}
