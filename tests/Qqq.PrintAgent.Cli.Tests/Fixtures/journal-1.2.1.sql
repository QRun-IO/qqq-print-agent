-- Copyright (c) 2026 James Maes
-- SPDX-License-Identifier: MIT
--
-- The journal as an installation of 1.2.1 leaves it: the last release without a schema version (user_version stays 0).
--
-- The schema is transcribed from tag v1.2.1, src/Qqq.PrintAgent.Windows/AgentStore*.cs, statement by statement and in
-- the order that release runs them on a new installation: InitializeDatabase (events, exposed_printers, the two
-- columns added to events, local_jobs), CreateJobIndexes, MigrateJobCreationTime (the column added to local_jobs and
-- the five display indexes, whose expressions come from JobSorting.SqlColumn at that tag), InitializeControls, and
-- then the three tables created on first use: realtime_settings (read when the service starts), local_metadata (read
-- by the first snapshot) and server_trust (read by the first sign-in). SQLite keeps each table's text as it was
-- created plus the columns added later, so creating and altering here, as 1.2.1 did, gives the stored schema text of
-- a real installation.
--
-- The rows are synthetic. Two connection scopes appear: the current pairing (https://synthetic.invalid, device
-- "device") and an earlier one (https://earlier.invalid, device "device-before"). A scope is the upper-case
-- hexadecimal SHA-256 of the server address, a line feed and the device identifier. The JSON is what 1.2.1 wrote,
-- including the authorizationRequired member of the control state that later versions no longer have.

PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, event_id TEXT NOT NULL UNIQUE, state TEXT NOT NULL, json TEXT NOT NULL, acknowledged INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS exposed_printers(printer_id TEXT PRIMARY KEY);
ALTER TABLE events ADD COLUMN recorded_at TEXT;
ALTER TABLE events ADD COLUMN connection_id TEXT;
CREATE TABLE IF NOT EXISTS local_jobs(connection_id TEXT NOT NULL, job_id TEXT NOT NULL, printer_id TEXT, printer_name TEXT, title TEXT, format TEXT, state TEXT, last_event_json TEXT, updated_at TEXT, failure_acknowledged INTEGER NOT NULL DEFAULT 0, history_pruned INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(connection_id,job_id));

CREATE INDEX IF NOT EXISTS events_outbox ON events(connection_id,acknowledged,sequence);
CREATE INDEX IF NOT EXISTS events_job ON events(connection_id,job_id,sequence);
CREATE INDEX IF NOT EXISTS jobs_recent ON local_jobs(connection_id,updated_at DESC,job_id DESC);
CREATE INDEX IF NOT EXISTS jobs_status ON local_jobs(connection_id,state,failure_acknowledged,updated_at DESC,job_id DESC);
CREATE INDEX IF NOT EXISTS jobs_printer ON local_jobs(connection_id,printer_id,updated_at DESC,job_id DESC);
CREATE INDEX IF NOT EXISTS jobs_attention ON local_jobs(connection_id,CASE WHEN state IN ('FAILED','OUTCOME_UNKNOWN') AND failure_acknowledged=0 THEN 0 ELSE 1 END,updated_at DESC,job_id DESC);
CREATE INDEX IF NOT EXISTS jobs_retention ON local_jobs(connection_id,history_pruned,updated_at DESC,job_id DESC);
CREATE INDEX IF NOT EXISTS jobs_title_search ON local_jobs(connection_id,title COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS jobs_printer_search ON local_jobs(connection_id,printer_name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS jobs_id_search ON local_jobs(connection_id,job_id COLLATE NOCASE);

ALTER TABLE local_jobs ADD COLUMN created_at TEXT;
CREATE INDEX IF NOT EXISTS jobs_display_createdAt ON local_jobs(connection_id,created_at COLLATE NOCASE DESC,job_id DESC);
CREATE INDEX IF NOT EXISTS jobs_display_title ON local_jobs(connection_id,COALESCE(title,job_id) COLLATE NOCASE DESC,job_id DESC);
CREATE INDEX IF NOT EXISTS jobs_display_printerName ON local_jobs(connection_id,COALESCE(printer_name,printer_id,'Not recorded') COLLATE NOCASE DESC,job_id DESC);
CREATE INDEX IF NOT EXISTS jobs_display_status ON local_jobs(connection_id,(CASE COALESCE(state,'CLAIMED') WHEN 'READY' THEN 'Ready' WHEN 'OFFLINE' THEN 'Offline' WHEN 'PAUSED' THEN 'Paused' WHEN 'ERROR' THEN 'Needs attention' WHEN 'QUEUED' THEN 'Queued' WHEN 'CLAIMED' THEN 'Assigned to this computer' WHEN 'RECEIVED' THEN 'Received' WHEN 'SUBMITTING' THEN 'Sending to printer' WHEN 'SPOOLED' THEN 'Sent to printer' WHEN 'SPOOLER_COMPLETED' THEN 'Windows completed the job' WHEN 'FAILED' THEN 'Failed' WHEN 'OUTCOME_UNKNOWN' THEN 'Check printer before retrying' ELSE 'Status unavailable' END || CASE WHEN failure_acknowledged<>0 THEN ' · Reviewed' ELSE '' END) COLLATE NOCASE DESC,job_id DESC);
CREATE INDEX IF NOT EXISTS jobs_display_format ON local_jobs(connection_id,COALESCE(format,'—') COLLATE NOCASE DESC,job_id DESC);

CREATE TABLE IF NOT EXISTS control_state(connection_id TEXT PRIMARY KEY,json TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS control_commands(connection_id TEXT NOT NULL,command_id TEXT NOT NULL,command_json TEXT NOT NULL,receipt_json TEXT,acknowledged INTEGER NOT NULL DEFAULT 0,expires_at TEXT NOT NULL,PRIMARY KEY(connection_id,command_id));
CREATE INDEX IF NOT EXISTS control_outbox ON control_commands(connection_id,acknowledged);
CREATE INDEX IF NOT EXISTS control_expiry ON control_commands(expires_at);

CREATE TABLE IF NOT EXISTS realtime_settings(connection_id TEXT PRIMARY KEY,json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS local_metadata(profile_key TEXT PRIMARY KEY,json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS server_trust(scope TEXT PRIMARY KEY,json TEXT NOT NULL);

-- Job events. Sequence 5 and 9 have not been delivered to the server (acknowledged = 0); sequence 7 belongs to the
-- earlier pairing.
INSERT INTO events(sequence,job_id,event_id,state,json,acknowledged,recorded_at,connection_id) VALUES
 (1,'job-1001','ev-1001-received','RECEIVED','{"eventId":"ev-1001-received","status":"RECEIVED"}',1,'2026-11-06T14:03:20.1000000+00:00','B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED'),
 (2,'job-1001','ev-1001-submitting','SUBMITTING','{"eventId":"ev-1001-submitting","status":"SUBMITTING"}',1,'2026-11-06T14:03:21.2000000+00:00','B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED'),
 (3,'job-1001','ev-1001-spooled','SPOOLED','{"eventId":"ev-1001-spooled","status":"SPOOLED","spoolerJobId":"41"}',1,'2026-11-06T14:03:22.3000000+00:00','B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED'),
 (4,'job-1002','ev-1002-received','RECEIVED','{"eventId":"ev-1002-received","status":"RECEIVED"}',1,'2026-11-06T14:10:00.0000000+00:00','B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED'),
 (5,'job-1002','ev-1002-failed','FAILED','{"eventId":"ev-1002-failed","status":"FAILED","message":"The printer is offline. No bytes were submitted."}',0,'2026-11-06T14:10:01.5000000+00:00','B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED'),
 (6,'job-1003','ev-1003-failed','FAILED','{"eventId":"ev-1003-failed","status":"FAILED","message":"The document did not match its checksum. No bytes were submitted."}',1,'2026-11-06T14:20:00.0000000+00:00','B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED'),
 (7,'job-0900','ev-0900-spooled','SPOOLED','{"eventId":"ev-0900-spooled","status":"SPOOLED","spoolerJobId":"12"}',1,'2026-10-20T09:00:00.0000000+00:00','5F2379F644BF0ADC1CB267E7C0BDE2DE1E2CABEDC247DC7CE598E73E67854221'),
 (8,'job-1004','ev-1004-received','RECEIVED','{"eventId":"ev-1004-received","status":"RECEIVED"}',1,'2026-11-06T15:00:00.0000000+00:00','B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED'),
 (9,'job-1004','ev-1004-unknown','OUTCOME_UNKNOWN','{"eventId":"ev-1004-unknown","status":"OUTCOME_UNKNOWN","message":"Agent stopped during native submission; inspect the printer before retrying."}',0,'2026-11-06T15:02:00.0000000+00:00','B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED');

-- Job summaries: one sent to the printer, one failed and not reviewed, one failed and reviewed, one whose outcome is
-- unknown, one whose event detail retention has removed (history_pruned = 1, no events), and one of the earlier
-- pairing.
INSERT INTO local_jobs(connection_id,job_id,printer_id,printer_name,title,format,state,last_event_json,updated_at,failure_acknowledged,history_pruned,created_at) VALUES
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','job-1001','printer-a','Front desk laser','Packing slip 1001','PDF','SPOOLED','{"eventId":"ev-1001-spooled","status":"SPOOLED","spoolerJobId":"41"}','2026-11-06T14:03:22.3000000+00:00',0,0,'2026-11-06T14:03:19.0000000+00:00'),
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','job-1002','printer-a','Front desk laser','Packing slip 1002','PDF','FAILED','{"eventId":"ev-1002-failed","status":"FAILED","message":"The printer is offline. No bytes were submitted."}','2026-11-06T14:10:01.5000000+00:00',0,0,'2026-11-06T14:09:58.0000000+00:00'),
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','job-1003','printer-zpl','Label printer','Shipping label 1003','ZPL','FAILED','{"eventId":"ev-1003-failed","status":"FAILED","message":"The document did not match its checksum. No bytes were submitted."}','2026-11-06T14:20:00.0000000+00:00',1,0,'2026-11-06T14:19:55.0000000+00:00'),
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','job-1004','printer-a','Front desk laser','Packing slip 1004','PDF','OUTCOME_UNKNOWN','{"eventId":"ev-1004-unknown","status":"OUTCOME_UNKNOWN","message":"Agent stopped during native submission; inspect the printer before retrying."}','2026-11-06T15:02:00.0000000+00:00',0,0,'2026-11-06T14:59:50.0000000+00:00'),
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','job-0950','printer-a','Front desk laser','Packing slip 0950','PDF','SPOOLED','{"eventId":"ev-0950-spooled","status":"SPOOLED","spoolerJobId":"30"}','2026-10-01T08:00:00.0000000+00:00',0,1,'2026-10-01T07:59:58.0000000+00:00'),
 ('5F2379F644BF0ADC1CB267E7C0BDE2DE1E2CABEDC247DC7CE598E73E67854221','job-0900','printer-a','Front desk laser','Packing slip 0900','PDF','SPOOLED','{"eventId":"ev-0900-spooled","status":"SPOOLED","spoolerJobId":"12"}','2026-10-20T09:00:00.0000000+00:00',0,0,'2026-10-20T08:59:57.0000000+00:00');

-- Sharing and the operator's own names.
INSERT INTO exposed_printers(printer_id) VALUES ('printer-a'),('printer-zpl');
INSERT INTO local_metadata(profile_key,json) VALUES
 ('device','{"name":"Front desk PC","description":"Shipping station\nsecond shift","location":"Dock 2"}'),
 ('printer:printer-a','{"displayName":"Front desk laser","description":"Tray 2 holds label stock","location":"Dock 2"}'),
 ('printer:printer-zpl','{"displayName":"Label printer","description":"","location":""}');

-- Remote control: the desired state of each pairing, a delivered command, a receipt that has not been delivered and a
-- command whose result was never recorded.
INSERT INTO control_state(connection_id,json,updated_at) VALUES
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','{"support":"SUPPORTED","desiredState":{"revision":7,"dispatchPaused":true,"connectionState":"CONNECTED"},"authorizationRequired":false,"lastContact":"2026-11-06T15:04:00.0000000Z","lastCommand":"CANCEL_SPOOL","lastOutcome":"ALREADY_ABSENT","lastCommandAt":"2026-11-06T14:31:00.0000000Z"}','2026-11-06T15:04:00.0000000+00:00'),
 ('5F2379F644BF0ADC1CB267E7C0BDE2DE1E2CABEDC247DC7CE598E73E67854221','{"support":"SUPPORTED","desiredState":{"revision":2,"dispatchPaused":false,"connectionState":"CONNECTED"},"authorizationRequired":false,"lastContact":"2026-10-20T09:05:00.0000000Z"}','2026-10-20T09:05:00.0000000+00:00');
INSERT INTO control_commands(connection_id,command_id,command_json,receipt_json,acknowledged,expires_at) VALUES
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','cmd-refresh','{"commandId":"cmd-refresh","requestId":"req-1","action":"REFRESH_INVENTORY","createdAt":"2026-11-06T14:25:00Z","expiresAt":"2026-11-06T14:30:00Z"}','{"receiptId":"receipt-1","outcome":"INVENTORY_REFRESHED","completedAt":"2026-11-06T14:25:01.0000000Z"}',1,'2026-11-06T14:30:00.0000000+00:00'),
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','cmd-cancel','{"commandId":"cmd-cancel","requestId":"req-2","action":"CANCEL_SPOOL","createdAt":"2026-11-06T14:30:30Z","expiresAt":"2026-11-06T14:35:30Z","jobId":"job-1001"}','{"receiptId":"receipt-2","outcome":"ALREADY_ABSENT","completedAt":"2026-11-06T14:31:00.0000000Z","message":"The document was no longer in the Windows queue."}',0,'2026-11-06T14:35:30.0000000+00:00'),
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','cmd-interrupted','{"commandId":"cmd-interrupted","requestId":"req-3","action":"REFRESH_INVENTORY","createdAt":"2026-11-06T15:03:00Z","expiresAt":"2026-11-06T15:08:00Z"}',NULL,0,'2026-11-06T15:08:00.0000000+00:00');

-- The server's policy as cached for the current pairing, and the development certificate exception.
INSERT INTO realtime_settings(connection_id,json) VALUES
 ('B80C4B521AC51BDCD7D606BAAFDBFE37BE3A7E34B3FBF39B5FBEC8DA5F1029ED','{"revision":3,"maxPdfBytes":104857600,"maxZplBytes":20971520,"maxFrameBytes":1048576,"maxQueuedFrames":64,"maxQueuedFrameBytes":4194304,"maxConcurrentDownloads":2,"maxConcurrentPrints":1,"maxStagedBytes":536870912,"maxDownloadBytesPerSecond":0,"requestTimeoutMillis":30000,"downloadConnectTimeoutMillis":30000,"webSocketHandshakeTimeoutMillis":30000,"ackTimeoutMillis":15000,"downloadIdleTimeoutMillis":30000,"downloadTimeoutMillis":600000,"heartbeatIntervalSeconds":20,"heartbeatTimeoutSeconds":60,"reconnectMinDelayMillis":1000,"reconnectMaxDelayMillis":30000,"reconnectJitterPercent":20,"maxCopies":100,"nativeSubmissionTimeoutMillis":120000}');
INSERT INTO server_trust(scope,json) VALUES ('machine','{"allowUntrustedCertificate":true,"origin":"https://synthetic.invalid"}');
