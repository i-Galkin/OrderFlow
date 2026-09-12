using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderFlow.Contracts.Dtos;
using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Services;

public class ProductService
{
    private readonly OrderFlowDbContext _db;
    private readonly IProductCache _cache;
    private readonly ILogger<ProductService> _logger;

    public ProductService(OrderFlowDbContext db, IProductCache cache, ILogger<ProductService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ProductDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var cached = await _cache.GetAsync(id, ct);
        if (cached is not null)
        {
            _logger.LogDebug("Product {ProductId} served from cache", id);
            return cached;
        }

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return null;
        }

        var dto = product.ToDto();
        await _cache.SetAsync(dto, ct);
        return dto;
    }

    public async Task<ProductDto> CreateAsync(CreateProductRequest request, CancellationToken ct)
    {
        var exists = await _db.Products.AnyAsync(p => p.Sku == request.Sku, ct);
        if (exists)
        {
            throw new DomainException($"A product with SKU '{request.Sku}' already exists.");
        }

        var product = new Product
        {
            Sku = request.Sku,
            Name = request.Name,
            Price = request.Price,
            StockQuantity = request.StockQuantity,
            Version = 1
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync(ct);

        var dto = product.ToDto();
        await _cache.SetAsync(dto, ct);

        _logger.LogInformation("Created product {ProductId} ({Sku})", product.Id, product.Sku);
        return dto;
    }
}
