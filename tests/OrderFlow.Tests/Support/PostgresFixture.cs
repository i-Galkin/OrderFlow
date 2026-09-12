using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderFlow.Infrastructure.Domain;
using OrderFlow.Infrastructure.Persistence;
using Xunit;

namespace OrderFlow.Tests.Support;

/// <summary>
/// Creates (once per test run) the orderflow_test database and applies the migrations.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public string ConnectionString => TestInfrastructure.PostgresConnectionString;

    public async Task InitializeAsync()
    {
        if (!TestInfrastructure.PostgresAvailable)
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var databaseName = builder.Database!;

        var adminBuilder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();

            await using var exists = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @name", admin);
            exists.Parameters.AddWithValue("name", databaseName);

            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
                await create.ExecuteNonQueryAsync();
            }
        }

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public OrderFlowDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<OrderFlowDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new OrderFlowDbContext(options);
    }

    /// <summary>Creates a customer that only the calling test uses.</summary>
    public async Task<Customer> CreateCustomerAsync()
    {
        await using var db = CreateContext();

        var customer = new Customer
        {
            Email = $"test-{Guid.NewGuid():N}@orderflow.test",
            Name = "Integration test"
        };

        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    public async Task<Product> CreateProductAsync(decimal price, int stock)
    {
        await using var db = CreateContext();

        var product = new Product
        {
            Sku = $"SKU-{Guid.NewGuid():N}"[..20],
            Name = "Integration test product",
            Price = price,
            StockQuantity = stock,
            Version = 1
        };

        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
