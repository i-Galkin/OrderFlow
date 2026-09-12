# 002 - Orders sit in Confirmed for minutes during busy periods

**Reported by:** Operations
**Severity:** High
**Status:** Open

## Symptoms

Normally an order moves from `Confirmed` to `Completed` in under a second. Several times a day
the whole queue stalls: consumer lag on `orders.events` climbs into the thousands and orders stay
`Confirmed` for two to ten minutes, then the backlog drains on its own.

## Observations

* The stall always starts shortly after one or more orders fail with `gateway_timeout` in the
  worker logs.
* Lag grows on every partition, not only the one carrying the slow order.
* Worker CPU is near zero while the lag is growing, so it is not doing work.
* `kafka-consumer-groups --describe --group orderflow-worker` shows the same offsets for tens of
  seconds at a time, then a jump.
* The volume of orders during a stall is not unusual - we have handled three times that rate
  without any lag.

## Impact

Customers see "we are preparing your order" for minutes, and support gets tickets asking whether
the payment went through.
