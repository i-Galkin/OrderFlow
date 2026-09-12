# 005 - Cannot follow a request from the API into the worker

**Reported by:** Platform / on-call
**Severity:** Medium
**Status:** Open

## Symptoms

Searching the logs for a correlation id returns only the API side of the story. The worker lines
for the same order carry a different id, so a single search never shows the whole flow.

## Steps to reproduce

1. `curl -H 'X-Correlation-ID: demo-1' -X POST .../api/orders/{id}/confirm`.
2. Search the API logs for `demo-1` - the request, the state change and the publish are all there.
3. Search the worker logs for `demo-1` - nothing.
4. Find the worker lines by order id instead: they carry a 32 character hex `CorrelationId` that
   appears nowhere else.

## Observations

* Every worker log line has *a* correlation id, it is simply not the caller's.
* The id in the worker changes on every redelivery of the same event.
* `kafka-console-consumer --property print.headers=true` shows a header on the message, and the
  value in the header is the one the caller sent.
* The `correlationId` field inside the JSON payload is also the caller's value.

## Impact

Incident triage takes far longer than it should; on-call has to stitch the timeline together by
order id.
