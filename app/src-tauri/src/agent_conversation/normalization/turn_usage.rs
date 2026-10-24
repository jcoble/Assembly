//! What a finished ACP turn used and what it cost, read from the wire.

use serde_json::Value;

/// One model's tokens for one turn. Input is uncached input; Claude and Codex
/// both report `inputTokens` that way over ACP.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ModelTokens {
    pub model: String,
    pub input: u64,
    pub output: u64,
    pub cache_read: u64,
    pub cache_write: u64,
    pub reasoning: u64,
}

fn count(value: &Value, keys: &[&str]) -> u64 {
    keys.iter().find_map(|key| value.get(*key).and_then(Value::as_u64)).unwrap_or(0)
}

/// One entry per model from the `session/prompt` result's `_meta.quota.model_usage`
/// (field names differ between the Claude and Codex adapters), or the standard
/// ACP `usage` under the session's model when the agent gives no breakdown.
pub fn turn_model_tokens(result: &Value, session_model: Option<&str>) -> Vec<ModelTokens> {
    let read = |model: &str, usage: &Value| ModelTokens {
        model: model.to_string(),
        input: count(usage, &["inputTokens"]),
        output: count(usage, &["outputTokens"]),
        cache_read: count(usage, &["cachedReadTokens", "cachedInputTokens"]),
        cache_write: count(usage, &["cachedWriteTokens", "cacheWriteInputTokens"]),
        reasoning: count(usage, &["thoughtTokens", "reasoningOutputTokens"]),
    };
    let tokens: Vec<ModelTokens> = match result.pointer("/_meta/quota/model_usage").and_then(Value::as_array) {
        Some(models) => models.iter().filter_map(|entry| Some(read(entry.get("model")?.as_str()?, entry.get("token_count")?))).collect(),
        None => result.get("usage").filter(|usage| usage.is_object())
            .map(|usage| read(session_model.unwrap_or("unknown"), usage)).into_iter().collect(),
    };
    tokens.into_iter().filter(|t| t.input + t.output + t.cache_read + t.cache_write > 0).collect()
}

/// The running cost total in USD a `usage_update` session notification carries, if any.
pub fn reported_cost_total(params: &Value) -> Option<f64> {
    params.pointer("/update/cost/amount").and_then(Value::as_f64)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    // Recorded from claude-agent-acp 0.94.0: the cost rides only on the update sent at the turn's result.
    #[test]
    fn only_a_usage_update_with_a_cost_reports_a_total() {
        let with_cost = json!({"sessionId": "s", "update": {"sessionUpdate": "usage_update", "used": 20680, "size": 1000000, "cost": {"amount": 0.0838594, "currency": "USD"}}});
        let without = json!({"sessionId": "s", "update": {"sessionUpdate": "usage_update", "used": 20680, "size": 1000000}});
        assert_eq!(reported_cost_total(&with_cost), Some(0.0838594));
        assert_eq!(reported_cost_total(&without), None);
    }
}
