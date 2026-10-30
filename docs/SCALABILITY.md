# History and diagnostic limits

The current source targets facilities producing about 10,000 jobs per day. Synthetic tests exercise 10,000/30,000 stored jobs, a 10,000-event delivery backlog and 30,000 normal status-log entries. They measure local query/write behavior with fake HTTP/printers; they do not certify physical printer throughput or production network capacity.

The 2026-10-30 Windows gate passed204 tests (39 Core and165 Windows/CLI) with zero build warnings/errors. On the local development machine, the30,000-row combined query/count/retention checks took90ms after seeding;10,000 durable acknowledgements took30.5s in200-event batches;30,000 diagnostic entries used1,960,000bytes and took46.6s. These are regression observations under concurrent tests/coverage, not latency guarantees. The QQQ gate separately covers10,000-row pagination and bounded claims. Full Unicode profiles passed the actual local management pipe, persistence, heartbeat and inventory serialization paths using a fake server.

## Job history

The Jobs page queries SQLite rather than filtering the latest snapshot. Page size is bounded to 100, with SQL counts, status/printer/review filters and stable timestamp/ID ordering. Search matches the start of a job ID, title or printer name; literal percent and underscore characters are escaped. Search fields have SQLite prefix indexes. Only returned rows deserialize event JSON. Offset pages are live views: concurrent new jobs can move page boundaries, so refresh before assuming an unchanged list.

A bounded preview of up to 50 cloud-queued jobs appears ahead of local history and keeps queued cancellation available before receipt. Matching local jobs suppress duplicate preview rows. The UI marks when the preview reaches its bound; its combined count is not an authoritative total for an arbitrarily large cloud queue.

Outbox delivery reads at most 200 ordered events per poll. The worker heartbeats and defers new claims until the previous backlog has drained. Interrupted-state recovery is also batched. Job details expose the latest 200 retained events. The legacy event-to-summary migration runs once, rather than grouping all events each time the service opens its store.

Default detail retention is 30 days or 300,000 jobs, with at most 250 eligible jobs pruned per maintenance pass. Runtime schedules this maintenance once per minute. Pending events, active submissions, unreviewed failures and every uncertain outcome retain their evidence. Operator review never changes delivery status. Terminal summaries remain for duplicate suppression even after event detail is pruned; the database therefore has no fixed lifetime size ceiling. SQLite reuses freed pages; retention does not promise that its file immediately shrinks. Monitor disk use and resolve operational exceptions instead of deleting a live journal.

## Logs

AppKit retains its local-midnight daily filename rotation and shared log viewer. Machine-wide settings default to 30 days and 20 MiB per daily file; supported ranges are 1–365 days and 1–100 MiB. Service and companion use one cross-process lock for appends, settings, retention and clearing.

At the daily limit, the backend writes a warning when space permits and persists a cap marker. Settings exposes current bytes and whether that day's limit was reached. Further diagnostics are suppressed until the next local day, an increased limit or explicit clearing; printing continues. The cap is visible rather than silently discarding ongoing errors. Detailed high-frequency logs may need a higher limit than the normal three status entries per job used by the synthetic workload.

Clear logs requires explicit operator confirmation in the companion. The backend removes only exact application-owned dated log files and corresponding cap markers from its log directory. It leaves the job journal, credentials, settings and unrelated files intact. Reparse-point log paths are rejected or excluded. Completed MSI-update stages/install logs have a separate administrator-review policy and are not removed by this control.
