use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::{git_operation::GitOperation, git_status::GitStatus};
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};

fn request(
    action: &str,
    status: Option<&GitStatus>,
    remote: Option<&str>,
    remotes: &[String],
) -> Result<Value, String> {
    if action == "fetch" {
        return if remotes.is_empty() {
            Err("no remote".into())
        } else {
            Ok(json!({}))
        };
    }
    let status = status.ok_or("Refresh Source control before synchronizing.")?;
    let branch = status.branch.as_ref().ok_or("detached HEAD")?;
    if status.head.is_none() {
        return Err("This branch has no commits.".into());
    }
    match action {
        "pull" | "push" if status.has_upstream => Ok(json!({"expectedBranch":branch})),
        "pull" | "push" => Err("no upstream: publish first".into()),
        "publish" => {
            let remote = remote
                .filter(|remote| remotes.iter().any(|known| known.as_str() == *remote))
                .ok_or("Choose a current repository remote.")?;
            Ok(json!({"expectedBranch":branch,"remote":remote}))
        }
        _ => Err("Unknown Git operation.".into()),
    }
}
#[derive(Deserialize)]
struct Branches {
    remotes: Vec<String>,
}
pub struct SyncCompleted;
pub struct SyncControls {
    session: String,
    active: bool,
    status: Option<GitStatus>,
    remotes: Vec<String>,
    read: Option<Task<()>>,
    revision: u64,
    operation: Entity<GitOperation>,
    finished: Option<String>,
    failure: Option<String>,
    _operation: Subscription,
}
impl EventEmitter<SyncCompleted> for SyncControls {}
impl SyncControls {
    pub fn new(session: String, active: bool, cx: &mut Context<Self>) -> Self {
        let operation = cx.new(|_| GitOperation::default());
        let heard = cx.observe(&operation, |view: &mut Self, _, cx| {
            let done = view
                .operation
                .read(cx)
                .status
                .as_ref()
                .filter(|status| status.terminal() && status.state != "uncertain")
                .map(|status| status.operation_id.clone());
            if done.is_some() && done != view.finished {
                view.finished = done;
                cx.emit(SyncCompleted);
                view.refresh_remotes(cx);
            }
            cx.notify();
        });
        let mut view = Self {
            session,
            active,
            status: None,
            remotes: Vec::new(),
            read: None,
            revision: 0,
            operation,
            finished: None,
            failure: None,
            _operation: heard,
        };
        if active {
            view.refresh_remotes(cx);
        }
        view
    }
    pub fn set_status(&mut self, status: Option<GitStatus>, cx: &mut Context<Self>) {
        self.status = status;
        cx.notify();
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        if self.active == active {
            return;
        }
        self.active = active;
        self.read = None;
        self.revision += 1;
        if active {
            self.refresh_remotes(cx);
        }
        cx.notify();
    }
    fn refresh_remotes(&mut self, cx: &mut Context<Self>) {
        if !self.active {
            return;
        }
        self.read = None;
        self.revision += 1;
        let revision = self.revision;
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let call = socket.read(cx).call::<Branches>(
            "git",
            "branches",
            json!({"session":self.session}),
            None,
        );
        self.read = Some(cx.spawn(async move |view, cx| {
            let answer = call.await;
            _ = view.update(cx, |view, cx| {
                if !view.active || view.revision != revision {
                    return;
                }
                view.read = None;
                match answer {
                    Outcome::Ok(branches) => {
                        view.remotes = branches.remotes;
                        view.failure = None;
                    }
                    Outcome::Failed(error) => view.failure = Some(error.message),
                    Outcome::Aborted => {}
                }
                cx.notify();
            });
        }));
    }
    fn run(&mut self, action: &'static str, remote: Option<&str>, cx: &mut Context<Self>) {
        if !self.active {
            return;
        }
        match request(action, self.status.as_ref(), remote, &self.remotes) {
            Ok(args) => {
                self.failure = None;
                let session = self.session.clone();
                self.operation.update(cx, |operation, cx| {
                    operation.start(action, Some(session), args, cx)
                });
            }
            Err(error) => self.failure = Some(error),
        }
        cx.notify();
    }
}
impl Render for SyncControls {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let operation = self.operation.read(cx);
        let disabled = operation.busy || operation.unconfirmed;
        let unknown = operation.unconfirmed;
        let error = operation.error.clone().or_else(|| self.failure.clone());
        let danger = cx.theme().danger;
        let mut actions = h_flex().gap_1().flex_wrap();
        for (action, label) in [("fetch", "Fetch"), ("pull", "Pull"), ("push", "Push")] {
            let reason = if disabled {
                Some(if unknown {
                    "Refresh this operation before trying again.".to_owned()
                } else {
                    "another action is running".to_owned()
                })
            } else {
                request(action, self.status.as_ref(), None, &self.remotes).err()
            };
            actions = actions.child(
                Button::new(action)
                    .ghost()
                    .compact()
                    .label(label)
                    .disabled(reason.is_some())
                    .tooltip(reason.unwrap_or_else(|| label.to_owned()))
                    .on_click(cx.listener(move |view, _, _, cx| view.run(action, None, cx))),
            );
        }
        let owner = cx.entity().downgrade();
        let remotes = self.remotes.clone();
        let publish_reason = if disabled {
            Some(if unknown {
                "Refresh this operation before trying again."
            } else {
                "another action is running"
            })
        } else if remotes.is_empty() {
            Some("no remote")
        } else {
            match &self.status {
                None => Some("Refresh Source control before synchronizing."),
                Some(status) if status.branch.is_none() => Some("detached HEAD"),
                Some(status) if status.head.is_none() => Some("This branch has no commits."),
                Some(_) => None,
            }
        };
        actions = actions.child(
            Button::new("publish-branch")
                .ghost()
                .compact()
                .label("Publish…")
                .disabled(publish_reason.is_some())
                .tooltip(publish_reason.unwrap_or("Publish branch"))
                .dropdown_menu(move |menu, _, _| {
                    remotes.iter().fold(menu, |menu, remote| {
                        let target = owner.clone();
                        let remote = remote.clone();
                        menu.item(
                            PopupMenuItem::new(remote.clone()).on_click(move |_, _, cx| {
                                _ = target
                                    .update(cx, |view, cx| view.run("publish", Some(&remote), cx));
                            }),
                        )
                    })
                }),
        );
        if operation.busy && !unknown {
            actions = actions.child(
                Button::new("sync-cancel")
                    .ghost()
                    .compact()
                    .label("Cancel")
                    .on_click(cx.listener(|view, _, _, cx| {
                        view.operation
                            .update(cx, |operation, cx| operation.cancel(cx))
                    })),
            );
        }
        if unknown {
            actions = actions.child(
                Button::new("sync-reconcile")
                    .ghost()
                    .compact()
                    .label("Refresh operation status")
                    .on_click(cx.listener(|view, _, _, cx| {
                        view.operation
                            .update(cx, |operation, cx| operation.reconcile(cx))
                    })),
            );
        }
        v_flex()
            .px_2()
            .gap_1()
            .child(actions)
            .children(error.map(|error| div().text_role(TextRole::Body, cx).text_color(danger).child(error)))
    }
}
#[cfg(test)]
#[path = "sync_controls_tests.rs"]
mod tests;
