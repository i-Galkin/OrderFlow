# Incident 2026-02-14 - Order backlog during the Valentine's campaign

**Duration:** 09:12 - 10:48 UTC (1h36m)
**Severity:** SEV2
**Author:** on-call (platform)

## Impact

Roughly 18,000 orders stayed in `Confirmed` for up to 40 minutes. No orders were lost and no
customer was charged twice, but the "order confirmed" emails went out an hour late.

## Timeline

| Time (UTC) | Event |
| --- | --- |
| 09:05 | Campaign goes live, order rate rises from ~30/min to ~500/min. |
| 09:12 | Consumer lag alert on `orders.events` fires (lag > 5,000). |
| 09:15 | On-call confirms the worker is up and its CPU is at 4%. |
| 09:22 | Worker logs show repeated `gateway_timeout` entries for a small set of orders. |
| 09:30 | Scaled the worker from 1 to 3 replicas. Lag keeps growing. |
| 09:41 | Noticed 27 events had been routed to `orders.failed`. |
| 09:55 | Restarted all worker replicas; lag dropped briefly, then resumed climbing. |
| 10:20 | Disabled the campaign's high-value bundle (total 12,499.77) at the storefront. |
| 10:31 | Lag starts to fall steadily. |
| 10:48 | Lag back to zero, backlog drained. |

## Mitigation

Removing the high-value bundle from the campaign stopped new timeout-prone orders from entering
the topic, after which the worker drained the backlog on its own.

## Root cause (working hypothesis)

The payment gateway stub rate limits high-value authorisations, so a burst of expensive orders
produced a burst of timeouts. One worker replica could not keep up with the retry volume on top
of the regular traffic, so lag accumulated. Scaling did not help immediately because the new
replicas had to wait for a consumer group rebalance.

## Follow-ups

- [ ] Alert on dead letter topic volume, not only on consumer lag. (done 2026-02-20)
- [ ] Revisit `Kafka:MaxRetryAttempts`; three attempts may be too many during campaigns.
- [ ] Consider a separate topic for retries so they cannot compete with fresh orders.
- [ ] Load test with 5% timeout-prone orders before the next campaign.
