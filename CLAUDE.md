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

Configuration lives in each app's `appsettings.json` (`ConnectionStrings:Postgres`, `Redis:*`,
`Kafka:*`, `Observability:*`) and can be overridden with `__` environment variables.

## Testing notes

* One project, `tests/OrderFlow.Tests`, split into `Unit/`, `Integration/` and `Support/`. xUnit +
  FluentAssertions, no mocking library (hand-written doubles in `Support/Fakes.cs`), test names are
  sentences (`Two_writers_confirming_the_same_order_cannot_both_win`).
* Integration and concurrency tests need Postgres **and** Redis. Without them they silently
  **skip** (`PostgresFactAttribute` / `PostgresTheoryAttribute` / `PostgresRedisFactAttribute` in
  `tests/OrderFlow.Tests/Support/Infrastructure.cs`), so a green `dotnet test` on a machine with no
  docker running proves much less than it looks. Start `docker compose up -d postgres redis` before
  trusting a run. CI (`.github/workflows/ci.yml`) runs them against real service containers.
* `PostgresFixture` (shared through `[Collection(PostgresCollection.Name)]`) creates and migrates the
  `orderflow_test` database once per run. Override the targets with `ORDERFLOW_TEST_POSTGRES` /
  `ORDERFLOW_TEST_REDIS` (CI also sets `ORDERFLOW_TEST_KAFKA`, which no test reads).
* `OrderFlowApiFactory` boots the real API in the `Testing` environment against that database but
  swaps `IOrderEventPublisher` for `RecordingEventPublisher` and `IProductCache` for
  `InMemoryProductCache`, so HTTP tests need no broker and never touch the Redis cache path. It
  depends on `public partial class Program;` at the bottom of `Program.cs`.
* Stop any locally running `OrderFlow.Api` before building: the file lock breaks the build.

## Architecture

Two deployables over one Postgres database, sharing all domain code:

* **OrderFlow.Api** owns the synchronous side: validation, writing orders and products, and the
  confirm, cancel and retry operations.
* **OrderFlow.Worker** owns everything after confirmation: stock reservation, payment, and the
  `Processing -> Completed/Failed` transitions.
* **OrderFlow.Infrastructure** holds the domain model, EF Core, Redis, Kafka and the processing
  services. **OrderFlow.Contracts** holds DTOs (`sealed record`) plus the event envelope. Controllers
  never expose EF entities; `OrderMapping` (orders and products) is the only entity→DTO path.

`docs/architecture.md` has the diagrams, domain, index and worker pipeline descriptions;
`docs/incidents/` has past incident write-ups.

Code conventions: `[ApiController]` controllers (not minimal APIs) inject the concrete `OrderService`
/ `ProductService` registered in `ServiceRegistration`. There is no repository layer and services
have no interfaces; only infrastructure seams are abstracted (`IOrderEventPublisher`,
`IProductCache`, `IInventoryService`, `IPaymentService`). File-scoped namespaces, implicit usings,
nullable enabled, `CancellationToken` on every async call. Each area registers itself through an
`Add*` extension (`PersistenceRegistration`, `CachingRegistration`, `MessagingRegistration`, ...).

### HTTP surface

* `POST /api/orders`, `GET /api/orders/{id}`, `GET /api/orders?page=&pageSize=&status=` (paged,
  `pageSize` max 200), `POST /api/orders/{id}/confirm | cancel | retry`
* `GET /api/products/{id}`, `POST /api/products`
* `GET /health` (all checks), `/health/live` (checks tagged `live`), `/health/ready` (postgres,
  redis, kafka). Checks are registered in `Infrastructure/Health/HealthRegistration`.
* Every endpoint accepts and echoes `X-Correlation-ID`.

### Domain and state machine

Entities (`Infrastructure/Domain`): `Customer`, `Product` (unique `Sku`, `StockQuantity`, `Version`),
`Order` (`Status`, `TotalAmount`, `FailureReason`, `Version`), `OrderItem`, `ProcessedEvent`,
`InventoryReservation` (one row per order line).

Statuses: `Pending`, `Confirmed`, `Processing`, `Completed`, `Failed`, `Cancelled`.
`OrderStatusTransitions` is the single source of truth for which transitions are legal, and
`Order.Confirm/Cancel/StartProcessing/Complete/Fail` are the way to change `Status`: each one
validates the transition, touches `UpdatedAt` and increments `Version`. New status logic belongs
there, not in a service.

### Events

One topic (`orders.events`, dead letters on `orders.failed`, names in `KafkaTopics`), one envelope
(`Contracts/Events/OrderEvent`), keyed by order id so a given order's events stay ordered on one
partition. `EventType` is a string constant from `OrderEventTypes` (`OrderCreated`, `OrderConfirmed`,
`OrderCancelled` from the API; `OrderProcessingStarted`, `OrderCompleted`, `OrderFailed` from the
worker). `OrderEventProcessor` switches on it and acts on `OrderConfirmed` and `OrderCancelled`. The
producer is fire-and-forget (`Produce` with a delivery callback), so a publish call returning does
not mean the broker has the message.

`Worker/OrderEventConsumer` runs the consume loop and owns retry handling: `DomainException`s
(including `NotFoundException` and `InsufficientStockException`) are permanent and go to
`orders.failed`; other failures are retried by re-publishing the event with an incremented `Attempt`
after a backoff from `Kafka:RetryBaseDelaySeconds`, up to `Kafka:MaxRetryAttempts`.

### Idempotency

Kafka delivery is at-least-once. `OrderEventProcessor` skips events whose id is already in
`processed_events` (PK = event id) and records each event it handles there.
`POST /api/orders/{id}/retry` intentionally publishes a *new* `OrderConfirmed` event id (with
`Attempt = 1`) so an operator-requested retry is not deduplicated away.

