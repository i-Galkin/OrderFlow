# 003 - Order list times out when filtered by status

**Reported by:** Back-office team
**Severity:** High
**Status:** Open

## Symptoms

`GET /api/orders?status=Pending` takes 20-40 seconds against the production data set and
sometimes fails with a gateway timeout. The unfiltered `GET /api/orders?page=1&pageSize=25`
answers in a few milliseconds on the same database.

It reproduces on a locally seeded database as well, just smaller: around 10 ms unfiltered
against 250-500 ms filtered, and the gap grows with the number of orders in that status.

## Steps to reproduce

1. Seed a database: `dotnet run --project src/OrderFlow.Api -- --seed`.
2. `time curl "http://localhost:8080/api/orders?page=1&pageSize=25"` - fast.
3. `time curl "http://localhost:8080/api/orders?page=1&pageSize=25&status=Pending"` - slow.
4. Repeat with `status=Cancelled` (a rare status) - noticeably faster than `Pending`.

## Observations

* `pageSize` makes no measurable difference; `pageSize=1` is just as slow.
* Asking for page 400 costs the same as page 1.
* The API container's memory climbs by a few hundred MB during the request and comes back down
  afterwards.
* Postgres reports one query per request, so it is not a connection or pooling problem.

## Impact

The back-office queue screen is unusable during the morning peak.
