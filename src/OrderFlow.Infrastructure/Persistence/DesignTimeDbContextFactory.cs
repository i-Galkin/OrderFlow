using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderFlow.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef migrations add</c>. The connection string is only needed to
/// build the model, not to reach a live server.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrderFlowDbContext>
{
    public OrderFlowDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ORDERFLOW_CONNECTION")
                               ?? "Host=localhost;Port=5432;Database=orderflow;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<OrderFlowDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new OrderFlowDbContext(options);
    }
}
