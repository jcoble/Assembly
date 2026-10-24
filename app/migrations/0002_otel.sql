-- OpenTelemetry received from agents, kept for reports. The GenAI fields are
-- columns; everything else the record carried, after redaction, is the JSON
-- `attributes` column. Rows older than 90 days are deleted at startup.

CREATE TABLE IF NOT EXISTS otel_spans (
    id INTEGER PRIMARY KEY,
    time_ms INTEGER NOT NULL,
    end_ms INTEGER,
    trace_id TEXT,
    span_id TEXT,
    parent_span_id TEXT,
    name TEXT NOT NULL,
    service TEXT,
    session_id TEXT,
    owned_id TEXT,
    run_id TEXT,
    node_run_id TEXT,
    model TEXT,
    operation TEXT,
    input_tokens INTEGER,
    output_tokens INTEGER,
    cache_read_tokens INTEGER,
    cache_write_tokens INTEGER,
    cost_micros INTEGER,
    duration_ms INTEGER,
    ttft_ms INTEGER,
    request_id TEXT,
    tool_name TEXT,
    status TEXT,
    attributes TEXT NOT NULL CHECK (json_valid(attributes))
);
CREATE INDEX IF NOT EXISTS otel_spans_time_idx ON otel_spans (time_ms);
CREATE INDEX IF NOT EXISTS otel_spans_owner_idx ON otel_spans (owned_id, time_ms);
CREATE INDEX IF NOT EXISTS otel_spans_step_idx ON otel_spans (node_run_id);
CREATE INDEX IF NOT EXISTS otel_spans_parent_idx ON otel_spans (trace_id, parent_span_id);

CREATE TABLE IF NOT EXISTS otel_log_events (
    id INTEGER PRIMARY KEY,
    time_ms INTEGER NOT NULL,
    trace_id TEXT,
    span_id TEXT,
    name TEXT NOT NULL,
    service TEXT,
    session_id TEXT,
    owned_id TEXT,
    run_id TEXT,
    node_run_id TEXT,
    model TEXT,
    operation TEXT,
    input_tokens INTEGER,
    output_tokens INTEGER,
    cache_read_tokens INTEGER,
    cache_write_tokens INTEGER,
    cost_micros INTEGER,
    duration_ms INTEGER,
    ttft_ms INTEGER,
    request_id TEXT,
    tool_name TEXT,
    status TEXT,
    attributes TEXT NOT NULL CHECK (json_valid(attributes))
);
CREATE INDEX IF NOT EXISTS otel_log_events_time_idx ON otel_log_events (time_ms);
CREATE INDEX IF NOT EXISTS otel_log_events_owner_idx ON otel_log_events (owned_id, time_ms);
CREATE INDEX IF NOT EXISTS otel_log_events_step_idx ON otel_log_events (node_run_id);

CREATE TABLE IF NOT EXISTS otel_metric_points (
    id INTEGER PRIMARY KEY,
    time_ms INTEGER NOT NULL,
    name TEXT NOT NULL,
    unit TEXT,
    value REAL,
    service TEXT,
    session_id TEXT,
    owned_id TEXT,
    run_id TEXT,
    node_run_id TEXT,
    model TEXT,
    attributes TEXT NOT NULL CHECK (json_valid(attributes))
);
CREATE INDEX IF NOT EXISTS otel_metric_points_time_idx ON otel_metric_points (time_ms);
CREATE INDEX IF NOT EXISTS otel_metric_points_owner_idx ON otel_metric_points (owned_id, time_ms);

-- Report views. A model call is a log event or span with tokens or cost; a
-- tool call is one with a tool name and a duration. A span whose child spans
-- carry tokens (TanStack AI's `chat` span repeats its iterations' usage) is
-- not counted again. Metric points repeat the log events' numbers and are
-- left out. Views hold no data, so they are recreated on every open.
DROP VIEW IF EXISTS otel_report_by_run;
DROP VIEW IF EXISTS otel_report_by_step;
DROP VIEW IF EXISTS otel_report_by_session;
DROP VIEW IF EXISTS otel_report_by_model;
DROP VIEW IF EXISTS otel_measures;

