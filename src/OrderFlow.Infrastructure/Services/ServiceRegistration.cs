using Microsoft.Extensions.DependencyInjection;

namespace OrderFlow.Infrastructure.Services;

public static class ServiceRegistration
{
    public static IServiceCollection AddOrderFlowServices(this IServiceCollection services)
    {
        services.AddScoped<OrderService>();
        services.AddScoped<ProductService>();
        return services;
    }
}
