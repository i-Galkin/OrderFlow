# 007 - API reports not ready after a clean deploy

**Reported by:** Platform
**Severity:** Medium
**Status:** Open

## Symptoms

After a deploy into an empty environment, `/health/ready` returns `Unhealthy` and the rollout
never completes, even though the API answers ordinary requests perfectly well. Creating a single
order makes readiness go green and stay green.

## Observations

* The failing entry in the response body is `kafka`, with `"description": "topic orders.events
  not found"`.
* Under load, `kafka` occasionally flips to `Unhealthy` for one or two polls on an otherwise
  healthy cluster and then recovers.
* During a short Redis restart the API containers were restarted by the runtime, not just taken
  out of the load balancer. Postgres restarts do not have that effect.
* Each readiness poll shows a new client id in the broker logs.

## Impact

Deploys into fresh environments need a manual "warm-up" order, and Redis maintenance turns into
an API restart.
