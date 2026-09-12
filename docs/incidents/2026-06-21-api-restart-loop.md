# Incident 2026-06-21 - API restart loop during Redis maintenance

**Duration:** 02:03 - 02:37 UTC (34m)
**Severity:** SEV1
**Author:** on-call (platform)

## Impact

The API was unavailable or degraded for 34 minutes during a planned Redis upgrade. Around 4,100
requests failed. The worker was unaffected and kept processing the events already on the topic.

## Timeline

| Time (UTC) | Event |
| --- | --- |
| 02:00 | Planned Redis upgrade starts; the managed instance fails over. |
| 02:02 | Redis is unreachable for about 40 seconds. |
| 02:03 | All three API containers are killed and restarted by the runtime. |
| 02:04 | Containers come back, Redis is still failing over, they are killed again. |
| 02:09 | Redis is healthy again, but the API containers keep restarting. |
| 02:18 | Noticed the restarts continued after Redis recovered. |
| 02:25 | `/health/ready` also red, reporting `kafka` as the failing check, which nobody could explain since Kafka was untouched. |
| 02:31 | Forced a fresh deploy of the API. |
| 02:37 | All containers healthy, error rate back to zero. |

## Mitigation

A full redeploy of the API. Restarting individual containers was not enough.

## Root cause (working hypothesis)

The liveness probe covers Redis, so a Redis outage looks like a dead API process and the runtime
restarts it. The restart loop then outlived the outage, which we believe is because the Redis
client is configured to abort when it cannot connect at startup, so a container that started
during the failover never established a connection afterwards.

We are less sure about the `kafka` readiness failures. The broker was healthy throughout, so the
most likely explanation is that the restarting containers saturated the broker with connections
from short-lived admin clients.

## Follow-ups

- [ ] Separate liveness from readiness: liveness should only prove the process is alive.
- [ ] Review the Redis client connection settings.
- [ ] Check whether the readiness checks need per-check timeouts.
- [ ] Add a runbook entry for cache maintenance windows.