CREATE VIEW otel_measures AS
SELECT owned_id, run_id, node_run_id, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_micros,
       duration_ms AS call_ms, ttft_ms, NULL AS tool_ms, 1 AS calls, 0 AS tools
  FROM otel_log_events
 WHERE tool_name IS NULL AND COALESCE(input_tokens, output_tokens, cost_micros) IS NOT NULL
UNION ALL
SELECT owned_id, run_id, node_run_id, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_micros,
       duration_ms, ttft_ms, NULL, 1, 0
  FROM otel_spans s
 WHERE tool_name IS NULL AND COALESCE(input_tokens, output_tokens, cost_micros) IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM otel_spans c WHERE c.trace_id = s.trace_id AND c.parent_span_id = s.span_id
                     AND COALESCE(c.input_tokens, c.output_tokens, c.cost_micros) IS NOT NULL)
UNION ALL
SELECT owned_id, run_id, node_run_id, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, duration_ms, 0, 1
  FROM otel_log_events WHERE tool_name IS NOT NULL AND duration_ms IS NOT NULL
UNION ALL
SELECT owned_id, run_id, node_run_id, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, duration_ms, 0, 1
  FROM otel_spans WHERE tool_name IS NOT NULL AND duration_ms IS NOT NULL;

CREATE VIEW otel_report_by_run AS
SELECT run_id, SUM(calls) AS model_calls, SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens,
       SUM(cache_read_tokens) AS cache_read_tokens, SUM(cache_write_tokens) AS cache_write_tokens, SUM(cost_micros) AS cost_micros,
       SUM(call_ms) AS model_ms, CAST(AVG(call_ms) AS INTEGER) AS avg_call_ms, MAX(call_ms) AS slowest_call_ms,
       CAST(AVG(ttft_ms) AS INTEGER) AS avg_ttft_ms, SUM(tools) AS tool_calls, SUM(tool_ms) AS tool_ms
  FROM otel_measures WHERE run_id IS NOT NULL GROUP BY run_id;

CREATE VIEW otel_report_by_step AS
SELECT run_id, node_run_id, SUM(calls) AS model_calls, SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens,
       SUM(cache_read_tokens) AS cache_read_tokens, SUM(cache_write_tokens) AS cache_write_tokens, SUM(cost_micros) AS cost_micros,
       SUM(call_ms) AS model_ms, CAST(AVG(call_ms) AS INTEGER) AS avg_call_ms, MAX(call_ms) AS slowest_call_ms,
       CAST(AVG(ttft_ms) AS INTEGER) AS avg_ttft_ms, SUM(tools) AS tool_calls, SUM(tool_ms) AS tool_ms
  FROM otel_measures WHERE node_run_id IS NOT NULL GROUP BY run_id, node_run_id;

CREATE VIEW otel_report_by_session AS
SELECT owned_id, SUM(calls) AS model_calls, SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens,
       SUM(cache_read_tokens) AS cache_read_tokens, SUM(cache_write_tokens) AS cache_write_tokens, SUM(cost_micros) AS cost_micros,
       SUM(call_ms) AS model_ms, CAST(AVG(call_ms) AS INTEGER) AS avg_call_ms, MAX(call_ms) AS slowest_call_ms,
       CAST(AVG(ttft_ms) AS INTEGER) AS avg_ttft_ms, SUM(tools) AS tool_calls, SUM(tool_ms) AS tool_ms
  FROM otel_measures GROUP BY owned_id;

CREATE VIEW otel_report_by_model AS
SELECT model, SUM(calls) AS model_calls, SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens,
       SUM(cache_read_tokens) AS cache_read_tokens, SUM(cache_write_tokens) AS cache_write_tokens, SUM(cost_micros) AS cost_micros,
       SUM(call_ms) AS model_ms, CAST(AVG(call_ms) AS INTEGER) AS avg_call_ms, MAX(call_ms) AS slowest_call_ms,
       CAST(AVG(ttft_ms) AS INTEGER) AS avg_ttft_ms, SUM(tools) AS tool_calls, SUM(tool_ms) AS tool_ms
  FROM otel_measures WHERE model IS NOT NULL GROUP BY model;
