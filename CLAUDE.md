# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build OrderFlow.sln
dotnet test OrderFlow.sln
dotnet test OrderFlow.sln --filter "FullyQualifiedName~Confirming_twice_is_rejected_with_409"
dotnet test OrderFlow.sln --filter "FullyQualifiedName~OrderFlow.Tests.Unit"
```

Infrastructure and applications (.NET 10 SDK, Docker):

```bash
docker compose up -d --build            # postgres, redis, kafka, api (:8080), worker + prometheus (:9090), loki, alloy, grafana (:3000)
docker compose up -d postgres redis kafka   # deps only, then run the apps from source
dotnet run --project src/OrderFlow.Api
dotnet run --project src/OrderFlow.Worker
dotnet run --project src/OrderFlow.Api -- --seed   # migrates, then seeds 100 customers, 10k products, 50k orders; no-op if customers exist
```

Migrations (the design-time factory lives in `OrderFlow.Infrastructure`, so it is both `-p` and `-s`):

```bash
dotnet ef migrations add <Name> -p src/OrderFlow.Infrastructure -s src/OrderFlow.Infrastructure -o Persistence/Migrations
dotnet ef database update -p src/OrderFlow.Infrastructure -s src/OrderFlow.Infrastructure
```

The API only applies migrations at startup when `OrderFlow__ApplyMigrationsOnStartup=true` (set in
`docker-compose.yml`); a plain `dotnet run` does not. The OpenAPI/Scalar UI is mapped only in the
`Development` environment.

Packages use lock files (`RestorePackagesWithLockFile` in `Directory.Build.props`), so adding or
bumping a package must update the relevant `packages.lock.json`; CI caches restores on those files.

## Testing notes

* Integration and concurrency tests need Postgres **and** Redis. Without them they silently
  **skip** (`PostgresFactAttribute` / `PostgresTheoryAttribute` / `PostgresRedisFactAttribute` in
  `tests/OrderFlow.Tests/Support/Infrastructure.cs`), so a green `dotnet test` on a machine with no
  docker running proves much less than it looks. Start `docker compose up -d postgres redis` before
  trusting a run. CI (`.github/workflows/ci.yml`) runs them against real service containers.
* `PostgresFixture` creates and migrates the `orderflow_test` database once per run. Override the
  targets with `ORDERFLOW_TEST_POSTGRES` / `ORDERFLOW_TEST_REDIS` (CI also sets
  `ORDERFLOW_TEST_KAFKA`, which no test reads).
* `OrderFlowApiFactory` boots the real API in the `Testing` environment against that database but
  swaps `IOrderEventPublisher` for `RecordingEventPublisher` and `IProductCache` for
  `InMemoryProductCache`, so HTTP tests need no broker and never touch the Redis cache path. It
  depends on `public partial class Program;` at the bottom of `Program.cs`.
* Stop any locally running `OrderFlow.Api` before building: the file lock breaks the build.

## Architecture

Two deployables over one Postgres database, sharing all domain code:

* **OrderFlow.Api** owns the synchronous side: validation, writing the order, and the
  `Pending -> Confirmed` and `Pending/Confirmed -> Cancelled` transitions.
* **OrderFlow.Worker** owns everything after confirmation: stock reservation, payment, and the
  `Confirmed/Failed -> Processing -> Completed/Failed` transitions.
* **OrderFlow.Infrastructure** holds the domain model, EF Core, Redis, Kafka and the processing
  services. **OrderFlow.Contracts** holds DTOs plus the event envelope. Controllers never expose EF
  entities; `OrderMapping` (orders and products) is the only entity→DTO path.

`docs/architecture.md` has the diagrams; `docs/issues/` and `docs/incidents/` carry the open reports
and past write-ups. **`docs/architecture.md` and the README describe the intended design; several
open issues exist precisely because the code diverges from it.** Where this file and the code
disagree below, the divergence is called out — check the code, not the docs, before relying on a
guarantee.

### State machine

`OrderStatusTransitions` is the single source of truth, and `Order.Confirm/Cancel/StartProcessing/
Complete/Fail` are the intended way to change `Status` — each one validates the transition,
touches `UpdatedAt` and increments `Version`. New status logic belongs there, not in a service.
The table itself only allows `Pending -> Cancelled`; `OrderService.CancelAsync` handles
`Confirmed -> Cancelled` by assigning `Status` directly (bypassing validation and `Version`).

### Events

One topic (`orders.events`), one envelope (`Contracts/Events/OrderEvent`), keyed by order id so a
given order's events stay ordered on one partition. `EventType` is a string constant from
`OrderEventTypes`; `OrderEventProcessor` switches on it and only acts on `OrderConfirmed` and
`OrderCancelled`. The producer is fire-and-forget (`Produce` with a delivery callback that only
logs), so a publish call returning does not mean the broker has the message.

Retry handling lives in `Worker/OrderEventConsumer`, not the processor:

* `DomainException` (including `NotFoundException` and `InsufficientStockException`) → straight to
  `orders.failed`.
* **Any other exception** (not only `TransientProcessingException`) → `Task.Delay` backoff inside the
  consume loop, then re-publish as a new event id with an incremented `Attempt`. The attempt count
  comes from an in-memory per-order dictionary, not from the incoming envelope, so it resets on
  restart. Past `Kafka:MaxRetryAttempts` the event goes to `orders.failed`.
* On `Attempt > 0`, if a reservation already exists the processor skips reservation and payment and
  completes the order directly.

The consumer resolves a single DI scope (one `OrderFlowDbContext`) for its whole lifetime and also
keeps an in-memory `_handledEvents` set.

### Idempotency

Kafka delivery is at-least-once. `OrderEventProcessor` checks `processed_events` (PK = event id)
before handling and inserts the row in a separate `SaveChanges` after handling — not atomically
with the handler's own writes. `POST /api/orders/{id}/retry` intentionally publishes a *new*
`OrderConfirmed` event id (with `Attempt = 1`) so an operator-requested retry is not deduplicated
away.

### Concurrency

`Order.Version` and `Product.Version` are EF concurrency tokens incremented in the domain methods;
a losing writer gets `DbUpdateConcurrencyException`, which `ExceptionHandlingMiddleware` maps to
409. That middleware also maps `NotFoundException` → 404, `InvalidStatusTransitionException` and
`InsufficientStockException` → 409, other `DomainException` → 400. `FakeInventoryService` does not
go through the tokens: it checks stock on an untracked read and then decrements with raw SQL.

### Persistence naming

Tables are snake_case (`orders`, `order_items`, `processed_events`, `inventory_reservations`) but
**columns keep their CLR PascalCase names**, so raw SQL has to quote them:
`UPDATE products SET "StockQuantity" = ...`. `Order.Status` is stored as an int.

### Caching

`GET /api/products/{id}` is cache-aside over Redis behind `IProductCache`
(`RedisProductCache.CacheKey` → `product:{id:N}`), TTL from `Redis:ProductTtlSeconds`. Only
`RedisConnectionException` on read falls through to Postgres; `SetAsync` is unguarded. Always build
keys via `RedisProductCache.CacheKey` — `FakeInventoryService` hand-formats `product:{id}` (dashed
GUID) when invalidating, which does not match.

### Correlation ids

`CorrelationContext` is an `AsyncLocal` ambient value. `CorrelationIdMiddleware` seeds it from the
`X-Correlation-ID` header (or mints one) and echoes it back; `KafkaOrderEventPublisher` copies it
into the payload and a `correlation-id` message header (`CorrelationContext.KafkaHeaderName`); the
worker opens a correlation + logging scope per message. Anything that logs inside a request or a
message handler picks it up automatically — do not thread it through signatures. Use the
`CorrelationContext` constants for header names: the consumer currently reads a hard-coded
`correlationId`, which does not match the producer.

### Observability

OpenTelemetry metrics are exported in Prometheus format (API: `/metrics` on :8080; worker: an
HttpListener on :9464). Logs reach Loki through Grafana Alloy tailing container stdout, so logging
code stays as it is. Config, dashboards and alert rules live under `observability/`;
`docs/observability.md` has the metric catalogue and runbooks.

* Custom instruments belong in the static `Infrastructure/Observability/OrderFlowMetrics` (same
  pattern as `CorrelationContext`), so instrumented services keep their constructors. Record only,
  and only after the operation succeeds: never swallow, never change control flow. Tag values must
  come from a closed set; no ids of any kind.
* Dashboards and alerts query the Prometheus names, not the OTel names: `.` becomes `_`, counters
  gain `_total`, units add a suffix (`orderflow.worker.last_poll` with unit `s` →
  `orderflow_worker_last_poll_seconds`). Renaming an instrument breaks them.
* The dashboard JSON under `observability/grafana/dashboards/` is provisioned read-only; edit it in
  the repo.
* Consumer lag comes from `Worker/KafkaLagMonitor` (a separate admin client, cluster-wide
  metadata only, so it never triggers topic auto-creation). Health gauges update only when
  `/health*` is polled; do not add an `IHealthCheckPublisher`, which would run the Kafka check on a
  timer.
* The HttpListener exporter rejects `*`/`+` as `Host`; `Worker/Program.cs` works around it with
  `ConfigureHttpListener`. `/metrics` and `/health*` call `DisableHttpMetrics()`.

### Deterministic stubs

`FakePaymentService` decides from the order total: amounts ending `.13` are permanently declined,
`.77` time out transiently, over 10,000 goes to manual review (transient), everything else is
authorised. `SeedDataGenerator` derives every id from an MD5 of `orderflow:kind:index`, so seeded
data is identical on every machine, and exposes fixture SKUs (`SKU-DECLINE-13`, `SKU-TIMEOUT-77`,
`SKU-SCARCE-01` with 5 units, `SKU-HIGHVALUE-01`) that hit those branches. Keep both
deterministic — tests and the docs rely on it.
