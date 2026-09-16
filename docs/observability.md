# Observability

Metrics go to Prometheus, logs go to Loki, and Grafana shows both. Everything is provisioned
from `observability/` and started by `docker compose`. The apps do not depend on the monitoring
containers: stop them and OrderFlow behaves exactly the same.

| Component | URL | Notes |
| --- | --- | --- |
| Grafana | <http://localhost:3000> | `admin` / `admin` unless `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` are set |
| Prometheus | <http://localhost:9090> | `/targets`, `/alerts`, `/rules` |
| Loki | <http://localhost:3100> | query through Grafana |
| Alloy | <http://localhost:12345> | pipeline graph and component health |
| API metrics | <http://localhost:8080/metrics> | same port as the API |
| Worker metrics | `worker:9464/metrics` | compose network only |

```bash
docker compose up -d --build                         # apps + monitoring
docker compose up -d prometheus loki alloy grafana   # monitoring only, e.g. next to apps run from source
```

The provisioned Prometheus config scrapes `api` and `worker` by their compose service names. When
the apps run from source, their metrics are still at `localhost:8080/metrics` and
`localhost:9464/metrics`, but the compose Prometheus will not reach them. Alloy only ships logs from
containers.

## How it is wired

* **Metrics.** Both hosts register the OpenTelemetry SDK through
  `ObservabilityRegistration.AddOrderFlowObservability`. Shared meters: `OrderFlow` (custom
  instruments in `OrderFlowMetrics`), `System.Runtime`, `Npgsql` and `Microsoft.EntityFrameworkCore`.
  The API also listens to the ASP.NET Core, Kestrel and `System.Net.Http` meters and serves
  `/metrics` through `OpenTelemetry.Exporter.Prometheus.AspNetCore`. The worker has no web host, so
  it uses the stand-alone `OpenTelemetry.Exporter.Prometheus.HttpListener`.
* **Consumer lag.** `KafkaLagMonitor` is a background service in the worker. On its own admin
  client it compares the group's committed offsets with each partition's end offset. It does not use
  the consume loop, so lag stays accurate while that loop is blocked. It only reads offsets and
  never joins the consumer group.
* **Logs.** No logging code changed: the apps still write `JsonConsole` lines to stdout. Alloy
  discovers the compose containers through the Docker socket and parses the .NET JSON lines. It adds
  `service_name` and `level` as Loki labels, and `correlation_id`, `order_id`, `event_id` and
  `category` as structured metadata.
* **Health.** `/health*` responses also update `orderflow_health_check_status`. Checks are **not**
  run on a timer (a timer would open extra Kafka admin clients), so the gauge shows whatever the last
  probe saw. In compose, the api healthcheck polls `/health/ready` (postgres, redis, kafka) every
  10s purely to keep the gauges fresh; container health is still decided by `/health/live` alone.
  Outside compose, something else must poll `/health/ready` on an interval or the gauge goes stale.
* `/metrics` and `/health*` are excluded from `http.server.*` metrics so scrapes and probes do not
  count as API traffic.

The Prometheus exporter packages are still prerelease (`1.18.0-beta.1`). Pin their versions and
review release notes before upgrading.

### Configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `Observability__Enabled` | `true` | register the meter provider and the scrape endpoint |
| `Observability__MetricsHost` | `localhost` | worker: listener host; `*` or `+` binds every interface (compose uses `*`) |
| `Observability__MetricsPort` | `9464` | worker: listener port |
| `Observability__ConsumerLagPollSeconds` | `15` | worker: lag sampling interval, `0` disables |

## Metric catalogue

Names below are as Prometheus shows them. The exporter turns `.` into `_` and adds `_total` to
counters and a unit suffix such as `_seconds`. Histograms also produce `_bucket`, `_sum` and
`_count` series.

### Custom (`OrderFlow` meter)

