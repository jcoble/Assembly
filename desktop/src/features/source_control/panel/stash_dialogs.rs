use super::git_action::GitAction;
use super::git_labels::age_of_millis;
use super::git_target::GitTarget;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;
use std::{cell::RefCell, rc::Rc};

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Stash {
    pub sha: String,
    pub message: String,
    pub branch: Option<String>,
    created_ms: i64,
    made_by_assembly: bool,
}
impl Stash {
    /// What a person calls it, as git lists it: `On <branch>: <words>`. One made here is saved as
    /// `assembly <session> <time>: <text>`, whose words are the text, or its time when it has none.
    pub fn description(&self) -> String {
        let words = match self.message.strip_prefix("assembly ") {
            // Git trims the space that follows an empty text, leaving just the colon.
            Some(rest) => match rest.trim_end().split_once(": ") {
                Some((_, text)) => text,
                None => {
                    let made = rest.trim_end().trim_end_matches(':');
                    made.rsplit(' ').next().unwrap_or(made)
                }
            },
            None => &self.message,
        };
        match &self.branch {
            Some(branch) => format!("On {branch}: {words}"),
            None => words.to_string(),
        }
    }
}
struct DropApproval {
    sha: String,
    remaining: u8,
    sent: bool,
}
impl DropApproval {
    fn new(sha: String, own: bool) -> Self {
        Self {
            sha,
            remaining: if own { 1 } else { 2 },
            sent: false,
        }
    }
    fn sent(&self) -> bool {
        self.sent
    }
    fn confirm(&mut self) -> Option<String> {
        if self.sent {
            return None;
        }
        self.remaining -= 1;
        if self.remaining > 0 {
            return None;
        }
        self.sent = true;
        Some(self.sha.clone())
    }
}
#[derive(Clone)]
struct DropDialog {
    target: GitTarget,
    action: Entity<GitAction>,
    stash: Stash,
    approval: Rc<RefCell<DropApproval>>,
}
impl DropDialog {
    fn ask(&self, again: bool, window: &mut Window, cx: &mut App) {
        let captured = self.clone();
        window.open_alert_dialog(cx, move |alert, _, _| {
            let captured = captured.clone();
            alert
                .title(if again {
                    "This stash wasn't made by Assembly"
                } else {
                    "Drop this stash?"
                })
                .description(format!(
                    "{}\n{}\nDropping this stash permanently removes its saved changes.",
                    captured.stash.message, captured.stash.sha
                ))
                .show_cancel(true)
                .on_ok(move |_, window, cx| {
                    let target = captured.approval.borrow_mut().confirm();
                    if let Some(sha) = target {
                        let target = captured.target.clone();
                        captured.action.update(cx, |action, cx| {
                            let args = json!({"session":target.session,"sha":sha});
                            action.run(&target, "dropStash", args, cx);
                        });
                    } else if !captured.approval.borrow().sent() {
                        let handle = window.window_handle();
                        let next = captured.clone();
                        cx.defer(move |cx| {
                            _ = handle.update(cx, |_, window, cx| next.ask(true, window, cx));
                        });
                    }
                    true
                })
        });
    }
}
struct StashList {
    target: GitTarget,
    action: Entity<GitAction>,
    items: Vec<Stash>,
    loading: bool,
    error: Option<String>,
    read: Option<Task<()>>,
    completed: u64,
    revision: u64,
    _action: Subscription,
}
pub fn list(target: GitTarget, action: Entity<GitAction>, window: &mut Window, cx: &mut App) {
    let view = cx.new(|cx| {
        let completed = action.read(cx).completed;
        let heard = cx.observe(&action, |view: &mut StashList, _, cx| {
            let action = view.action.read(cx);
            let completed = action.completed;
            let own = action.target.as_ref() == Some(&view.target);
            if own && completed > view.completed {
                view.completed = completed;
                view.refresh(cx);
            }
            cx.notify();
        });
        let mut view = StashList {
            target,
            action,
            items: Vec::new(),
            loading: false,
            error: None,
            read: None,
            completed,
            revision: 0,
            _action: heard,
        };
        view.refresh(cx);
        view
    });
    window.open_dialog(cx, move |dialog, _, _| {
        dialog.title("Stashes").footer(Empty).child(view.clone())
    });
}
impl StashList {
    fn refresh(&mut self, cx: &mut Context<Self>) {
        self.read = None;
        self.loading = true;
        self.error = None;
        self.revision += 1;
        let revision = self.revision;
        let Some(socket) = SocketClient::try_global(cx) else {
            self.loading = false;
            self.error = Some("The backend isn't running.".into());
            cx.notify();
            return;
        };
        let call = socket.read(cx).call::<Vec<Stash>>(
            "git",
            "stashes",
            self.target
                .read_args(json!({"session":self.target.session})),
            None,
        );
        self.read = Some(cx.spawn(async move |view, cx| {
            let answer = call.await;
            _ = view.update(cx, |view, cx| {
                if view.revision != revision {
                    return;
                }
                view.loading = false;
                view.read = None;
                match answer {
                    Outcome::Ok(items) => view.items = items,
                    Outcome::Failed(error) => view.error = Some(error.message),
                    Outcome::Aborted => view.error = Some("The stash read was cancelled.".into()),
                }
                cx.notify();
            });
        }));
        cx.notify();
    }
}
impl Render for StashList {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let danger = cx.theme().danger;
        let count = self.items.len();
        v_flex()
            .gap_2()
            .children(
                self.error
                    .clone()
                    .map(|error| div().text_color(danger).child(error)),
            )
            .children(
                self.action
                    .read(cx)
                    .failure_sentence()
                    .map(|error| div().text_color(danger).child(error)),
            )
            .child(
                uniform_list(
                    "stash-items",
                    count,
                    cx.processor(|view, range: std::ops::Range<usize>, _, cx| {
                        let items = view
                            .items
                            .iter()
                            .skip(range.start)
                            .take(range.len())
                            .cloned()
                            .collect::<Vec<_>>();
                        let busy = view.action.read(cx).pending.is_some();
                        items
                            .into_iter()
                            .map(|stash| {
                                let age = age_of_millis(stash.created_ms);
                                let applied = stash.sha.clone();
                                let dropped = stash.clone();
                                h_flex()
                                    .h(px(64.))
                                    .gap_2()
                                    .child(
                                        v_flex()
                                            .flex_1()
                                            .min_w_0()
                                            .child(div().truncate().child(stash.message))
                                            .child(div().text_role(TextRole::Body, cx).child(
                                                format!(
                                                    "{} · {}{}",
                                                    stash.branch.unwrap_or_default(),
                                                    age,
                                                    if stash.made_by_assembly {
                                                        " · Assembly"
                                                    } else {
                                                        ""
                                                    }
                                                ),
                                            )),
                                    )
                                    .child(
                                        Button::new(SharedString::from(format!(
                                            "apply:{}",
                                            stash.sha
                                        )))
                                        .ghost()
                                        .compact()
                                        .label("Apply")
                                        .disabled(busy)
                                        .on_click(
                                            cx.listener(move |view, _, _, cx| {
                                                let target = view.target.clone();
                                                view.action.update(cx, |action, cx| {
                                                let args =
                                                    json!({"session":target.session,"sha":applied});
                                                action.run(&target, "applyStash", args, cx);
                                            });
                                            }),
                                        ),
                                    )
                                    .child(
                                        Button::new(SharedString::from(format!(
                                            "drop:{}",
                                            stash.sha
                                        )))
                                        .ghost()
                                        .compact()
                                        .label("Drop…")
                                        .disabled(busy)
                                        .on_click(
                                            cx.listener(move |view, _, window, cx| {
                                                DropDialog {
                                                    target: view.target.clone(),
                                                    action: view.action.clone(),
                                                    approval: Rc::new(RefCell::new(
                                                        DropApproval::new(
                                                            dropped.sha.clone(),
                                                            dropped.made_by_assembly,
                                                        ),
                                                    )),
                                                    stash: dropped.clone(),
                                                }
                                                .ask(false, window, cx);
                                            }),
                                        ),
                                    )
                            })
                            .collect()
                    }),
                )
                .h(px(320.)),
            )
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        Button::new("stashes-refresh")
                            .ghost()
                            .label(if self.loading {
                                "Reading…"
                            } else {
                                "Refresh"
                            })
                            .disabled(self.loading)
                            .on_click(cx.listener(|view, _, _, cx| view.refresh(cx))),
                    )
                    .child(
                        Button::new("stashes-close")
                            .ghost()
                            .label("Close")
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    ),
            )
    }
}
#[cfg(test)]
#[path = "stash_dialogs_tests.rs"]
mod tests;
