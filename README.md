# OrderFlow

OrderFlow is the order processing backend: it accepts orders over HTTP, publishes the order
lifecycle to Kafka and lets a worker reserve stock and take payment asynchronously.

```
client -> OrderFlow.Api -> Postgres
                        -> Redis (product read cache)
                        -> Kafka (orders.events) -> OrderFlow.Worker -> Postgres
```

| Project | Purpose |
| --- | --- |
| `src/OrderFlow.Api` | REST API for orders and products, health endpoints |
| `src/OrderFlow.Worker` | Kafka consumer, inventory reservation, payment, retries |
| `src/OrderFlow.Contracts` | DTOs and the event envelope shared by both applications |
| `src/OrderFlow.Infrastructure` | Domain model, EF Core, Redis, Kafka, processing services |
| `tests/OrderFlow.Tests` | Unit, integration and concurrency tests |

See [docs/architecture.md](docs/architecture.md) for the full picture, [docs/issues](docs/issues)
for the current backlog and [docs/incidents](docs/incidents) for past incident write-ups.

## Requirements

* .NET 10 SDK
* Docker (Postgres, Redis, Kafka)
* `dotnet-ef` if you want to add migrations: `dotnet tool install --global dotnet-ef`

## Running the stack

```bash
docker compose up -d --build
curl http://localhost:8080/health/ready
```

The API listens on <http://localhost:8080> and applies pending migrations on startup
(`OrderFlow__ApplyMigrationsOnStartup=true` in the compose file). The worker starts consuming
`orders.events` as soon as the topic exists; Kafka is configured to create it on first publish.

Only the dependencies, for local `dotnet run`:

```bash
docker compose up -d postgres redis kafka
dotnet run --project src/OrderFlow.Api
dotnet run --project src/OrderFlow.Worker
```

## Observability

The compose file also starts Prometheus (<http://localhost:9090>), Loki, Grafana Alloy and Grafana
(<http://localhost:3000>, `admin`/`admin`). Grafana has provisioned dashboards (Overview, Worker &
Kafka, Logs), and Prometheus loads alert rules. The API serves metrics on `/metrics`; the worker
serves them on port `9464`. See [docs/observability.md](docs/observability.md) for the metric
catalogue, log queries and alert runbooks.

## Seed data

```bash
dotnet run --project src/OrderFlow.Api -- --seed
```

Creates 100 customers, 10,000 products and 50,000 orders. Everything is derived from the row
index, so the ids, prices and timestamps are identical on every machine. The generator is a
no-op if the database already has customers.

Fixture SKUs for manual testing:

| SKU | Price | Behaviour |
| --- | --- | --- |
| `SKU-DECLINE-13` | 100.13 | payment gateway declines the card permanently |
| `SKU-TIMEOUT-77` | 100.77 | payment gateway times out (transient) |
| `SKU-SCARCE-01` | 49.50 | only 5 units in stock |
| `SKU-HIGHVALUE-01` | 12,500.00 | over the gateway's auto-approval limit |

The payment stub decides from the order total: amounts ending in `.13` are declined, amounts
ending in `.77` time out, anything over 10,000 goes to manual review, everything else is
authorised.

## API

All endpoints accept and echo an `X-Correlation-ID` header; one is generated when the caller
does not send it.

```bash
# create an order
curl -X POST http://localhost:8080/api/orders \
  -H 'Content-Type: application/json' \
  -H 'X-Correlation-ID: demo-1' \
  -d '{"customerId":"<guid>","items":[{"productId":"<guid>","quantity":2}]}'

# read one, list, filter
curl http://localhost:8080/api/orders/<id>
curl "http://localhost:8080/api/orders?page=1&pageSize=25"
curl "http://localhost:8080/api/orders?status=Failed"

# lifecycle
curl -X POST http://localhost:8080/api/orders/<id>/confirm
curl -X POST http://localhost:8080/api/orders/<id>/cancel
curl -X POST http://localhost:8080/api/orders/<id>/retry

# products
curl http://localhost:8080/api/products/<id>
curl -X POST http://localhost:8080/api/products \
  -H 'Content-Type: application/json' \
  -d '{"sku":"SKU-NEW-01","name":"Widget","price":19.99,"stockQuantity":100}'
```

| Endpoint | Notes |
| --- | --- |
| `GET /health` | every check |
| `GET /health/live` | liveness, polled by the container runtime |
| `GET /health/ready` | readiness: Postgres, Redis, Kafka |

Order status transitions: `Pending -> Confirmed -> Processing -> Completed`, with
`Pending -> Cancelled`, `Processing -> Failed` and `Failed -> Processing` for retries. Anything
else is rejected with `409 Conflict`.

## Tests

```bash
dotnet test
```

Unit tests always run. The integration and concurrency tests need Postgres and Redis; they are
skipped when those are not reachable. Point them somewhere else with:

```bash
export ORDERFLOW_TEST_POSTGRES="Host=localhost;Port=5432;Database=orderflow_test;Username=orderflow;Password=orderflow"
export ORDERFLOW_TEST_REDIS="localhost:6379"
```

## Migrations

```bash
dotnet ef migrations add <Name> -p src/OrderFlow.Infrastructure -s src/OrderFlow.Infrastructure -o Persistence/Migrations
dotnet ef database update -p src/OrderFlow.Infrastructure -s src/OrderFlow.Infrastructure
```

## Configuration

Every setting can be overridden with environment variables using the usual `__` separator.

| Key | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Postgres` | localhost | Postgres connection string |
| `Redis__Configuration` | `localhost:6379` | Redis endpoint |
| `Redis__ProductTtlSeconds` | `600` | product cache TTL |
| `Kafka__BootstrapServers` | `localhost:9092` | broker list |
| `Kafka__Topic` | `orders.events` | lifecycle topic |
| `Kafka__DeadLetterTopic` | `orders.failed` | dead letter topic |
| `Kafka__MaxRetryAttempts` | `3` | retries before dead lettering |
| `Kafka__RetryBaseDelaySeconds` | `2` | base delay for the retry backoff |
| `OrderFlow__ApplyMigrationsOnStartup` | `false` | run migrations when the API starts |
| `Observability__Enabled` | `true` | OpenTelemetry metrics and the `/metrics` endpoint |
| `Observability__MetricsHost` / `Observability__MetricsPort` | `localhost` / `9464` | worker metrics listener |
| `Observability__ConsumerLagPollSeconds` | `15` | worker consumer lag sampling, `0` disables |
