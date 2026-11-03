use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::git_action::GitAction;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;
use std::{cell::RefCell, rc::Rc};

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Stash {
    sha: String,
    message: String,
    branch: Option<String>,
    created_ms: i64,
    made_by_assembly: bool,
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
    session: String,
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
                        let session = captured.session.clone();
                        captured.action.update(cx, |action, cx| {
                            action.run(
                                session.clone(),
                                "dropStash",
                                json!({"session":session,"sha":sha}),
                                cx,
                            );
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
struct StashForm {
    session: String,
    input: Entity<InputState>,
    include_new: bool,
    action: Entity<GitAction>,
    submitted: bool,
    _heard: Vec<Subscription>,
}
pub fn create(session: String, action: Entity<GitAction>, window: &mut Window, cx: &mut App) {
    let form = cx.new(|cx| {
        let input = cx.new(|cx| InputState::new(window, cx).placeholder("Message (optional)"));
        let heard = vec![
            cx.subscribe(&input, |_: &mut StashForm, _, _: &InputEvent, cx| {
                cx.notify()
            }),
            cx.observe(&action, |_, _, cx| cx.notify()),
        ];
        StashForm {
            session,
            input,
            include_new: false,
            action,
            submitted: false,
            _heard: heard,
        }
    });
    window.open_dialog(cx, move |dialog, _, _| {
        dialog
            .title("Stash changes")
            .footer(Empty)
            .child(form.clone())
    });
}
impl Render for StashForm {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let busy = self.action.read(cx).pending.is_some();
        let danger = cx.theme().danger;
        let mut root = v_flex()
            .gap_2()
            .child(Input::new(&self.input).disabled(busy))
            .child(
                Checkbox::new("stash-new-files")
                    .label("Include new files")
                    .checked(self.include_new)
                    .disabled(busy)
                    .on_click(cx.listener(|view, value, _, cx| {
                        view.include_new = *value;
                        cx.notify();
                    })),
            );
        if self.submitted {
            root = root
                .children(
                    self.action
                        .read(cx)
                        .failure
                        .clone()
                        .map(|error| div().text_color(danger).child(error)),
                )
                .children(
                    self.action
                        .read(cx)
                        .message
                        .clone()
                        .map(|message| div().child(message)),
                );
        }
        root.child(h_flex().gap_2().child(Button::new("stash-close").ghost().label("Close").on_click(|_,window,cx|window.close_dialog(cx)))
            .child(Button::new("stash-submit").primary().label("Stash changes").disabled(busy||self.submitted)
                .on_click(cx.listener(|view,_,_,cx|{
                    let session=view.session.clone();let message=view.input.read(cx).value().to_string();let include=view.include_new;
                    view.submitted=view.action.update(cx,|action,cx|action.run(session.clone(),"stash",json!({"session":session,"message":message,"includeUntracked":include}),cx));cx.notify();
                }))))
    }
}
struct StashList {
    session: String,
    action: Entity<GitAction>,
    items: Vec<Stash>,
    loading: bool,
    error: Option<String>,
    read: Option<Task<()>>,
    completed: u64,
    revision: u64,
    now_ms: i64,
    _action: Subscription,
}
pub fn list(session: String, action: Entity<GitAction>, window: &mut Window, cx: &mut App) {
    let view = cx.new(|cx| {
        let completed = action.read(cx).completed;
        let heard = cx.observe(&action, |view: &mut StashList, _, cx| {
            let action = view.action.read(cx);
            let completed = action.completed;
            let own = action.session.as_deref() == Some(view.session.as_str());
            if own && completed > view.completed {
                view.completed = completed;
                view.refresh(cx);
            }
            cx.notify();
        });
        let mut view = StashList {
            session,
            action,
            items: Vec::new(),
            loading: false,
            error: None,
            read: None,
            completed,
            revision: 0,
            now_ms: chrono::Utc::now().timestamp_millis(),
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
            json!({"session":self.session}),
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
                    Outcome::Ok(items) => {
                        view.items = items;
                        view.now_ms = chrono::Utc::now().timestamp_millis();
                    }
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
                    .failure
                    .clone()
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
                                let seconds = (view.now_ms - stash.created_ms).max(0) / 1000;
                                let age = if seconds < 3600 {
                                    format!("{}m ago", seconds / 60)
                                } else if seconds < 86400 {
                                    format!("{}h ago", seconds / 3600)
                                } else {
                                    format!("{}d ago", seconds / 86400)
                                };
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
                                            .child(div().text_role(TextRole::Body, cx).child(format!(
                                                "{} · {}{}",
                                                stash.branch.unwrap_or_default(),
                                                age,
                                                if stash.made_by_assembly {
                                                    " · Assembly"
                                                } else {
                                                    ""
                                                }
                                            ))),
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
                                                let session = view.session.clone();
                                                view.action.update(cx, |action, cx| {
                                                    action.run(
                                                        session.clone(),
                                                        "applyStash",
                                                        json!({"session":session,"sha":applied}),
                                                        cx,
                                                    );
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
                                                    session: view.session.clone(),
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