| Metric | Type | Labels | Recorded |
| --- | --- | --- | --- |
| `orderflow_orders_created_total` | counter | | order saved by `OrderService.CreateAsync` |
| `orderflow_orders_value` | histogram | | total amount of created orders |
| `orderflow_orders_status_changes_total` | counter | `status`, `source` (api/worker) | after the status change is saved |
| `orderflow_orders_manual_retries_total` | counter | | `POST /api/orders/{id}/retry` |
| `orderflow_api_errors_total` | counter | `type`, `status_code` | exception mapped by `ExceptionHandlingMiddleware` |
| `orderflow_kafka_produced_total` | counter | `topic`, `event_type` | message handed to the producer queue |
| `orderflow_kafka_delivery_total` | counter | `topic`, `result` (delivered/error) | broker delivery report |
| `orderflow_kafka_delivery_duration_seconds` | histogram | `topic` | from Produce to delivery report |
| `orderflow_kafka_dead_lettered_total` | counter | `topic`, `event_type` | event routed to `orders.failed` |
| `orderflow_kafka_client_errors_total` | counter | `client`, `fatal` | librdkafka error handler (producer/consumer/lag_monitor) |
| `orderflow_kafka_consumer_lag` | gauge | `topic`, `partition` | `KafkaLagMonitor` sample |
| `orderflow_kafka_consumer_lag_last_sample_seconds` | gauge | | Unix time of last successful lag sample |
| `orderflow_kafka_consumer_lag_sample_failures_total` | counter | | failed lag sample |
| `orderflow_worker_messages_total` | counter | `event_type`, `outcome` | each consumed message |
| `orderflow_worker_message_duration_seconds` | histogram | `event_type`, `outcome` | handling time, including retry backoff |
| `orderflow_worker_retries_total` | counter | `attempt`, `result` (scheduled/exhausted) | retry decision |
| `orderflow_worker_retry_backoff_duration_seconds` | histogram | | time spent in the backoff `Task.Delay` |
| `orderflow_worker_consume_errors_total` | counter | | `ConsumeException` |
| `orderflow_worker_correlation_id_generated_total` | counter | | message whose correlation header the consumer did not recognise |
| `orderflow_worker_last_poll_seconds` | gauge | | Unix time the consume loop last called `Consume` |
| `orderflow_processing_duplicates_skipped_total` | counter | `event_type` | event already in `processed_events` |
| `orderflow_processing_resumed_with_reservation_total` | counter | | retry completed from an existing reservation |
| `orderflow_processing_mark_processed_conflicts_total` | counter | | `processed_events` insert hit an existing row |
| `orderflow_payments_total` | counter | `outcome`, `reason` | `FakePaymentService.ChargeAsync` |
| `orderflow_inventory_reservations_total` | counter | `result` (reserved/insufficient_stock/product_not_found) | `FakeInventoryService.ReserveAsync` |
| `orderflow_inventory_releases_total` | counter | | reservations released for an order |
| `orderflow_cache_requests_total` | counter | `cache`, `result` (hit/miss/unavailable) | `RedisProductCache.GetAsync` |
| `orderflow_cache_write_errors_total` | counter | `cache` | `RedisProductCache.SetAsync` threw (the exception still propagates) |
| `orderflow_health_check_status` | gauge | `check` | last polled health result: 1 / 0.5 / 0 |

`outcome` on worker messages is one of `processed`, `duplicate_in_memory`, `dead_lettered`,
`retry_scheduled`, `unparseable`, `error`. Event types outside `OrderEventTypes` are reported as
`other`.

### Built in

* `http_server_request_duration_seconds{http_route, http_request_method, http_response_status_code}`,
  `http_server_active_requests`, `kestrel_*` (API)
* `dotnet_process_cpu_time_seconds_total`, `dotnet_process_memory_working_set_bytes`, `dotnet_gc_*`,
  `dotnet_thread_pool_*`, `dotnet_exceptions_total` (both)
* Npgsql connection pool and command metrics, EF Core metrics (both)
* `target_info` carries the resource attributes: `service_name`, `service_instance_id`,
  `deployment_environment_name`

### Label rules

Every label value must come from a closed set. Never use order ids, event ids, correlation ids,
customer ids or exception messages as labels: each distinct value becomes a new series that
Prometheus keeps. Those ids belong in logs, where Loki stores them as structured metadata.

## Dashboards

Grafana → *Dashboards → OrderFlow*. They are provisioned read-only from
`observability/grafana/dashboards/`; to change one, edit the JSON (or export it from a copy) and
commit it.

| Dashboard | Use it for |
| --- | --- |
| **OrderFlow / Overview** | API traffic, errors and latency by route; problem responses by exception; readiness; order throughput and status changes; CPU, memory, GC and thread pool for both services |
| **OrderFlow / Worker & Kafka** | consumer lag per partition, time since the consume loop last polled, outcomes, retries and backoff, dead letters, producer delivery, payments, reservations, cache hit ratio, Npgsql pool |
| **OrderFlow / Logs** | log volume by level, warnings and errors, full log stream filtered by service, level, correlation id, order id or text |

