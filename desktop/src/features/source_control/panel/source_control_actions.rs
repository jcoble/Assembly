//! The pane's writes. Each is decided against what the pane shows and goes through the one write authority.
use super::{BranchPicked, MenuAction, RunningOperation, SourceControlPane};
use crate::core::intents::open_git_history::OpenGitHistory;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::features::files::entry::FileOwnership;
use crate::features::source_control::commit_box::{amend_needs_confirm, can_commit, stageable};
use crate::features::source_control::discard_dialog::{self, DiscardRequest};
use crate::features::source_control::git_status::ChangedFile;
use crate::features::source_control::git_sync;
use crate::features::source_control::git_target::GitTarget;
use crate::features::source_control::{remote_dialogs, stash_dialogs};
use gpui_kit::component::WindowExt as _;
use gpui_kit::*;
use serde_json::{Value, json};

pub(super) const NOT_ON_THIS_DESKTOP: &str =
    "Reveal is available only for files owned by this desktop.";

impl SourceControlPane {
    /// Shows a file in this desktop's file manager. The session's owner says where its folder is; on another machine
    /// there's nothing here to show.
    pub(super) fn reveal_file(&mut self, path: String, cx: &mut Context<Self>) {
        let Some(target) = self.target(cx) else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            self.fail("The backend isn't running.", cx);
            return;
        };
        let call = socket.read(cx).call::<FileOwnership>(
            "files",
            "owner",
            json!({"session":target.session,"expectedSource":target.source,"expectedOwner":null}),
            None,
        );
        self.reveal_read = Some(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            _ = pane.update(cx, |pane, cx| {
                pane.reveal_read = None;
                let same = pane.target(cx).is_some_and(|now| {
                    now.session == target.session && now.source == target.source
                });
                if !same {
                    return;
                }
                match answer {
                    Outcome::Ok(owner) => {
                        let local = SocketClient::try_global(cx).is_some_and(|socket| {
                            socket.read(cx).engine_machine().as_deref()
                                == Some(owner.machine.as_str())
                        });
                        if local {
                            cx.reveal_path(&std::path::Path::new(&owner.checkout_root).join(&path));
                        } else {
                            pane.fail(NOT_ON_THIS_DESKTOP, cx);
                        }
                    }
                    Outcome::Failed(failure) => pane.fail(&failure.message, cx),
                    Outcome::Aborted => pane.fail("The connection dropped.", cx),
                }
            });
        }));
    }
    pub(super) fn write_to(
        &mut self,
        target: GitTarget,
        method: &'static str,
        args: Value,
        cx: &mut Context<Self>,
    ) {
        if self.busy(cx) {
            return;
        }
        self.submitted_revision = (method == "commit").then_some(self.edit_revision);
        self.result = None;
        self.writes
            .action
            .update(cx, |action, cx| action.run(&target, method, args, cx));
        cx.notify();
    }
    pub(super) fn start_operation(
        &mut self,
        action: &'static str,
        args: Value,
        cx: &mut Context<Self>,
    ) {
        let Some(target) = self.target(cx) else {
            return;
        };
        if self.busy(cx) {
            return;
        }
        self.result = None;
        self.running = Some(RunningOperation {
            action,
            target: target.clone(),
            edit_revision: self.edit_revision,
        });
        self.writes.operation.update(cx, |operation, cx| {
            operation.start_target(action, &target, args, cx)
        });
        cx.notify();
    }
    pub(super) fn start_sync(
        &mut self,
        action: &'static str,
        remote: Option<String>,
        cx: &mut Context<Self>,
    ) {
        let status = self.shown_status(cx);
        let remotes = self
            .branches
            .as_ref()
            .map(|list| list.remotes.clone())
            .unwrap_or_default();
        match git_sync::request(action, status.as_ref(), remote.as_deref(), &remotes) {
            Ok(args) => self.start_operation(action, args, cx),
            Err(reason) => self.fail(&reason, cx),
        }
    }
    pub(super) fn commit(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let (Some(target), Some(status)) = (self.target(cx), self.shown_status(cx)) else {
            return;
        };
        // Enter in a multi-line box has already added its newline by the time the event says so.
        let message = self.message.read(cx).value().trim_end().to_string();
        if !can_commit(Some(&status), &message, self.amend, self.busy(cx)) {
            return;
        }
        let args = json!({"session":target.session,"message":message,"amend":self.amend,"expectedHead":status.head});
        if !amend_needs_confirm(&status, self.amend) {
            self.write_to(target, "commit", args, cx);
            return;
        }
        let pane = cx.entity().downgrade();
        window.open_alert_dialog(cx, move |alert, _, _| {
            let pane = pane.clone();
            let args = args.clone();
            let target = target.clone();
            alert
                .title("Amend a pushed commit?")
                .description("This commit is already pushed. Amending it means the next push will be refused, and Assembly never force-pushes.")
                .show_cancel(true)
                .on_ok(move |_, _, cx| {
                    _ = pane.update(cx, |pane, cx| {
                        pane.write_to(target.clone(), "commit", args.clone(), cx)
                    });
                    true
                })
        });
    }
    pub(super) fn draft_message(&mut self, cx: &mut Context<Self>) {
        self.start_operation("draftCommitMessage", json!({}), cx);
    }
    pub(super) fn stage_paths(&mut self, paths: Vec<String>, cx: &mut Context<Self>) {
        if paths.is_empty() {
            return;
        }
        if let Some(target) = self.target(cx) {
            let args = json!({"session":target.session,"paths":paths});
            self.write_to(target, "stage", args, cx);
        }
    }
    pub(super) fn stage_all(&mut self, cx: &mut Context<Self>) {
        if let Some(status) = self.shown_status(cx) {
            self.stage_paths(stageable(&status), cx);
        }
    }
    pub(super) fn unstage_paths(&mut self, paths: Vec<String>, cx: &mut Context<Self>) {
        if let Some(target) = self.target(cx) {
            let args = json!({"session":target.session,"paths":paths});
            self.write_to(target, "unstage", args, cx);
        }
    }
    pub(super) fn unstage_all(&mut self, cx: &mut Context<Self>) {
        if let (Some(target), Some(status)) = (self.target(cx), self.shown_status(cx)) {
            let args = json!({"session":target.session,"expectedFingerprint":status.fingerprint});
            self.write_to(target, "unstage", args, cx);
        }
    }
    pub(super) fn ask_discard(
        &mut self,
        files: Vec<ChangedFile>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.ask_discard_request(DiscardRequest::Files(files), window, cx);
    }
    fn ask_discard_request(
        &mut self,
        request: DiscardRequest,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.busy(cx) {
            return;
        }
        if let Some(target) = self.target(cx) {
            discard_dialog::ask(request, target, self.writes.action.clone(), window, cx);
        }
    }
    pub(super) fn initialize(&mut self, cx: &mut Context<Self>) {
        if let Some(target) = self.target(cx) {
            let args = json!({"session":target.session});
            self.write_to(target, "init", args, cx);
        }
    }
    pub(super) fn menu_action(
        &mut self,
        action: MenuAction,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match action {
            MenuAction::Refresh => {
                self.read.update(cx, |read, cx| read.refresh(cx));
                self.read_branches(cx);
            }
            MenuAction::DiscardAll => {
                if let Some(status) = self.shown_status(cx) {
                    let omitted = status.total_files.saturating_sub(status.files.len());
                    self.ask_discard_request(
                        DiscardRequest::Everything {
                            fingerprint: status.fingerprint,
                            listed: status.files,
                            omitted,
                        },
                        window,
                        cx,
                    );
                }
            }
            MenuAction::Fetch => self.start_sync("fetch", None, cx),
            MenuAction::Pull => self.start_sync("pull", None, cx),
            MenuAction::Push => self.start_sync("push", None, cx),
            MenuAction::AddRemote => {
                if let Some(target) = self.target(cx) {
                    remote_dialogs::add(target, self.writes.action.clone(), window, cx);
                }
            }
            MenuAction::OpenHistory => {
                if let Some(target) = self.session_target(cx).or_else(|| self.shown_target(cx)) {
                    window.dispatch_action(
                        Box::new(OpenGitHistory {
                            expected_source: target.source,
                            session_id: target.session,
                            path: None,
                            checkout: None,
                        }),
                        cx,
                    );
                }
            }
        }
    }
    pub(super) fn set_branch_open(&mut self, open: bool, cx: &mut Context<Self>) {
        self.branch_open = open;
        if self.branch_open {
            self.read_branches(cx);
        }
        cx.notify();
    }
    pub(super) fn branch_picked(
        &mut self,
        picked: BranchPicked,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.read_only(cx) {
            return;
        }
        if !matches!(picked, BranchPicked::Create(_)) {
            self.branch_open = false;
        }
        match picked {
            BranchPicked::Switch(name) => self.switch_branch(name, cx),
            BranchPicked::MoveToFolder(folder) => self.move_to_folder(folder, cx),
            BranchPicked::Create(name) => {
                if self.viewing(cx) {
                    self.branch_open = false;
                    self.branch_menu
                        .update(cx, |menu, cx| menu.clear(window, cx));
                }
                self.create_branch(name, cx);
            }
            BranchPicked::PopStash(sha) => {
                if let Some(target) = self.target(cx) {
                    let args = json!({"session":target.session,"sha":sha});
                    self.write_to(target, "applyStash", args, cx);
                }
            }
            BranchPicked::Stash => {
                if let Some(target) = self.target(cx) {
                    let args =
                        json!({"session":target.session,"message":"","includeUntracked":true});
                    self.write_to(target, "stash", args, cx);
                }
            }
            BranchPicked::OpenStashes => {
                if let Some(target) = self.target(cx) {
                    stash_dialogs::list(target, self.writes.action.clone(), window, cx);
                }
            }
        }
        cx.notify();
    }
}
