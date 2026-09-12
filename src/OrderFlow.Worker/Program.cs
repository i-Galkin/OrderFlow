using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;
using OrderFlow.Infrastructure.Processing;
using OrderFlow.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.Services.AddOrderFlowPersistence(builder.Configuration);
builder.Services.AddOrderFlowMessaging(builder.Configuration);
builder.Services.AddOrderFlowCaching(builder.Configuration);
builder.Services.AddOrderFlowProcessing();

builder.Services.AddHostedService<OrderEventConsumer>();

var host = builder.Build();
host.Run();
