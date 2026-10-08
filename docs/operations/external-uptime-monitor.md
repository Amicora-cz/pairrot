# Operations task: external uptime monitoring

Status: open; not configured. Separate deployment task required by [Feature 019](../features/019-operational-health-and-alerting.md).

## Outcome

An uptime monitor outside Pairrot's VPS checks the public HTTPS `/health/ready` endpoint and notifies the operator even when the application, VPS or database is unavailable. A separate `/health` check distinguishes process availability from readiness. The notification path must not depend on Pairrot's application, database or outgoing webhook worker.

## Work to complete

1. Select an external monitoring service or an independently hosted checker. Record its owner and configuration location; do not run the only checker on the monitored VPS.
2. Configure the actual production HTTPS targets after Feature 019 is deployed. Suggested initial settings: check every 60 seconds, request timeout 10 seconds, alert after two consecutive failed checks, and resolve after two consecutive successes. A timeout, connection/TLS error, non-200 status or unexpected status body is a failure. Record the actual settings and any maintenance suppression policy.
3. Configure and verify an operator notification destination independent of Pairrot. Record the responsible operator and where notification routing is maintained; keep credentials out of this document.
4. In an agreed deployment verification window, stop the application container. Confirm an external incident and operator notification within the configured detection window, then restart and confirm recovery notification. Also verify a readiness-only failure is detected while `/health` remains successful.
5. Record verification time, targets, configured thresholds and incident/notification evidence below. Complete this task only after that evidence exists.

## Completion evidence

| Item | Recorded value |
| --- | --- |
| Responsible operator | Pending assignment |
| External monitor/service and configuration location | Pending |
| Production targets | Pending |
| Interval, timeout and failure/recovery thresholds | Pending |
| Independent notification routing verified | Pending |
| Container-stop incident and notification evidence | Pending |
| Readiness-only incident evidence | Pending |
| Recovery notification evidence | Pending |
| Verification date and verifier | Pending |

Feature 019's implementation and this operations task have separate completion states. Writing this task or passing application health-check tests does not establish that production is externally monitored.
