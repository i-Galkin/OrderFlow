using Microsoft.Extensions.DependencyInjection;

namespace OrderFlow.Infrastructure.Processing;

public static class ProcessingRegistration
{
    public static IServiceCollection AddOrderFlowProcessing(this IServiceCollection services)
    {
        services.AddScoped<IInventoryService, FakeInventoryService>();
        services.AddSingleton<IPaymentService, FakePaymentService>();
        services.AddScoped<OrderEventProcessor>();
        return services;
    }
}
