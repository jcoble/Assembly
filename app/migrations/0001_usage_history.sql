CREATE TABLE IF NOT EXISTS usage_events (
    id INTEGER PRIMARY KEY,
    provider TEXT NOT NULL,
    provider_instance_id TEXT NOT NULL,
    owned_id TEXT,
    workflow_id TEXT,
    turn_id TEXT,
    project_id TEXT,
    workspace_id TEXT,
    occurred_at_micros INTEGER NOT NULL,
    input_tokens INTEGER NOT NULL CHECK (input_tokens >= 0),
    output_tokens INTEGER NOT NULL CHECK (output_tokens >= 0),
    cache_read_tokens INTEGER NOT NULL CHECK (cache_read_tokens >= 0),
    cache_write_tokens INTEGER NOT NULL CHECK (cache_write_tokens >= 0),
    reasoning_tokens INTEGER NOT NULL CHECK (reasoning_tokens >= 0),
    model TEXT NOT NULL,
    estimated_cost_micros INTEGER,
    estimate_rate_version TEXT,
    source_kind TEXT NOT NULL,
    source_event_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    inserted_at_micros INTEGER NOT NULL,
    node_run_id TEXT,
    reported_cost_micros INTEGER,
    -- The provider's running cost total the share was taken from.
    reported_cost_total_micros INTEGER,
    UNIQUE (provider, provider_instance_id, source_kind, source_event_id)
);

CREATE INDEX IF NOT EXISTS usage_events_time_idx ON usage_events (occurred_at_micros);
CREATE INDEX IF NOT EXISTS usage_events_provider_model_time_idx ON usage_events (provider, model, occurred_at_micros);
CREATE INDEX IF NOT EXISTS usage_events_owner_time_idx ON usage_events (owned_id, occurred_at_micros);
CREATE INDEX IF NOT EXISTS usage_events_workflow_time_idx ON usage_events (workflow_id, occurred_at_micros);
CREATE INDEX IF NOT EXISTS usage_events_project_time_idx ON usage_events (project_id, occurred_at_micros);
CREATE INDEX IF NOT EXISTS usage_events_source_key_idx ON usage_events (source_key);

CREATE TABLE IF NOT EXISTS usage_source_cursors (
    provider TEXT NOT NULL,
    provider_instance_id TEXT NOT NULL,
    source_kind TEXT NOT NULL,
    source_key TEXT NOT NULL,
    file_identity TEXT NOT NULL,
    offset_bytes INTEGER NOT NULL CHECK (offset_bytes >= 0),
    size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
    modified_at_micros INTEGER NOT NULL,
    last_event_id TEXT,
    updated_at_micros INTEGER NOT NULL,
    PRIMARY KEY (provider, provider_instance_id, source_kind, source_key)
);

CREATE TABLE IF NOT EXISTS usage_rate_versions (
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    effective_from_micros INTEGER NOT NULL,
    effective_to_micros INTEGER,
    input_micros_per_million INTEGER NOT NULL CHECK (input_micros_per_million >= 0),
    output_micros_per_million INTEGER NOT NULL CHECK (output_micros_per_million >= 0),
    cache_read_micros_per_million INTEGER NOT NULL CHECK (cache_read_micros_per_million >= 0),
    cache_write_micros_per_million INTEGER NOT NULL CHECK (cache_write_micros_per_million >= 0),
    reasoning_micros_per_million INTEGER NOT NULL CHECK (reasoning_micros_per_million >= 0),
    version TEXT NOT NULL,
    PRIMARY KEY (provider, model, effective_from_micros, version)
);
CREATE INDEX IF NOT EXISTS usage_rate_lookup_idx ON usage_rate_versions (provider, model, effective_from_micros, effective_to_micros);

-- US dollars per million tokens. Rebuilt at startup from the rates Assembly
-- ships; never edited in place.
CREATE TABLE IF NOT EXISTS usage_rates (
    model TEXT PRIMARY KEY,
    input_rate REAL NOT NULL,
    output_rate REAL NOT NULL,
    cache_read_rate REAL NOT NULL,
    cache_write_rate REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS usage_daily_rollups (
    day TEXT NOT NULL,
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    project_id TEXT,
    event_count INTEGER NOT NULL,
    session_count INTEGER NOT NULL,
    turn_count INTEGER NOT NULL,
    workflow_count INTEGER NOT NULL,
    input_tokens INTEGER NOT NULL,
    output_tokens INTEGER NOT NULL,
    cache_read_tokens INTEGER NOT NULL,
    cache_write_tokens INTEGER NOT NULL,
    reasoning_tokens INTEGER NOT NULL,
    estimated_cost_micros INTEGER,
    PRIMARY KEY (day, provider, model, project_id)
);

CREATE VIEW IF NOT EXISTS usage_daily_provider_model AS
SELECT day, provider, model, project_id, event_count, session_count, turn_count, workflow_count,
       input_tokens, output_tokens, estimated_cost_micros
FROM usage_daily_rollups;
