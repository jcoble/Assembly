//! Complete snapshots replace only their source and scope. Sparse event data is
//! an invalidation, not permission to fabricate current display fields.
use std::collections::{HashMap, HashSet};
use serde_json::Value;
use super::{EngineCursor, EngineItem, EngineKey, EngineRefusal, EngineScope};

#[derive(Default)]
pub struct EngineStore {
    items: HashMap<EngineKey, EngineItem>,
    scopes: HashMap<EngineScope, HashSet<EngineKey>>,
    cursors: HashMap<EngineScope, EngineCursor>,
    pending: HashMap<EngineKey, i64>,
}

impl EngineStore {
    pub fn item(&self, key: &EngineKey) -> Option<&EngineItem> { self.items.get(key) }
    pub fn items(&self, scope: &EngineScope) -> Vec<EngineItem> {
        self.scopes.get(scope).into_iter().flatten()
            .filter_map(|key| self.items.get(key).cloned()).collect()
    }
    pub fn cursor(&self, scope: &EngineScope) -> Option<&EngineCursor> { self.cursors.get(scope) }
    pub fn pending(&self, key: &EngineKey) -> Option<i64> { self.pending.get(key).copied() }

    pub fn snapshot(&mut self, scope: EngineScope, cursor: EngineCursor, values: Vec<Value>)
        -> Result<Vec<EngineKey>, EngineRefusal>
    {
        if cursor.through < 0 { return Err(invalid("negative cursor")); }
        // Decode the entire cut before changing held state.
        let rows: Vec<_> = values.into_iter().map(|value| decode(&scope, value)).collect::<Result<_, _>>()?;
        let keys: HashSet<_> = rows.iter().map(|row| row.key.clone()).collect();
        if keys.len() != rows.len() { return Err(invalid("duplicate display identity")); }
        let mut changed = self.scopes.insert(scope.clone(), keys.clone()).unwrap_or_default();
        changed.extend(keys);
        for row in rows {
            let key = row.key.clone();
            if self.items.get(&key).is_none_or(|held| held.version <= row.version) {
                if self.pending.get(&key).is_some_and(|version| *version <= row.version) {
                    self.pending.remove(&key);
                }
                self.items.insert(key, row);
            }
        }
        self.items.retain(|key, _| self.scopes.values().any(|keys| keys.contains(key)));
        self.pending.retain(|key, _| self.scopes.values().any(|keys| keys.contains(key)));
        self.cursors.insert(scope, cursor);
        Ok(changed.into_iter().collect())
    }

    pub fn events(&mut self, scope: &EngineScope, cursor: EngineCursor, events: &[Value])
        -> Result<Vec<EngineKey>, EngineRefusal>
    {
        let Some(held) = self.cursors.get(scope) else { return Err(invalid("events before snapshot")); };
        if cursor.through <= held.through { return Ok(Vec::new()); }
        let mut changes = HashMap::<EngineKey, i64>::new();
        for event in events {
            if event["machine"].as_str() != Some(scope.machine.as_str()) {
                return Err(invalid("event from another source"));
            }
            let seq = event["seq"].as_i64().ok_or_else(|| invalid("missing event cursor"))?;
            if seq <= held.through || seq > cursor.through { return Err(invalid("event outside page")); }
            let key = EngineKey {
                machine: scope.machine.clone(),
                kind: event["entityKind"].as_str().ok_or_else(|| invalid("missing event kind"))?.into(),
                id: event["entityId"].as_str().ok_or_else(|| invalid("missing event identity"))?.into(),
            };
            let version = event["version"].as_i64().filter(|v| *v >= 0).ok_or_else(|| invalid("missing event version"))?;
            changes.entry(key).and_modify(|held| *held = (*held).max(version)).or_insert(version);
        }
        for (key, version) in &changes {
            self.pending.entry(key.clone()).and_modify(|held| *held = (*held).max(*version)).or_insert(*version);
        }
        self.cursors.insert(scope.clone(), cursor);
        Ok(changes.into_keys().collect())
    }
}

fn decode(scope: &EngineScope, value: Value) -> Result<EngineItem, EngineRefusal> {
    if value["machine"].as_str() != Some(scope.machine.as_str()) { return Err(invalid("display row from another source")); }
    if serde_json::to_vec(&value).map_err(|_| invalid("invalid display row"))?.len() > 32 * 1024 {
        return Err(invalid("display row exceeds 32 KiB"));
    }
    let key = EngineKey {
        machine: scope.machine.clone(),
        kind: value["entityKind"].as_str().ok_or_else(|| invalid("missing display kind"))?.into(),
        id: value["id"].as_str().ok_or_else(|| invalid("missing display identity"))?.into(),
    };
    let version = value["version"].as_i64().filter(|v| *v >= 0).ok_or_else(|| invalid("missing display version"))?;
    Ok(EngineItem { key, version, value })
}
fn invalid(message: &str) -> EngineRefusal { EngineRefusal { code: "invalid_engine_page".into(), message: message.into() } }
