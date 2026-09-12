# 006 - Stock goes negative on popular SKUs

**Reported by:** Warehouse
**Severity:** Critical
**Status:** Open

## Symptoms

After promotions, a handful of products end up with a negative `StockQuantity` in the database.
The warehouse then receives picking lists for units that do not exist.

## Steps to reproduce

Not reproducible one order at a time. With a load generator:

1. Seed the database and pick `SKU-SCARCE-01` (5 units in stock).
2. Create 20 orders for 1 unit each and confirm them as fast as possible.
3. Most of the time 5 complete and 15 fail with insufficient stock, which is correct.
4. Occasionally 6 or 7 complete and the product ends at `-1` or `-2`.

## Observations

* The anomaly only appears when several orders for the same product are confirmed within the same
  second.
* It is more frequent when the worker is running with more than one replica.
* Sequential orders never produce it, and neither does a single worker replica processing the
  same 20 orders one after another.
* `inventory_reservations` always has exactly one row per order, so no order is reserved twice.
* The `Version` column on the affected products increases by the expected number of steps.

## Impact

Oversold promotions, manual reconciliation by the warehouse team after every campaign.
