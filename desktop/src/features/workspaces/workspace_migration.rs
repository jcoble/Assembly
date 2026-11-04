//! One-time migration through the existing real owner resolver and canonical workspace CAS calls.
use super::{Outcome, Read, SocketClient, WorkspaceCoordinator, WorkspaceKey, WorkspacePanes};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::files::{
    entry::FileOwnership,
    file_view::{FileView, LegacyFileCapture},
};
use crate::features::workspaces::legacy_migration;
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;
use std::collections::{HashMap, HashSet};
#[derive(Deserialize)]
struct SavedMany {
    saved: Vec<SavedEntry>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SavedEntry {
    machine: String,
    session_id: String,
    checkout_root: String,
    revision: u64,
}
pub(super) struct Receipt {
    key: WorkspaceKey,
    revision: u64,
}
fn confirmed(saved: &SavedMany, groups: &[legacy_migration::Group], expected: &[u64]) -> bool {
    saved.saved.len() == groups.len()
        && saved
            .saved
            .iter()
            .zip(groups)
            .zip(expected)
            .all(|((saved, group), expected)| {
                saved.machine == group.key.machine
                    && saved.session_id == group.key.session_id
                    && saved.checkout_root == group.key.checkout_root
                    && expected.checked_add(1) == Some(saved.revision)
            })
}
pub(super) struct Completed {
    captures: Vec<LegacyFileCapture>,
    receipts: Vec<Receipt>,
}
fn matches_current(captures: &[LegacyFileCapture], cx: &App) -> bool {
    let views = CenterPanes::file_views(cx);
    views.len() == captures.len()
        && views
            .iter()
            .zip(captures)
            .all(|(view, captured)| view.read(cx).legacy_matches(captured, cx))
}
impl Completed {
    /// Read-only preflight; not permission to activate or persist before the joint navigator/registry cutover.
    pub(super) fn pruned_layout(
        &self,
        state: &gpui_kit::component::dock::DockAreaState,
        cx: &App,
    ) -> Result<gpui_kit::component::dock::DockAreaState, String> {
        if !self.still_current(cx) {
            return Err("Newer file metadata or drafts prevent old layout pruning.".into());
        }
        crate::features::workspaces::legacy_layout::prune(state, &self.captures)
    }
    fn still_current(&self, cx: &App) -> bool {
        !self.receipts.is_empty()
            && self
                .receipts
                .iter()
                .all(|receipt| receipt.revision > 0 && !receipt.key.machine.is_empty())
            && matches_current(&self.captures, cx)
    }
}
impl WorkspaceCoordinator {
    pub(super) fn admit_legacy_cutover(&self, cx: &App) -> Result<(), String> {
        let Some(completed) = &self.migrated else {
            return Ok(());
        };
        let dock = CenterPanes::dock(cx).ok_or("The old file layout is unavailable.")?;
        completed
            .pruned_layout(&dock.read(cx).dump(cx), cx)
            .map(|_| ())
    }
    pub(super) fn migrate_legacy(&mut self, window: &mut Window, cx: &mut Context<Self>) -> bool {
        let views = CenterPanes::file_views(cx);
        if views.is_empty() {
            return true;
        }
        if self
            .migrated
            .as_ref()
            .is_some_and(|completed| completed.still_current(cx))
        {
            return true;
        }
        self.migrated = None;
        if self.migrating.is_some() {
            return false;
        }
        let captures = match views
            .iter()
            .map(|view| view.read(cx).legacy_capture(cx))
            .collect::<Result<Vec<_>, _>>()
        {
            Ok(captures) => captures,
            Err(error) => {
                self.refuse(format!("The old file tabs were not migrated: {error}"), cx);
                return false;
            }
        };
        let Some(bulk) = SocketClient::try_bulk(cx) else {
            self.refuse("The old file tabs were not migrated: the bulk connection is unavailable. Their metadata remains unchanged.".into(),cx);
            return false;
        };
        let primary = SocketClient::try_global(cx);
        let handle = window.window_handle();
        self.migrating=Some(cx.spawn(async move |view,cx| {
            let result:Result<Completed,String>=async {
                let mut resolved=HashMap::new();let mut sessions=HashSet::new();
                for captured in &captures {
                    if captured.owner.is_some()||!sessions.insert(captured.legacy_session.clone()){continue;}
                    let primary=primary.as_ref().ok_or("The owning connection is unavailable.")?;
                    let call=view.update(cx,|_,cx|primary.read(cx).call::<FileOwnership>("files","owner",json!({"session":captured.legacy_session}),None))
                        .map_err(|_|"The workspace view closed.")?;
                    let owner=match call.await {Outcome::Ok(owner)=>owner,Outcome::Failed(error)=>return Err(error.message),Outcome::Aborted=>return Err("The owning connection closed.".into())};
                    resolved.insert(captured.legacy_session.clone(),WorkspaceKey {machine:owner.machine,session_id:owner.remote_session_id,checkout_root:owner.checkout_root});
                }
                let plan=legacy_migration::prepare(captures,&resolved)?;
                view.update(cx,|_,cx| {
                    if !matches_current(&plan.captures,cx){return Err("The old file tabs or drafts changed while resolving their owners.".to_string());}
                    // Validate every group before the first canonical write; no partial collision or schema admission.
                    for group in &plan.groups {
                        FileView::validate_legacy_snapshot(&group.snapshot,&group.key,cx)?;
                        WorkspacePanes::validate_snapshot(&group.snapshot,&group.key,cx)?;
                    }Ok(())
                }).map_err(|_|"The workspace view closed.")??;
                let mut expected=Vec::with_capacity(plan.groups.len());
                for group in &plan.groups {
                    if !view.update(cx,|view,_|view.active_request).map_err(|_|"The workspace view closed.")? {return Err("The workspace open was cancelled; old metadata remains unchanged.".into());}
                    let args=json!({"machine":group.key.machine,"sessionId":group.key.session_id,"checkoutRoot":group.key.checkout_root});
                    let read=view.update(cx,|_,cx|bulk.read(cx).call::<Read>("workspaces","read",args,None)).map_err(|_|"The workspace view closed.")?;
                    let read=match read.await {Outcome::Ok(read)=>read,Outcome::Failed(error)=>return Err(error.message),Outcome::Aborted=>return Err("The workspace read was interrupted.".into())};
                    // Detect conflicts now, without mutating any canonical workspace independently.
                    let revision=match legacy_migration::canonical(read.revision,read.snapshot.as_ref(),&group.snapshot)? {
                        legacy_migration::Canonical::Missing=>0,
                        legacy_migration::Canonical::Confirmed(revision)=>revision,
                    };
                    revision.checked_add(1).ok_or("The workspace revision cannot be advanced.")?;
                    expected.push(revision);
                }
                view.update(cx,|view,cx| {
                    if !view.active_request||!matches_current(&plan.captures,cx) {return Err("The workspace open or its old drafts changed before migration.".to_string());}
                    Ok(())
                }).map_err(|_|"The workspace view closed.")??;
                // All groups, including identical existing snapshots, participate in ONE writer transaction.
                // The server's generated/default-escaped total-args budget is authoritative; raw wire size is no guarantee.
                let saves:Vec<_>=plan.groups.iter().zip(&expected).map(|(group,expected)|json!({
                    "machine":group.key.machine,"sessionId":group.key.session_id,"checkoutRoot":group.key.checkout_root,
                    "expectedRevision":expected,"snapshot":group.snapshot})).collect();
                let write=view.update(cx,|_,cx|bulk.read(cx).call::<SavedMany>("workspaces","save_many",json!({"saves":saves}),None))
                    .map_err(|_|"The workspace view closed.")?;
                match write.await {
                    Outcome::Ok(saved) if confirmed(&saved,&plan.groups,&expected)=>{},
                    outcome=>{
                        let message=match outcome {Outcome::Failed(error)=>error.message,_=>"The migration batch result is unknown.".into()};
                        // Never replay an uncertain atomic write. Only exact next revisions AND captured bytes confirm it.
                        for (group,expected) in plan.groups.iter().zip(&expected) {
                            let read=view.update(cx,|_,cx|bulk.read(cx).call::<Read>("workspaces","read",json!({
                                "machine":group.key.machine,"sessionId":group.key.session_id,"checkoutRoot":group.key.checkout_root}),None))
                                .map_err(|_|"The workspace view closed.")?;
                            match read.await {
                                Outcome::Ok(read) if read.revision==expected+1&&read.snapshot.as_ref()==Some(&group.snapshot)=>{},
                                _=>return Err(format!("The migration batch was not confirmed: {message}. All old tabs remain open.")),
                            }
                        }
                    }
                }
                let receipts=plan.groups.into_iter().zip(expected).map(|(group,expected)|Receipt {key:group.key,revision:expected+1}).collect();
                Ok(Completed {captures:plan.captures,receipts})
            }.await;
            let _=handle.update(cx,|_,window,cx|view.update(cx,|view,cx| {
                view.migrating=None;
                match result {
                    Ok(completed) if completed.still_current(cx)=>{view.migrated=Some(completed);if view.active_request {view.begin_transition(window,cx);}}
                    Ok(_)=>view.refuse("The old file tabs were not migrated for departure: newer view state or drafts remain open.".into(),cx),
                    Err(error)=>view.refuse(format!("The old file tabs were not migrated: {error}"),cx),
                }cx.notify();
            }));
        }));
        false
    }
}

#[cfg(test)]
#[path = "workspace_migration_tests.rs"]
mod tests;
