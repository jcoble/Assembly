//! One registry of pane-owned view codecs. It captures metadata and drafts, never operational state.
use gpui_kit::*;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::BTreeMap;
use std::io::{self, Write};
use std::rc::Rc;

#[derive(Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkspaceKey {
    pub machine: String,
    pub session_id: String,
    pub checkout_root: String,
}

pub const MAX_SNAPSHOT_BYTES: usize = 3 * 1024 * 1024;
pub trait PaneCodec: 'static {
    fn name(&self) -> &'static str;
    /// Files returns {files, activePath}; other codecs return their named field's value.
    fn capture(&self, window: &Window, cx: &App) -> Result<Value, String>;
    fn validate(
        &self,
        value: &Value,
        key: &WorkspaceKey,
        snapshot: &Value,
        cx: &App,
    ) -> Result<(), String>;
    fn restore(&self, value: &Value, window: &mut Window, cx: &mut App);
    fn validate_deactivate(&self, _: &App) -> Result<(), String> {
        Ok(())
    }
    /// Releases checkout-owned panes after a validated transcript-only commit; Chat keeps its staged rows.
    fn deactivate(&self, _: &mut Window, _: &mut App) {}
}
#[derive(Default)]
pub struct WorkspacePanes {
    key: Option<WorkspaceKey>,
    restoring: bool,
    failure: Option<String>,
    codecs: BTreeMap<&'static str, Rc<dyn PaneCodec>>,
}
impl Global for WorkspacePanes {}
impl WorkspacePanes {
    pub fn key(cx: &App) -> Option<&WorkspaceKey> {
        cx.try_global::<Self>()?.key.as_ref()
    }
    pub fn set_key(key: WorkspaceKey, cx: &mut App) {
        if !cx.has_global::<Self>() {
            cx.set_global(Self::default());
        }
        cx.global_mut::<Self>().key = Some(key);
    }
    pub fn clear_key(cx: &mut App) {
        if cx.has_global::<Self>() {
            cx.global_mut::<Self>().key = None;
        }
    }
    pub fn validate_snapshot(snapshot: &Value, key: &WorkspaceKey, cx: &App) -> Result<(), String> {
        validate(snapshot)?;
        if let Some(panes) = cx.try_global::<Self>() {
            for (&name, codec) in &panes.codecs {
                codec.validate(&codec_value(name, snapshot), key, snapshot, cx)?;
            }
        }
        Ok(())
    }
    pub fn is_restoring(cx: &App) -> bool {
        cx.try_global::<Self>().is_some_and(|panes| panes.restoring)
    }
    pub fn refuse_restore(message: String, cx: &mut App) {
        cx.global_mut::<Self>().failure = Some(message);
    }
    pub fn register(codec: Box<dyn PaneCodec>, cx: &mut App) -> Result<(), String> {
        let name = codec.name();
        if !matches!(
            name,
            "files"
                | "tree"
                | "chat"
                | "topTabs"
                | "drawerTab"
                | "diff"
                | "browser"
                | "sourceControl"
        ) {
            return Err("Unknown workspace pane.".into());
        }
        if !cx.has_global::<Self>() {
            cx.set_global(Self::default());
        }
        let panes = cx.global_mut::<Self>();
        if panes.codecs.contains_key(name) {
            return Err(format!("The {name} workspace codec is already registered."));
        }
        panes.codecs.insert(name, Rc::from(codec));
        Ok(())
    }
    pub fn capture(window: &Window, cx: &App) -> Result<Value, String> {
        let mut snapshot = Map::new();
        snapshot.insert("version".into(), Value::from(1));
        if let Some(panes) = cx.try_global::<Self>() {
            for (&name, codec) in &panes.codecs {
                let value = codec.capture(window, cx)?;
                if name == "files" {
                    let Some(fields) = value.as_object() else {
                        return Err("The files workspace could not be captured.".into());
                    };
                    if fields
                        .keys()
                        .any(|key| !matches!(key.as_str(), "files" | "activePath"))
                    {
                        return Err("Unexpected files workspace fields.".into());
                    }
                    snapshot.extend(fields.clone());
                } else {
                    snapshot.insert(name.into(), value);
                }
            }
        }
        let snapshot = Value::Object(snapshot);
        if let Some(key) = Self::key(cx) {
            Self::validate_snapshot(&snapshot, key, cx)?;
        } else {
            validate(&snapshot)?;
        }
        Ok(snapshot)
    }
    pub fn validate_deactivate(cx: &App) -> Result<(), String> {
        if let Some(panes) = cx.try_global::<Self>() {
            for codec in panes.codecs.values() {
                codec.validate_deactivate(cx)?;
            }
        }
        Ok(())
    }
    pub fn deactivate(window: &mut Window, cx: &mut App) {
        if !cx.has_global::<Self>() {
            return;
        }
        cx.global_mut::<Self>().restoring = true;
        let codecs: Vec<_> = cx.global::<Self>().codecs.values().cloned().collect();
        for codec in codecs {
            codec.deactivate(window, cx);
        }
        cx.global_mut::<Self>().restoring = false;
    }
    pub fn restore(snapshot: &Value, window: &mut Window, cx: &mut App) -> Result<(), String> {
        let key = Self::key(cx).ok_or("The workspace checkout identity is unavailable.")?;
        Self::validate_snapshot(snapshot, key, cx)?;
        if !cx.has_global::<Self>() {
            cx.set_global(Self::default());
        }
        cx.global_mut::<Self>().failure = None;
        cx.global_mut::<Self>().restoring = true;
        // Release the global borrow before a codec restores its feature's real model.
        let codecs: Vec<_> = cx
            .try_global::<Self>()
            .map(|panes| {
                panes
                    .codecs
                    .iter()
                    .map(|(&name, codec)| (name, codec.clone()))
                    .collect()
            })
            .unwrap_or_default();
        for (name, codec) in codecs {
            codec.restore(&codec_value(name, snapshot), window, cx);
        }
        cx.global_mut::<Self>().restoring = false;
        if let Some(failure) = cx.global_mut::<Self>().failure.take() {
            return Err(failure);
        }
        Ok(())
    }
}
fn codec_value(name: &str, snapshot: &Value) -> Value {
    if name == "files" {
        let mut fields = Map::new();
        fields.insert(
            "files".into(),
            snapshot
                .get("files")
                .cloned()
                .unwrap_or_else(|| Value::Array(Vec::new())),
        );
        fields.insert(
            "activePath".into(),
            snapshot.get("activePath").cloned().unwrap_or(Value::Null),
        );
        Value::Object(fields)
    } else {
        snapshot.get(name).cloned().unwrap_or(Value::Null)
    }
}
fn validate(snapshot: &Value) -> Result<(), String> {
    let Some(fields) = snapshot.as_object() else {
        return Err("Couldn't restore this workspace.".into());
    };
    if fields.get("version").and_then(Value::as_u64) != Some(1)
        || fields.keys().any(|key| {
            !matches!(
                key.as_str(),
                "version"
                    | "files"
                    | "activePath"
                    | "tree"
                    | "chat"
                    | "topTabs"
                    | "drawerTab"
                    | "diff"
                    | "browser"
                    | "sourceControl"
            )
        })
    {
        return Err("Couldn't restore this workspace.".into());
    }
    serde_json::to_writer(BoundedJson(0), snapshot)
        .map_err(|_| "This workspace is larger than 3 MiB, so it was not saved.".to_string())
}
struct BoundedJson(usize);
impl Write for BoundedJson {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if bytes.len() > MAX_SNAPSHOT_BYTES - self.0 {
            return Err(io::Error::other("workspace size limit"));
        }
        self.0 += bytes.len();
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
#[cfg(test)]
#[path = "workspace_panes_tests.rs"]
mod tests;
