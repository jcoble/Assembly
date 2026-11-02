-- Actual RowStore producer 00108dd9ad780fba2c7f1cd69d6e0353c650caa0; isolated RowConverter/Commit backup.
-- Backup SHA256 1cb07332f7199e1777470e013b63630c59c4f879183cc36f3b47ef93289a5512; actual PRAGMA user_version=4.
CREATE TABLE audit(at INTEGER NOT NULL, what TEXT NOT NULL);
INSERT INTO audit VALUES(1793581016431,'rows.schema');
INSERT INTO audit VALUES(1793581016461,'rows.commit');
CREATE TABLE sessions(session_id TEXT PRIMARY KEY, next_seq INTEGER NOT NULL);
INSERT INTO sessions VALUES('fixture-session',3);
CREATE TABLE rows(session_id TEXT NOT NULL, seq INTEGER NOT NULL, run_id TEXT, item_id TEXT,
  kind TEXT NOT NULL, payload TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (session_id, seq));
INSERT INTO "rows" VALUES('fixture-session',1,'fixture-run',NULL,'user','{"text":"actual format4 prompt","clientId":"fixture-client"}',1234);
INSERT INTO "rows" VALUES('fixture-session',2,'fixture-run',NULL,'turn','{"state":"started"}',1234);
CREATE TABLE tool_details(session_id TEXT NOT NULL, item_id TEXT NOT NULL, input TEXT, output TEXT,
  diff TEXT, path TEXT, output_source TEXT, acp_session_id TEXT, PRIMARY KEY (session_id, item_id));
CREATE TABLE acp_terminal_details(
  session_id TEXT NOT NULL, acp_session_id TEXT NOT NULL, terminal_id TEXT NOT NULL,
  output BLOB, output_bytes INTEGER, command TEXT, cwd TEXT, exit_status TEXT,
  PRIMARY KEY(session_id, acp_session_id, terminal_id));
CREATE TABLE tool_terminal_refs(
  session_id TEXT NOT NULL, item_id TEXT NOT NULL, acp_session_id TEXT NOT NULL, terminal_id TEXT NOT NULL,
  ordinal INTEGER NOT NULL, current INTEGER NOT NULL,
  PRIMARY KEY(session_id, item_id, acp_session_id, terminal_id));
CREATE INDEX audit_at ON audit(at);
CREATE INDEX rows_item ON rows(session_id, item_id, kind, seq);
CREATE INDEX rows_summary ON rows(session_id, kind, item_id, seq) WHERE kind IN ('turn', 'ask', 'task', 'subagent');
PRAGMA user_version=4;
