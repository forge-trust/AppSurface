# Deferred work

## Named-canary snapshot follow-ups (#645)

- Consider POST exact-list batches, host-declared snapshot profiles, asynchronous jobs, or stored snapshots only if adopters show that the bounded synchronous `GET /_appsurface/canaries` contract is insufficient.
- Keep polling, retry/backoff, CI exit codes, and GitHub reporting in the caller/workflow follow-up tracked by #625; the AppSurface package remains a current-proof producer, not a deployment controller.
- Revisit schema versioning or a generated client only after a stable external consumer demonstrates a compatibility need.