## Logs

Labels (indexed): `service_name`, `level`, `container`, `compose_project`.
Structured metadata (filterable, not indexed): `correlation_id`, `order_id`, `event_id`, `category`.

```logql
# one request end to end on the API side
{service_name="api"} | correlation_id="demo-1"

# everything about one order, both services
{service_name=~"api|worker"} | order_id="3f7c..." 

# errors with the parsed JSON fields available for further filtering
{service_name=~"api|worker", level=~"error|critical"} | json

# retries scheduled per minute
sum(count_over_time({service_name="worker"} |= "scheduling retry" [1m]))
```

`order_id` and `event_id` are taken from the log message's own properties (`State.OrderId`,
`State.EventId`), so they are only present on lines that log them.

## Alerts

Rules live in `observability/prometheus/alerts.yml` and can be seen in Prometheus (`/alerts`) and
Grafana. No Alertmanager is deployed, so nothing is sent anywhere. To route notifications, add an
`alertmanager` service and an `alerting:` block to `prometheus.yml`. CI checks the rules with
`promtool`.

### OrderFlowTargetDown

Prometheus cannot scrape the API or worker. Run `docker compose ps` and read the container logs. For
the worker, check that `Observability__MetricsHost` binds all interfaces in the container.

### ApiNotReady

A readiness dependency is failing. The `check` label names it. Open `/health` for the description.
The value only updates when a probe calls a health endpoint; in compose that's the api healthcheck
polling `/health/ready` every 10s, so the gauge is fresh to within that interval.

### ApiHigh5xxRate

More than 5% of API responses are 5xx. Filter the Logs dashboard to `service_name=api`, level
`error`: unhandled exceptions are logged by `ExceptionHandlingMiddleware`.

### ApiHighLatencyP95

p95 above 1s on a route. Compare with the Npgsql pool panel: pool exhaustion and large list queries
are the usual causes.

### ApiConcurrencyConflictsHigh

Sustained `DbUpdateConcurrencyException` 409s. Clients are racing on the same orders; check which
routes (`orderflow_api_errors_total` against request rate).

### KafkaConsumerLagHigh

The worker is behind. Check *Seconds since last poll* first. If it is climbing, the consume loop is
blocked, not overloaded (see `WorkerConsumeLoopStalled`).

### WorkerConsumeLoopStalled

The single consume loop has not polled for more than 30 seconds, so no partition is progressing.
Compare with *Backoff time per second* and `orderflow_worker_messages_total{outcome="retry_scheduled"}`.

### WorkerRetryBackoffSaturated

The worker spends more than half its time sleeping in retry backoff. Find the failing orders with
`{service_name="worker"} |= "scheduling retry"`.

### DeadLetterRateHigh

More than 10 events dead lettered in 15 minutes. The routing log line
(`Routing event ... to orders.failed: <reason>`) gives the reason for each.

### KafkaDeliveryFailures

The producer got an error delivery report. Publishing is fire-and-forget, so every one is an event
the broker never received. Check broker health and `orderflow_kafka_client_errors_total`.

### OrderFailureRatioHigh

More than 20% of orders that finish in the worker end `Failed`. Break down
`orderflow_payments_total` by `reason`.

### ConsumerLagMonitorStale

No successful lag sample for 2 minutes, or none since the worker started. Lag alerts cannot fire
while this is active. Look for `Consumer lag sample for ... failed` in the worker logs.

### ProductCacheUnavailable

Product reads are falling back to Postgres because Redis connections fail. Latency will rise.

### KafkaClientErrors

librdkafka is reporting errors, usually broker connectivity. The `client` label tells you whether
it is the producer, consumer or lag monitor.

## Production notes

* `/metrics` is served on the API's public port. Behind an ingress, block it from outside or move it
  to a management port (`RequireHost`).
* Change the Grafana admin credentials, and put Grafana behind SSO.
* Single-binary Loki on a filesystem volume, and local Prometheus storage, suit one node. For more,
  move Loki to object storage and give Prometheus `remote_write` or a long-term store.
* The worker's metrics listener starts with the host. If it cannot bind (port taken, missing URL
  ACL on Windows for a wildcard host), the worker fails to start. Change `Observability__MetricsPort`,
  or set `Observability__Enabled=false` as a stop-gap.
* Retention: Prometheus 15 days (`--storage.tsdb.retention.time`), Loki 7 days
  (`limits_config.retention_period`).
