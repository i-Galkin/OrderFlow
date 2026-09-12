using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OrderFlow.Infrastructure.Messaging;

public static class MessagingRegistration
{
    public static IServiceCollection AddOrderFlowMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.AddSingleton<IOrderEventPublisher, KafkaOrderEventPublisher>();
        return services;
    }
}
