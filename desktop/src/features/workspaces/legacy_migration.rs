//! First-cutover metadata is only pruned after exact owning workspace snapshots are confirmed.
use crate::core::state::workspace_panes::WorkspaceKey;
use crate::features::files::file_view::LegacyFileCapture;
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};
#[derive(Clone, Debug)]
pub(super) struct Group {
    pub key: WorkspaceKey,
    pub snapshot: Value,
}
pub(super) struct Plan {
    pub captures: Vec<LegacyFileCapture>,
    pub groups: Vec<Group>,
}
#[derive(Debug, PartialEq, Eq)]
pub(super) enum Canonical {
    Missing,
    Confirmed(u64),
}
pub(super) fn canonical(
    revision: u64,
    stored: Option<&Value>,
    planned: &Value,
) -> Result<Canonical, String> {
    match (revision,stored) {
        (0,None)=>Ok(Canonical::Missing),
        (revision,Some(stored)) if revision>0&&stored==planned=>Ok(Canonical::Confirmed(revision)),
        _=>Err("An existing canonical workspace differs from the old file tabs. Both are preserved; migration did not overwrite it.".into()),
    }
}
pub(super) fn prepare(
    captures: Vec<LegacyFileCapture>,
    resolved: &HashMap<String, WorkspaceKey>,
) -> Result<Plan, String> {
    let mut aliases = HashSet::new();
    let mut paths = HashSet::new();
    let mut groups: Vec<Group> = Vec::new();
    for captured in &captures {
        if captured.legacy_session.is_empty()
            || captured.path.is_empty()
            || !aliases.insert((&captured.legacy_session, &captured.path))
        {
            return Err("The old file tabs contain missing or duplicate references. Their metadata remains unchanged.".into());
        }
        let key = captured
            .owner
            .as_ref()
            .or_else(|| resolved.get(&captured.legacy_session))
            .ok_or(
                "An old file tab has no resolved owning checkout. Its metadata remains unchanged.",
            )?;
        if key.machine.is_empty() || key.session_id.is_empty() || key.checkout_root.is_empty() {
            return Err("The owning checkout is unavailable.".into());
        }
        if let Some(document) = &captured.document
            && (document.machine != key.machine
                || document.checkout_root != key.checkout_root
                || document.path != captured.path
                || captured.version.is_none())
        {
            return Err("An old file's current document contradicts its resolved owning checkout or version.".into());
        }
        if captured.entry["path"] != captured.path
            || !paths.insert((key.clone(), captured.path.clone()))
        {
            return Err("Old file aliases collide in one canonical workspace. Their metadata remains unchanged.".into());
        }
        let index = groups
            .iter()
            .position(|group| &group.key == key)
            .unwrap_or_else(|| {
                groups.push(Group {
                    key: key.clone(),
                    snapshot: json!({"version":1,"files":[],"activePath":null}),
                });
                groups.len() - 1
            });
        let snapshot = &mut groups[index].snapshot;
        snapshot["files"]
            .as_array_mut()
            .expect("files metadata")
            .push(captured.entry.clone());
        if captured.active {
            snapshot["activePath"] = json!(captured.path);
        }
    }
    Ok(Plan { captures, groups })
}
#[cfg(test)]
#[path = "legacy_migration_tests.rs"]
mod tests;
