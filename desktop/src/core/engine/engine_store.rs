//! Complete snapshots replace only their source and scope. Sparse event data is
//! an invalidation, not permission to fabricate current display fields.
use super::{EngineCursor, EngineItem, EngineKey, EngineRefusal, EngineScope};
use serde_json::Value;
use std::collections::{HashMap, HashSet};

#[derive(Default)]
pub struct EngineStore {
    items: HashMap<EngineKey, EngineItem>,
    scopes: HashMap<EngineScope, HashSet<EngineKey>>,
    cursors: HashMap<EngineScope, EngineCursor>,
}

impl EngineStore {
    pub fn item(&self, key: &EngineKey) -> Option<&EngineItem> {
        self.items.get(key)
    }
    pub fn items(&self, scope: &EngineScope) -> Vec<EngineItem> {
        self.scopes
            .get(scope)
            .into_iter()
            .flatten()
            .filter_map(|key| self.items.get(key).cloned())
            .collect()
    }
    pub fn cursor(&self, scope: &EngineScope) -> Option<&EngineCursor> {
        self.cursors.get(scope)
    }

    pub fn snapshot(
        &mut self,
        scope: EngineScope,
        cursor: EngineCursor,
        values: Vec<Value>,
    ) -> Result<Vec<EngineKey>, EngineRefusal> {
        if cursor.through < 0 {
            return Err(invalid("negative cursor"));
        }
        // Decode the entire cut before changing held state.
        let rows: Vec<_> = values
            .into_iter()
            .map(|value| decode(&scope, value))
            .collect::<Result<_, _>>()?;
        let keys: HashSet<_> = rows.iter().map(|row| row.key.clone()).collect();
        if keys.len() != rows.len() {
            return Err(invalid("duplicate display identity"));
        }
        let mut changed = self
            .scopes
            .insert(scope.clone(), keys.clone())
            .unwrap_or_default();
        changed.extend(keys);
        for row in rows {
            let key = row.key.clone();
            if self
                .items
                .get(&key)
                .is_none_or(|held| held.version <= row.version)
            {
                self.items.insert(key, row);
            }
        }
        self.items
            .retain(|key, _| self.scopes.values().any(|keys| keys.contains(key)));
        self.cursors.insert(scope, cursor);
        Ok(changed.into_iter().collect())
    }

    /// Validate the event cut before making any display request or advancing its cursor.
    pub fn event_refs(
        &self,
        scope: &EngineScope,
        cursor: &EngineCursor,
        events: &[Value],
    ) -> Result<Vec<EngineKey>, EngineRefusal> {
        Ok(self
            .event_changes(scope, cursor, events)?
            .into_keys()
            .collect())
    }

    /// A batch cursor is not a stream cursor. Commit the event cut only after every
    /// bounded display read has completed and its source, identity and version match.
    pub fn apply_event_rows(
        &mut self,
        scope: &EngineScope,
        cursor: EngineCursor,
        events: &[Value],
        values: Vec<Value>,
    ) -> Result<Vec<EngineKey>, EngineRefusal> {
        let changes = self.event_changes(scope, &cursor, events)?;
        if self
            .cursors
            .get(scope)
            .is_some_and(|held| cursor.through <= held.through)
        {
            return Ok(Vec::new());
        }
        let rows = values
            .into_iter()
            .map(|value| decode(scope, value))
            .collect::<Result<Vec<_>, _>>()?;
        let mut present = HashSet::new();
        for row in &rows {
            let Some(version) = changes.get(&row.key) else {
                return Err(invalid("unrequested display identity"));
            };
            if row.version < *version {
                return Err(invalid("display is behind the event version"));
            }
            if !present.insert(row.key.clone()) {
                return Err(invalid("duplicate display identity"));
            }
        }
        // No fallible work remains: publish the whole cut together.
        let membership = self.scopes.entry(scope.clone()).or_default();
        for key in changes.keys() {
            if present.contains(key) {
                membership.insert(key.clone());
            } else {
                membership.remove(key);
            }
        }
        for row in rows {
            if self
                .items
                .get(&row.key)
                .is_none_or(|held| held.version <= row.version)
            {
                self.items.insert(row.key.clone(), row);
            }
        }
        self.items
            .retain(|key, _| self.scopes.values().any(|keys| keys.contains(key)));
        self.cursors.insert(scope.clone(), cursor);
        Ok(changes.into_keys().collect())
    }

    fn event_changes(
        &self,
        scope: &EngineScope,
        cursor: &EngineCursor,
        events: &[Value],
    ) -> Result<HashMap<EngineKey, i64>, EngineRefusal> {
        let Some(held) = self.cursors.get(scope) else {
            return Err(invalid("events before snapshot"));
        };
        if cursor.through <= held.through {
            return Ok(HashMap::new());
        }
        let mut changes = HashMap::<EngineKey, i64>::new();
        for event in events {
            if event["machine"].as_str() != Some(scope.machine.as_str()) {
                return Err(invalid("event from another source"));
            }
            let seq = event["seq"]
                .as_i64()
                .ok_or_else(|| invalid("missing event cursor"))?;
            if seq <= held.through || seq > cursor.through {
                return Err(invalid("event outside page"));
            }
            let key = EngineKey {
                machine: scope.machine.clone(),
                kind: event["entityKind"]
                    .as_str()
                    .ok_or_else(|| invalid("missing event kind"))?
                    .into(),
                id: event["entityId"]
                    .as_str()
                    .ok_or_else(|| invalid("missing event identity"))?
                    .into(),
            };
            let version = event["version"]
                .as_i64()
                .filter(|v| *v >= 0)
                .ok_or_else(|| invalid("missing event version"))?;
            changes
                .entry(key)
                .and_modify(|held| *held = (*held).max(version))
                .or_insert(version);
        }
        Ok(changes)
    }
}

fn decode(scope: &EngineScope, value: Value) -> Result<EngineItem, EngineRefusal> {
    if value["machine"].as_str() != Some(scope.machine.as_str()) {
        return Err(invalid("display row from another source"));
    }
    if serde_json::to_vec(&value)
        .map_err(|_| invalid("invalid display row"))?
        .len()
        > 32 * 1024
    {
        return Err(invalid("display row exceeds 32 KiB"));
    }
    let key = EngineKey {
        machine: scope.machine.clone(),
        kind: value["entityKind"]
            .as_str()
            .ok_or_else(|| invalid("missing display kind"))?
            .into(),
        id: value["id"]
            .as_str()
            .ok_or_else(|| invalid("missing display identity"))?
            .into(),
    };
    let version = value["version"]
        .as_i64()
        .filter(|v| *v >= 0)
        .ok_or_else(|| invalid("missing display version"))?;
    Ok(EngineItem {
        key,
        version,
        value,
    })
}
fn invalid(message: &str) -> EngineRefusal {
    EngineRefusal {
        code: "invalid_engine_page".into(),
        message: message.into(),
    }
}
