using Microsoft.EntityFrameworkCore;
using OrderFlow.Infrastructure.Domain;

namespace OrderFlow.Infrastructure.Persistence;

public class OrderFlowDbContext : DbContext
{
    public OrderFlowDbContext(DbContextOptions<OrderFlowDbContext> options) : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers");
            b.HasKey(c => c.Id);
            b.Property(c => c.Email).HasMaxLength(256).IsRequired();
            b.Property(c => c.Name).HasMaxLength(200).IsRequired();
            b.Property(c => c.CreatedAt).HasColumnType("timestamp with time zone");
            b.HasIndex(c => c.Email).IsUnique().HasDatabaseName("ix_customers_email");
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.HasKey(p => p.Id);
            b.Property(p => p.Sku).HasMaxLength(64).IsRequired();
            b.Property(p => p.Name).HasMaxLength(300).IsRequired();
            b.Property(p => p.Price).HasColumnType("numeric(12,2)");
            b.Property(p => p.StockQuantity).IsRequired();
            b.Property(p => p.Version).IsConcurrencyToken();
            b.HasIndex(p => p.Sku).IsUnique().HasDatabaseName("ix_products_sku");
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(o => o.Id);
            b.Property(o => o.Status).HasConversion<int>().IsRequired();
            b.Property(o => o.TotalAmount).HasColumnType("numeric(12,2)");
            b.Property(o => o.FailureReason).HasMaxLength(500);
            b.Property(o => o.CreatedAt).HasColumnType("timestamp with time zone");
            b.Property(o => o.UpdatedAt).HasColumnType("timestamp with time zone");
            b.Property(o => o.Version).IsConcurrencyToken();

            b.HasOne(o => o.Customer)
                .WithMany(c => c.Orders)
                .HasForeignKey(o => o.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(o => o.Items)
                .WithOne()
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(o => o.CustomerId).HasDatabaseName("ix_orders_customer_id");
            b.HasIndex(o => o.Status).HasDatabaseName("ix_orders_status");
            b.HasIndex(o => o.CreatedAt).HasDatabaseName("ix_orders_created_at");
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.ToTable("order_items");
            b.HasKey(i => i.Id);
            b.Property(i => i.Quantity).IsRequired();
            b.Property(i => i.UnitPrice).HasColumnType("numeric(12,2)");
            b.Ignore(i => i.LineTotal);

            b.HasOne(i => i.Product)
                .WithMany()
                .HasForeignKey(i => i.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(i => i.OrderId).HasDatabaseName("ix_order_items_order_id");
        });

        base.OnModelCreating(modelBuilder);
    }
}