### Concurrency and errors

`Order.Version` and `Product.Version` are EF concurrency tokens incremented in the domain methods;
a losing writer gets `DbUpdateConcurrencyException`. `ExceptionHandlingMiddleware` maps that to 409,
`NotFoundException` → 404, `InvalidStatusTransitionException` and `InsufficientStockException` →
409, other `DomainException` → 400.

### Persistence naming

Tables are snake_case (`orders`, `order_items`, `processed_events`, `inventory_reservations`) but
**columns keep their CLR PascalCase names**, so raw SQL has to quote them:
`UPDATE products SET "StockQuantity" = ...`. `Order.Status` is stored as an int.

### Caching

`GET /api/products/{id}` is cache-aside over Redis behind `IProductCache` (`RedisProductCache`),
TTL from `Redis:ProductTtlSeconds` (600 s by default). Always build keys via
`RedisProductCache.CacheKey` (`product:{id:N}`); code that changes a product must drop its key.

### Correlation ids

`CorrelationContext` is an `AsyncLocal` ambient value. `CorrelationIdMiddleware` seeds it from the
`X-Correlation-ID` header (or mints one) and echoes it back; `KafkaOrderEventPublisher` copies it
into the payload and a `correlation-id` message header (`CorrelationContext.KafkaHeaderName`); the
worker opens a correlation + logging scope per message. Anything that logs inside a request or a
message handler picks it up automatically, so do not thread it through signatures. Use the
`CorrelationContext` constants for header names.

### Observability

OpenTelemetry metrics are exported in Prometheus format (API: `/metrics` on :8080; worker: an
HttpListener on :9464). Both apps log JSON to stdout; logs reach Loki through Grafana Alloy tailing
container stdout, so logging code stays as it is. Config, dashboards and alert rules live under
`observability/` (Prometheus :9090, Grafana :3000 `admin`/`admin`, Loki :3100, Alloy :12345);
`docs/observability.md` has the metric catalogue, log queries and alert runbooks.

* Custom instruments belong in the static `Infrastructure/Observability/OrderFlowMetrics` (same
  pattern as `CorrelationContext`), so instrumented services keep their constructors. Record only,
  and only after the operation succeeds: never swallow, never change control flow. Tag values must
  come from a closed set; no ids of any kind.
* Dashboards and alerts query the Prometheus names, not the OTel names: `.` becomes `_`, counters
  gain `_total`, units add a suffix (`orderflow.worker.last_poll` with unit `s` →
  `orderflow_worker_last_poll_seconds`). Renaming an instrument breaks them.
* The dashboard JSON under `observability/grafana/dashboards/` is provisioned read-only; edit it in
  the repo. CI validates `observability/prometheus/alerts.yml` with `promtool`.
* Consumer lag comes from `Worker/KafkaLagMonitor` (a separate admin client that only reads
  offsets). Health gauges update only when `/health*` is polled; do not add an
  `IHealthCheckPublisher`, which would run the Kafka check on a timer.
* The HttpListener exporter rejects `*`/`+` as `Host`; `Worker/Program.cs` works around it with
  `ConfigureHttpListener`. `/metrics` and `/health*` call `DisableHttpMetrics()`.

### Deterministic stubs

`FakePaymentService` decides from the order total: amounts ending `.13` are permanently declined,
`.77` time out transiently, over 10,000 goes to manual review (transient), everything else is
authorised. `FakeInventoryService` checks and decrements stock and writes `inventory_reservations`.
`SeedDataGenerator` derives every id from an MD5 of `orderflow:kind:index`, so seeded data is
identical on every machine, and exposes fixture SKUs (`SKU-DECLINE-13`, `SKU-TIMEOUT-77`,
`SKU-SCARCE-01` with 5 units, `SKU-HIGHVALUE-01`) that hit those branches. Keep both
deterministic, since the tests rely on it.

## Subagents and ownership

`.claude/agents/` defines the team. Writers own separate files so they can run in parallel:

| Agent | Writes | Database |
| --- | --- | --- |
| `arch` | nothing (designs, splits work by owner) | none |
| `backend` | `src/**` except `Infrastructure/Persistence/**`; `observability/**` | none; ORM code only, no raw SQL |
| `dba` | `Infrastructure/Persistence/**` (mapping, migrations), raw SQL | **only agent with real DB access** |
| `tester` | `tests/**` | fixture-managed test database only |
| `debugger` | nothing (diagnoses) | none; asks `dba` for data |
| `reviewer` | nothing (reviews diffs) | none |
| `qa` | nothing (exercises the running stack over HTTP) | none; asks `dba` for data |
| `po` | nothing (business acceptance against the GitHub issues) | none |

When writers run at the same time, give each its own git worktree (concurrent `dotnet build` in one
checkout locks `bin/obj`) and its own test database via `ORDERFLOW_TEST_POSTGRES`.

`.claude/skills/review-loop/` drives the whole team as one automated cycle: review and manual test
in parallel, then sequential fixes, a build-and-rebuild gate, and a business acceptance pass. The
rebuild step is load-bearing — `qa` tests the container image on `:8080`, so a fix in `src/**` is
invisible to it until `podman compose up -d --build api worker` runs.

After a clean loop and a `po` accept, review-loop hands off to `.claude/skills/ship/`: push, PR
against `master` (CI only runs on PRs to master and pushes to master, so a bare branch push runs
nothing), a `review-loop:clean sha=` marker comment on the PR, wait for CI on that SHA, fix and
re-review the delta if it fails, then `gh pr merge --merge --match-head-commit`.
