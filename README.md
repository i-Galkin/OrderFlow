# OrderFlow

Backend for e-commerce order processing.

Solution layout:

| Project | Purpose |
| --- | --- |
| `src/OrderFlow.Api` | REST API (orders, products, health) |
| `src/OrderFlow.Worker` | Kafka consumer / background processing |
| `src/OrderFlow.Contracts` | Shared events and DTO contracts |
| `src/OrderFlow.Infrastructure` | EF Core, Redis, Kafka, domain services |
| `tests/OrderFlow.Tests` | Unit, integration and concurrency tests |

## Build

```bash
dotnet restore
dotnet build
```

More documentation to follow.
