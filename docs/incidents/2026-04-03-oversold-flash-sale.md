# Incident 2026-04-03 - Oversold items during the spring flash sale

**Duration:** 12:00 - 12:14 UTC (detected at 14:30)
**Severity:** SEV2
**Author:** on-call (orders)

## Impact

Eleven SKUs finished the sale with negative stock; 63 orders were accepted for units that did not
exist. All 63 were cancelled manually and refunded the next day.

## Timeline

| Time (UTC) | Event |
| --- | --- |
| 12:00 | Flash sale opens. Order rate peaks at ~900/min for four minutes. |
| 12:04 | Worker replicas were at 4 (scaled up the previous week for the campaign). |
| 12:14 | Sale sells out, rate returns to normal. Nothing alerts. |
| 14:30 | Warehouse reports picking lists for out-of-stock items. |
| 14:55 | `SELECT * FROM products WHERE "StockQuantity" < 0` returns 11 rows. |
| 15:10 | Confirmed every affected order has exactly one row in `inventory_reservations`. |
| 15:40 | Confirmed the API rejected nothing - all 63 orders were valid at the moment they were confirmed. |
| 16:20 | Decision: cancel and refund, investigate afterwards. |

## Mitigation

None during the event - it was only noticed two hours later. The affected orders were cancelled
by hand, which released the reservations and brought stock back to zero.

## Root cause (working hypothesis)

Stock checks and the decrement happen in the worker, and with four replicas consuming different
partitions several orders for the same product were processed at the same moment. The
`Version` column on `products` is a concurrency token, so the database should have rejected the
second writer; our best guess is that the token is not being compared for these updates because
the affected rows all advanced their version by the full number of writes rather than one.

An alternative explanation the team has not ruled out is that the reservation rows were inserted
before the stock decrement was flushed, leaving a window in which a second replica read stale
stock.

## Follow-ups

- [ ] Add an alert on `StockQuantity < 0`. (done 2026-04-05)
- [ ] Decide whether stock should be reserved by the API at confirmation time instead.
- [ ] Add a database level guard so stock can never go below zero.
- [ ] Write a load test that confirms N orders for the same SKU concurrently.
