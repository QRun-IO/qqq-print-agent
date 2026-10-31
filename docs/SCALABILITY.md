# History and diagnostic limits

Audit source removes the configured idle delay after a newly submitted successful job. It yields and releases the connection gate between jobs; empty queues, duplicate receipts, failures, errors and pauses retain their delay/backoff. A regression drains three fake jobs before a configured 20-second interval, then verifies empty/error responses do not hot-loop. The current audit gate passed 249 tests with zero warnings/errors. This removes a scheduling bottleneck; it does not certify sustained physical throughput.

The current source targets facilities producing about 10,000 jobs per day. Synthetic tests exercise 10,000/30,000 stored jobs, a 10,000-event delivery backlog and 30,000 normal status-log entries. They measure local query/write behavior with fake HTTP/printers; they do not certify physical printer throughput or production network capacity.

The 2026-10-30 Windows gate passed204 tests (39 Core and165 Windows/CLI) with zero build warnings/errors. On the local development machine, the30,000-row combined query/count/retention checks took90ms after seeding;10,000 durable acknowledgements took30.5s in200-event batches;30,000 diagnostic entries used1,960,000bytes and took46.6s. These are regression observations under concurrent tests/coverage, not latency guarantees. The QQQ gate separately covers10,000-row pagination and bounded claims. Full Unicode profiles passed the actual local management pipe, persistence, heartbeat and inventory serialization paths using a fake server.

## Job history

### Larger synthetic storage qualification

On 2026-10-30, a private harness exercised the packaged 0.1.8-rc.1 `AgentStore` and compiled queue-merge method, using synthetic data only. It grew history from 100,000 to 365,000 jobs, ran retention, then appended another 10,000 jobs through the production journal APIs. Final state was **375,000 summaries and 1,109,254 events**, with no pending events and SQLite `quick_check` returning `ok`. The reviewed implementation was `2e6a2e5`; this experiment did not independently match its packaged DLLs to the CI MSI.

The 10,000-job append took **210 seconds**, with a per-job median of **19.2 ms** and empirical p95 of **35.2 ms**. It reused one store connection and called the real track/record/acknowledge methods without an outer transaction. Historical fixture seeding used direct SQL and is excluded from these write measurements.

The following are medians from warm samples on one Windows workstation. Paging measurements include the production count query.

| Operation | 100,000 jobs | 375,000 jobs |
| --- | ---: | ---: |
| First 50 by creation time | 21 ms | 67 ms |
| Last 50 by creation time | 53 ms | 165 ms |
| Printer filter, 50 rows | 6 ms | 14 ms |
| Broad title prefix, 50 rows | 190 ms | 651 ms |
| Combined prefix/status/printer filter | 217 ms | 771 ms |
| Reopen, merge queued preview and serialize/parse 50 rows | 47 ms | 146 ms |

The slowest sampled combined filter took 805 ms. Ordinary 50-row pages allocated approximately 71–83 kB regardless of history size; native SQLite memory is outside that allocation counter. One default retention pass removed 250 jobs' event detail in 10 ms and retained all summaries. The final database occupied 1.11 GiB after checkpoint, with much historical detail deliberately left above the normal retention budget; this is not a steady-state disk-size estimate.

These results cover local storage, bounded results and the compiled merge path. They exclude service scheduling, IPC, GUI rendering, concurrent writers, cold caches, networking and physical printing. The fixture spans about 36.5 historical days at 10,000 jobs/day, not a year or indefinitely growing history. Counts, offsets and broad filters take more database work as history grows; retained summaries have no fixed lifetime size cap. Actual printer, network and workstation capacity still needs deployment-specific qualification.

### Paging and retention behavior

The Jobs page queries SQLite rather than filtering the latest snapshot. Page size is bounded to 100, with SQL counts and status/printer/review filters. Jobs default to original creation time descending; clickable headers sort the full local history by creation time, title, printer, displayed status or format, with a deterministic job-ID tie. Search matches the start of a job ID, title or printer name; literal percent and underscore characters are escaped. Search fields have SQLite prefix indexes. Only returned rows deserialize event JSON. Offset pages are live views: concurrent new jobs can move page boundaries, so refresh before assuming an unchanged list.

A bounded preview of up to 50 cloud-queued jobs interleaves with local history under the same selected sort order and keeps queued cancellation available before receipt. Matching local jobs suppress duplicate preview rows. The UI marks when the preview reaches its bound; its combined count is not an authoritative total for an arbitrarily large cloud queue. The merge reads at most 150 local candidates in bounded SQL pages, regardless of the requested offset, plus at most 50 cached queued jobs. Text ordering uses SQLite NOCASE (ASCII case folding, otherwise UTF-8 order) consistently for both sources; visible fallback labels and reviewed-status suffixes participate in ordering.

Outbox delivery reads at most 200 ordered events per poll. The worker heartbeats and defers new claims until the previous backlog has drained. Interrupted-state recovery is also batched. Job details expose the latest 200 retained events. The legacy event-to-summary migration runs once, rather than grouping all events each time the service opens its store.

Default detail retention is 30 days or 300,000 jobs, with at most 250 eligible jobs pruned per maintenance pass. Runtime schedules this maintenance once per minute. Pending events, active submissions, unreviewed failures and every uncertain outcome retain their evidence. Operator review never changes delivery status. Terminal summaries remain for duplicate suppression even after event detail is pruned; the database therefore has no fixed lifetime size ceiling. SQLite reuses freed pages; retention does not promise that its file immediately shrinks. Monitor disk use and resolve operational exceptions instead of deleting a live journal.

Creation time is separate from the latest status-update time. Receipt captures the normalized server creation timestamp once; later events and repeated claims preserve it. The one-time migration uses the earliest retained event timestamp, falling back to the stored update timestamp when old detail is gone. An absent historical timestamp remains unknown rather than inventing a date. The 0.1.6-rc.1 gate passed 211 tests (42 Core and 169 Windows/CLI), including the creation-time migration and every header order across 10,000 rows, both directions and distant pages. Locked restore and the build completed with zero warnings/errors. The 204-test timing observations above describe the prior source.

## Logs

AppKit retains its local-midnight daily filename rotation and shared log viewer. Machine-wide settings default to 30 days and 20 MiB per daily file; supported ranges are 1–365 days and 1–100 MiB. Service and companion use one cross-process lock for appends, settings, retention and clearing.

At the daily limit, the backend writes a warning when space permits and persists a cap marker. Settings exposes current bytes and whether that day's limit was reached. Further diagnostics are suppressed until the next local day, an increased limit or explicit clearing; printing continues. The cap is visible rather than silently discarding ongoing errors. Detailed high-frequency logs may need a higher limit than the normal three status entries per job used by the synthetic workload.

Clear logs requires explicit operator confirmation in the companion. The backend removes only exact application-owned dated log files and corresponding cap markers from its log directory. It leaves the job journal, credentials, settings and unrelated files intact. Reparse-point log paths are rejected or excluded. Completed MSI-update stages/install logs have a separate administrator-review policy and are not removed by this control.
