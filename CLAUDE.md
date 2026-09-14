# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build OrderFlow.sln
dotnet test OrderFlow.sln
dotnet test OrderFlow.sln --filter "FullyQualifiedName~Confirming_twice_is_rejected_with_409"
dotnet test OrderFlow.sln --filter "FullyQualifiedName~OrderFlow.Tests.Unit"
```

Infrastructure and applications:

```bash
docker compose up -d --build            # postgres, redis, kafka, api (:8080), worker
docker compose up -d postgres redis kafka   # deps only, then run the apps from source
dotnet run --project src/OrderFlow.Api
dotnet run --project src/OrderFlow.Worker
dotnet run --project src/OrderFlow.Api -- --seed   # 100 customers, 10k products, 50k orders
```

Migrations (the design-time factory lives in `OrderFlow.Infrastructure`, so it is both `-p` and `-s`):

```bash
dotnet ef migrations add <Name> -p src/OrderFlow.Infrastructure -s src/OrderFlow.Infrastructure -o Persistence/Migrations
dotnet ef database update -p src/OrderFlow.Infrastructure -s src/OrderFlow.Infrastructure
```

The API only applies migrations at startup when `OrderFlow__ApplyMigrationsOnStartup=true` (set in
`docker-compose.yml`); a plain `dotnet run` does not.

## Testing notes

* Integration and concurrency tests need Postgres **and** Redis. Without them they silently
  **skip** (`PostgresFactAttribute` / `PostgresRedisFactAttribute` in
  `tests/OrderFlow.Tests/Support/Infrastructure.cs`), so a green `dotnet test` on a machine with no
  docker running proves much less than it looks. Start `docker compose up -d postgres redis` before
  trusting a run.
* `PostgresFixture` creates and migrates the `orderflow_test` database once per run. Override the
  targets with `ORDERFLOW_TEST_POSTGRES` / `ORDERFLOW_TEST_REDIS`.
* `OrderFlowApiFactory` boots the real API against that database but swaps `IOrderEventPublisher`
  for `RecordingEventPublisher` and `IProductCache` for `InMemoryProductCache`, so HTTP tests need
  no broker. It depends on `public partial class Program;` at the bottom of `Program.cs`.
* Stop any locally running `OrderFlow.Api` before building: the file lock breaks the build.

## Architecture

Two deployables over one Postgres database, sharing all domain code:

* **OrderFlow.Api** owns the synchronous side: validation, writing the order, and the
  `Pending -> Confirmed` and `Pending/Confirmed -> Cancelled` transitions.
* **OrderFlow.Worker** owns everything after confirmation: stock reservation, payment, and the
  `Confirmed/Failed -> Processing -> Completed/Failed` transitions.
* **OrderFlow.Infrastructure** holds the domain model, EF Core, Redis, Kafka and the processing
  services. **OrderFlow.Contracts** holds DTOs plus the event envelope. Controllers never expose EF
  entities; `OrderMapping` is the only entity→DTO path.

`docs/architecture.md` has the diagrams; `docs/issues/` and `docs/incidents/` carry the open reports
and past write-ups.

### State machine

`OrderStatusTransitions` is the single source of truth, and `Order.Confirm/Cancel/StartProcessing/
Complete/Fail` are the only intended way to change `Status` — each one validates the transition,
touches `UpdatedAt` and increments `Version`. New status logic belongs there, not in a service.

### Events

One topic (`orders.events`), one envelope (`Contracts/Events/OrderEvent`), keyed by order id so a
given order's events stay ordered on one partition. `EventType` is a string constant from
`OrderEventTypes`; consumers switch on it. Retries re-publish the event with `Attempt + 1`; once the
budget in `Kafka:MaxRetryAttempts` is gone the event goes to `orders.failed`.
`TransientProcessingException` is the signal for "retry this"; a `DomainException` means permanent.

### Idempotency

Kafka delivery is at-least-once. `OrderEventProcessor` consults and then writes `processed_events`
(PK = event id) around each handled event. `POST /api/orders/{id}/retry` intentionally publishes a
*new* event id so an operator-requested retry is not deduplicated away.

### Concurrency

`Order.Version` and `Product.Version` are EF concurrency tokens incremented in the domain methods;
a losing writer gets `DbUpdateConcurrencyException`, which `ExceptionHandlingMiddleware` maps to
409. That middleware also maps `NotFoundException` → 404, `InvalidStatusTransitionException` and
`InsufficientStockException` → 409, other `DomainException` → 400.

### Persistence naming

Tables are snake_case (`orders`, `order_items`, `processed_events`) but **columns keep their CLR
PascalCase names**, so raw SQL has to quote them: `UPDATE products SET "StockQuantity" = ...`.

### Caching

`GET /api/products/{id}` is cache-aside over Redis behind `IProductCache`, keyed `product:{id:N}`,
TTL from `Redis:ProductTtlSeconds`. Read failures fall through to Postgres by design.

### Correlation ids

`CorrelationContext` is an `AsyncLocal` ambient value. The API middleware seeds it from the
`X-Correlation-ID` header (or mints one) and echoes it back; the Kafka producer copies it onto the
message header and into the payload; the worker opens a scope per message. Anything that logs
inside a request or a message handler picks it up automatically — do not thread it through
signatures.

### Deterministic stubs

`FakePaymentService` decides from the order total: amounts ending `.13` are permanently declined,
`.77` time out transiently, over 10,000 goes to manual review (transient), everything else is
authorised. `SeedDataGenerator` derives every id from an MD5 of `kind:index`, so seeded data is
identical on every machine, and exposes fixture SKUs (`SKU-DECLINE-13`, `SKU-TIMEOUT-77`,
`SKU-SCARCE-01`, `SKU-HIGHVALUE-01`) that hit those branches. Keep both deterministic — tests and
the docs rely on it.
