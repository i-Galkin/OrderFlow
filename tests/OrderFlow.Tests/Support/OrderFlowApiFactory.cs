using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Tests.Support;

/// <summary>
/// Boots the real API against the test database, with Kafka and Redis replaced by
/// in-process doubles so the HTTP surface can be exercised without a broker.
/// </summary>
public sealed class OrderFlowApiFactory : WebApplicationFactory<Program>
{
    public RecordingEventPublisher Publisher { get; } = new();

    public InMemoryProductCache Cache { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", TestInfrastructure.PostgresConnectionString);

        builder.ConfigureServices(services =>
        {
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<OrderFlowDbContext>)
                            || d.ServiceType == typeof(DbContextOptions)
                            || d.ServiceType == typeof(OrderFlowDbContext))
                .ToList();

            foreach (var descriptor in dbDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<OrderFlowDbContext>(options =>
                options.UseNpgsql(TestInfrastructure.PostgresConnectionString));

            services.RemoveAll<IOrderEventPublisher>();
            services.AddSingleton<IOrderEventPublisher>(Publisher);

            services.RemoveAll<IProductCache>();
            services.AddSingleton<IProductCache>(Cache);
        });
    }
}
