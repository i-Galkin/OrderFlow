# 004 - Retrying a declined order marks it Completed

**Reported by:** QA
**Severity:** Critical
**Status:** Open

## Symptoms

An order that failed with `card_declined` becomes `Completed` after `POST /api/orders/{id}/retry`,
even though the card is still declined. No new authorisation appears in the payment stub's logs.

## Steps to reproduce

1. Create an order whose total ends in `.13` (for example one unit of `SKU-DECLINE-13`).
2. Confirm it and wait for the worker: the order ends up `Failed`, `failureReason:
   "card_declined"`.
3. `POST /api/orders/{id}/retry`.
4. Within a second the order is `Completed` and an `OrderCompleted` event is on the topic.

## Observations

* The worker logs `Resuming order ... on attempt 1 with an existing reservation` for these
  orders.
* Stock is only decremented once, which is correct.
* The same thing happens to orders that failed with `gateway_timeout` after the retry budget ran
  out, so it is not specific to declines.
* An order that fails because of insufficient stock behaves differently - retrying it stays
  `Failed`.

## Impact

Goods can be dispatched for orders that were never paid for.
