-- Copyright (c) 2026 James Maes
-- SPDX-License-Identifier: MIT
--
-- The oldest journal the repository's history can establish: one events table of six columns and nothing else.
--
-- The first commit of the store (461a3c0, 2026-10-29, in tag v0.1.0-rc.1) creates events with these six columns and
-- then adds recorded_at when it is missing. That check can only be for a journal made before the repository began, so
-- a journal without the column existed. Whether such a journal had the exposed_printers table is not recorded; the
-- one here does not, which is the harder case. Every later table and column is absent: the scope column and the job
-- summaries (6e0476e), the indexes and local names (b45a906), the creation time (d9033ae), the control tables
-- (2c1c5f1), realtime settings (8a3b7c1) and the certificate exception (aaa5051).
--
-- The rows are synthetic: one job sent to the printer, one received and never finished, and one failed whose last
-- event has not been delivered to the server.

PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, event_id TEXT NOT NULL UNIQUE, state TEXT NOT NULL, json TEXT NOT NULL, acknowledged INTEGER NOT NULL DEFAULT 0);

INSERT INTO events(sequence,job_id,event_id,state,json,acknowledged) VALUES
 (1,'job-a','ev-a-received','RECEIVED','{"eventId":"ev-a-received","status":"RECEIVED"}',1),
 (2,'job-a','ev-a-submitting','SUBMITTING','{"eventId":"ev-a-submitting","status":"SUBMITTING"}',1),
 (3,'job-a','ev-a-spooled','SPOOLED','{"eventId":"ev-a-spooled","status":"SPOOLED","spoolerJobId":"7"}',1),
 (4,'job-b','ev-b-received','RECEIVED','{"eventId":"ev-b-received","status":"RECEIVED"}',1),
 (5,'job-c','ev-c-received','RECEIVED','{"eventId":"ev-c-received","status":"RECEIVED"}',1),
 (6,'job-c','ev-c-failed','FAILED','{"eventId":"ev-c-failed","status":"FAILED","message":"The printer is offline."}',0);
