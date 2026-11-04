//! Complete snapshots replace only their source and scope. Sparse event data is
//! an invalidation, not permission to fabricate current display fields.
use super::{EngineCursor, EngineItem, EngineKey, EngineRefusal, EngineScope, SessionKey};
use serde_json::Value;
use std::collections::{HashMap, HashSet};

#[derive(Default)]
pub struct EngineStore {
    items: HashMap<EngineKey, EngineItem>,
    scopes: HashMap<EngineScope, HashSet<EngineKey>>,
    cursors: HashMap<EngineScope, EngineCursor>,
    needs_by_session: HashMap<SessionKey, HashSet<EngineKey>>,
    children_by_parent: HashMap<SessionKey, HashSet<EngineKey>>,
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
    /// Index references only: display facts remain solely in items.
    pub fn needs_for(&self, session: &SessionKey) -> Vec<&EngineItem> {
        self.needs_by_session
            .get(session)
            .into_iter()
            .flatten()
            .filter_map(|key| self.items.get(key))
            .collect()
    }
    /// Parentage uses the owning child's exported parentMachine/parentId pair.
    /// Missing legacy ownership remains unknown; no ID or saved-name fallback exists.
    pub fn children_of(&self, parent: &SessionKey) -> Vec<&EngineItem> {
        let parent_key = EngineKey {
            machine: parent.machine.clone(),
            kind: "session".into(),
            id: parent.id.clone(),
        };
        if !self.items.contains_key(&parent_key) {
            return Vec::new();
        }
        self.children_by_parent
            .get(parent)
            .into_iter()
            .flatten()
            .filter_map(|key| self.items.get(key))
            .collect()
    }
    #[cfg(test)]
    pub fn parent_of(&self, child: &SessionKey) -> Option<SessionKey> {
        let child_key = EngineKey {
            machine: child.machine.clone(),
            kind: "session".into(),
            id: child.id.clone(),
        };
        let parent = self.session_parent(&child_key)?;
        let key = EngineKey {
            machine: parent.machine.clone(),
            kind: "session".into(),
            id: parent.id.clone(),
        };
        self.items.contains_key(&key).then_some(parent)
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
            .map(|value| decode(&scope.machine, value))
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
                self.install(row);
            }
        }
        self.prune_items();
        self.cursors.insert(scope, cursor);
        Ok(changed.into_iter().collect())
    }

    /// A direct checked read does not own a stream cut or create cache membership.
    pub fn apply_entity(
        &mut self,
        key: &EngineKey,
        values: Vec<Value>,
    ) -> Result<Option<EngineItem>, EngineRefusal> {
        if values.len() > 1 {
            return Err(invalid("duplicate display identity"));
        }
        let Some(value) = values.into_iter().next() else {
            self.unindex(key);
            self.items.remove(key);
            for membership in self.scopes.values_mut() {
                membership.remove(key);
            }
            return Ok(None);
        };
        let row = decode(&key.machine, value)?;
        if row.key != *key {
            return Err(invalid("unrequested display identity"));
        }
        if self
            .items
            .get(key)
            .is_some_and(|held| held.version > row.version)
        {
            return Err(invalid("display is behind the held version"));
        }
        if self.items.contains_key(key) {
            self.install(row.clone());
        }
        Ok(Some(row))
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
            .map(|value| decode(&scope.machine, value))
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
                self.install(row);
            }
        }
        self.prune_items();
        self.cursors.insert(scope.clone(), cursor);
        Ok(changes.into_keys().collect())
    }

    fn need_sessions(&self, key: &EngineKey) -> Vec<SessionKey> {
        if key.kind != "needs_me" {
            return Vec::new();
        }
        let Some(row) = self.items.get(key) else {
            return Vec::new();
        };
        let mut sessions = Vec::new();
        if let Some(id) = row.value["sessionId"].as_str().filter(|id| !id.is_empty()) {
            sessions.push(SessionKey {
                machine: key.machine.clone(),
                id: id.into(),
            });
        }
        // The need owner need not own the addressee. A scalar ID grants no relation.
        if let (Some(machine), Some(id)) = (
            row.value["addresseeMachine"]
                .as_str()
                .filter(|v| !v.is_empty()),
            row.value["addresseeSessionId"]
                .as_str()
                .filter(|v| !v.is_empty()),
        ) {
            let session = SessionKey {
                machine: machine.into(),
                id: id.into(),
            };
            if !sessions.contains(&session) {
                sessions.push(session);
            }
        }
        sessions
    }
    fn session_parent(&self, key: &EngineKey) -> Option<SessionKey> {
        if key.kind != "session" {
            return None;
        }
        let row = self.items.get(key)?;
        let id = row.value["parentId"].as_str().filter(|id| !id.is_empty())?;
        let machine = row.value["parentMachine"]
            .as_str()
            .filter(|machine| !machine.is_empty())?;
        Some(SessionKey {
            machine: machine.into(),
            id: id.into(),
        })
    }
    fn unindex(&mut self, key: &EngineKey) {
        if let Some(parent) = self.session_parent(key)
            && let Some(keys) = self.children_by_parent.get_mut(&parent)
        {
            keys.remove(key);
            if keys.is_empty() {
                self.children_by_parent.remove(&parent);
            }
        }
        for session in self.need_sessions(key) {
            if let Some(keys) = self.needs_by_session.get_mut(&session) {
                keys.remove(key);
                if keys.is_empty() {
                    self.needs_by_session.remove(&session);
                }
            }
        }
    }
    fn install(&mut self, row: EngineItem) {
        let key = row.key.clone();
        self.unindex(&key);
        self.items.insert(key.clone(), row);
        for session in self.need_sessions(&key) {
            self.needs_by_session
                .entry(session)
                .or_default()
                .insert(key.clone());
        }
        if let Some(parent) = self.session_parent(&key) {
            self.children_by_parent
                .entry(parent)
                .or_default()
                .insert(key);
        }
    }
    fn prune_items(&mut self) {
        let removed: Vec<_> = self
            .items
            .keys()
            .filter(|key| !self.scopes.values().any(|keys| keys.contains(*key)))
            .cloned()
            .collect();
        for key in removed {
            self.unindex(&key);
            self.items.remove(&key);
        }
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

fn decode(machine: &str, value: Value) -> Result<EngineItem, EngineRefusal> {
    if value["machine"].as_str() != Some(machine) {
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
        machine: machine.into(),
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
