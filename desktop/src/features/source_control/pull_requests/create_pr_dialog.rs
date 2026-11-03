//! Create a pull request: branch into base, title and description drafted once when it opens, and it pushes before it
//! creates. A draft lands only on the fields it was asked for. Replaces PullRequestPanel.svelte.
use super::super::git_action::GitWrites;
use super::super::git_branches::GitBranchList;
use super::super::git_operation::OperationStatus;
use super::super::git_target::GitTarget;
use super::pull_request::PullRequestCreated;
use crate::core::{
    intents::{open_pull_requests::OpenPullRequests, open_url::OpenUrl},
    socket::{client::SocketClient, outcome::Outcome},
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::input::{Input, InputEvent, InputState, Textarea, TextareaState};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};

#[derive(Clone)]
struct CreatePrInput {
    base: String,
    title: String,
    description: String,
    draft: bool,
}
impl CreatePrInput {
    fn args(&self) -> Result<Value, String> {
        if self.base.trim().is_empty() {
            return Err("Choose a base branch.".into());
        }
        if self.title.trim().is_empty() {
            return Err("A pull request title is required.".into());
        }
        Ok(
            json!({"base":self.base.trim(),"title":self.title.trim(),"description":self.description,"draft":self.draft}),
        )
    }
}
#[derive(Deserialize)]
struct DraftedPr {
    title: String,
    description: String,
}
/// A failed create never leaves a pull request behind, and it says so, because the push may have gone through.
fn create_failure(error: Option<String>) -> String {
    format!(
        "{} Nothing was created.",
        error
            .as_deref()
            .unwrap_or("The pull request couldn't be created.")
            .trim_end()
    )
}
struct CreatePrForm {
    target: GitTarget,
    writes: GitWrites,
    branch: String,
    base: String,
    branches: Vec<String>,
    title: Entity<InputState>,
    description: Entity<TextareaState>,
    draft: bool,
    pending: Option<&'static str>,
    created: Option<PullRequestCreated>,
    unconfirmed: bool,
    error: Option<String>,
    edit_revision: u64,
    drafted_at: Option<(u64, String)>,
    drafted_once: bool,
    window: AnyWindowHandle,
    read: Option<Task<()>>,
    _heard: Vec<Subscription>,
}
pub fn open(
    target: GitTarget,
    writes: GitWrites,
    branch: String,
    window: &mut Window,
    cx: &mut App,
) {
    let form = cx.new(|cx| CreatePrForm::new(target, writes, branch, window, cx));
    window.open_dialog(cx, move |dialog, _, _| {
        dialog
            .title("Create pull request")
            .footer(Empty)
            .child(form.clone())
    });
}
impl CreatePrForm {
    fn new(
        target: GitTarget,
        writes: GitWrites,
        branch: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let title = cx.new(|cx| InputState::new(window, cx).placeholder("Title"));
        let description = cx.new(|cx| {
            TextareaState::new(window, cx)
                .placeholder("Description")
                .auto_grow(4, 10)
        });
        let edited = |form: &mut Self, event: &InputEvent, cx: &mut Context<Self>| {
            if matches!(event, InputEvent::Change) {
                form.edit_revision += 1;
                cx.notify();
            }
        };
        let heard = vec![
            cx.observe(&writes.operation, |form: &mut Self, _, cx| {
                form.operation_changed(cx)
            }),
            cx.subscribe(&title, move |form: &mut Self, _, event: &InputEvent, cx| {
                edited(form, event, cx)
            }),
            cx.subscribe(
                &description,
                move |form: &mut Self, _, event: &InputEvent, cx| edited(form, event, cx),
            ),
        ];
        let mut form = Self {
            target,
            writes,
            branch,
            base: String::new(),
            branches: Vec::new(),
            title,
            description,
            draft: false,
            pending: None,
            created: None,
            unconfirmed: false,
            error: None,
            edit_revision: 0,
            drafted_at: None,
            drafted_once: false,
            window: window.window_handle(),
            read: None,
            _heard: heard,
        };
        form.read_branches(cx);
        form
    }
    fn input(&self, cx: &App) -> CreatePrInput {
        CreatePrInput {
            base: self.base.clone(),
            title: self.title.read(cx).value().to_string(),
            description: self.description.read(cx).value().to_string(),
            draft: self.draft,
        }
    }
    fn read_branches(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let args = self
            .target
            .read_args(json!({"session":self.target.session}));
        let call = socket
            .read(cx)
            .call::<GitBranchList>("git", "branches", args, None);
        self.read = Some(cx.spawn(async move |form, cx| {
            let answer = call.await;
            _ = form.update(cx, |form, cx| {
                form.read = None;
                match answer {
                    Outcome::Ok(list) => {
                        form.branches = list
                            .branches
                            .into_iter()
                            .map(|branch| branch.name)
                            .collect();
                        if form.base.is_empty() {
                            form.base = list.default_branch.unwrap_or_default();
                        }
                        if !form.drafted_once && !form.base.is_empty() {
                            form.drafted_once = true;
                            form.generate(cx);
                        }
                    }
                    Outcome::Failed(error) => form.error = Some(error.message),
                    Outcome::Aborted => {}
                }
                cx.notify();
            });
        }));
    }
    fn generate(&mut self, cx: &mut Context<Self>) {
        if self.writes.busy(cx) || self.base.is_empty() {
            return;
        }
        self.error = None;
        self.pending = Some("draftPr");
        self.drafted_at = Some((self.edit_revision, self.base.clone()));
        let (target, args) = (self.target.clone(), json!({"base":self.base}));
        self.writes.operation.update(cx, |operation, cx| {
            operation.start_target("draftPr", &target, args, cx)
        });
        cx.notify();
    }
    fn create(&mut self, cx: &mut Context<Self>) {
        if self.writes.busy(cx) || self.created.is_some() || self.unconfirmed {
            return;
        }
        match self.input(cx).args() {
            Ok(args) => {
                self.error = None;
                self.pending = Some("createPr");
                let target = self.target.clone();
                self.writes.operation.update(cx, |operation, cx| {
                    operation.start_target("createPr", &target, args, cx)
                });
            }
            Err(error) => self.error = Some(error),
        }
        cx.notify();
    }
    fn operation_changed(&mut self, cx: &mut Context<Self>) {
        cx.notify();
        let Some(pending) = self.pending else {
            return;
        };
        let operation = self.writes.operation.read(cx);
        if operation.unconfirmed {
            self.pending = None;
            self.unconfirmed = true;
            return;
        }
        if operation.busy {
            return;
        }
        let error = operation.error.clone();
        match operation.status.clone() {
            None => {
                self.pending = None;
                self.error = error;
            }
            Some(status) if status.terminal() => {
                self.pending = None;
                self.finish(pending, status, error, cx);
            }
            Some(_) => {}
        }
    }
    fn finish(
        &mut self,
        pending: &'static str,
        status: OperationStatus,
        error: Option<String>,
        cx: &mut Context<Self>,
    ) {
        let error = status.error.clone().or(error);
        match (pending, status.state.as_str()) {
            ("draftPr", "succeeded") => {
                let current =
                    self.drafted_at.as_ref() == Some(&(self.edit_revision, self.base.clone()));
                let drafted = status
                    .result
                    .and_then(|result| serde_json::from_value::<DraftedPr>(result).ok());
                if let (true, Some(drafted)) = (current, drafted) {
                    self.apply_draft(drafted, cx);
                }
            }
            ("createPr", "succeeded") => {
                let created = status
                    .result
                    .and_then(|result| serde_json::from_value::<PullRequestCreated>(result).ok())
                    .filter(|pr| pr.number > 0 && !pr.url.is_empty());
                match created {
                    Some(pr) => self.created = Some(pr),
                    None => {
                        self.unconfirmed = true;
                        self.error = Some(
                            "The creation result was not confirmed. Refresh before trying again."
                                .into(),
                        );
                    }
                }
            }
            (_, "uncertain") => self.unconfirmed = true,
            (_, "cancelled") => {}
            ("createPr", _) => self.error = Some(create_failure(error)),
            _ => self.error = error,
        }
    }
    fn apply_draft(&self, drafted: DraftedPr, cx: &mut Context<Self>) {
        let (title, description, handle) =
            (self.title.clone(), self.description.clone(), self.window);
        cx.defer(move |cx| {
            _ = handle.update(cx, |_, window, cx| {
                title.update(cx, |input, cx| input.set_value(drafted.title, window, cx));
                description.update(cx, |input, cx| {
                    input.set_value(drafted.description, window, cx)
                });
            });
        });
    }
}
impl Render for CreatePrForm {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let (danger, muted) = (cx.theme().danger, cx.theme().muted_foreground);
        let busy = self.writes.busy(cx);
        let creating = self.pending == Some("createPr");
        let drafting = self.pending == Some("draftPr");
        let disabled = busy || self.created.is_some() || self.unconfirmed;
        let can_create = !disabled && self.input(cx).args().is_ok();
        let field = |label: &'static str| {
            div()
                .text_xs()
                .font_weight(FontWeight::MEDIUM)
                .text_color(muted)
                .child(label)
        };
        let form = cx.entity().downgrade();
        let branches = self.branches.clone();
        let base_menu = Button::new("create-pr-base")
            .ghost()
            .compact()
            .label(if self.base.is_empty() {
                "Choose base".to_string()
            } else {
                self.base.clone()
            })
            .dropdown_caret(true)
            .disabled(disabled)
            .dropdown_menu_with_anchor(Anchor::TopLeft, move |mut menu, _, _| {
                for name in &branches {
                    let (weak, chosen) = (form.clone(), name.clone());
                    menu = menu.item(PopupMenuItem::new(name.clone()).on_click(move |_, _, cx| {
                        _ = weak.update(cx, |form, cx| {
                            form.base = chosen.clone();
                            cx.notify();
                        });
                    }));
                }
                menu
            });
        let created = self.created.clone();
        let session = self.target.session.clone();
        let unconfirmed_session = session.clone();
        v_flex()
            .gap_2()
            .child(
                h_flex()
                    .gap_2()
                    .text_sm()
                    .child(div().truncate().child(self.branch.clone()))
                    .child(div().text_color(muted).child("→"))
                    .child(base_menu),
            )
            .child(field("TITLE"))
            .child(Input::new(&self.title).disabled(disabled))
            .child(field("DESCRIPTION"))
            .child(Textarea::new(&self.description).disabled(disabled))
            .child(
                Checkbox::new("create-pr-draft")
                    .label("Create as draft")
                    .checked(self.draft)
                    .disabled(disabled)
                    .on_click(cx.listener(|form, value, _, cx| {
                        form.draft = *value;
                        cx.notify();
                    })),
            )
            .children(drafting.then(|| {
                div()
                    .text_sm()
                    .text_color(muted)
                    .child("Generating title & description...")
            }))
            .children(
                self.error
                    .clone()
                    .map(|error| div().text_sm().text_color(danger).child(error)),
            )
            .children(self.unconfirmed.then(|| {
                Button::new("create-pr-refresh")
                    .ghost()
                    .label("Refresh pull requests")
                    .on_click(move |_, window, cx| {
                        window.close_dialog(cx);
                        cx.dispatch_action(&OpenPullRequests {
                            session_id: Some(unconfirmed_session.clone()),
                            repo: None,
                            number: None,
                        });
                    })
            }))
            .children(created.map(|pr| {
                let url = pr.url.clone();
                v_flex()
                    .gap_1()
                    .child(format!("PR #{} created", pr.number))
                    .child(
                        Button::new("created-pr-browser")
                            .ghost()
                            .label("Open on GitHub")
                            .on_click(move |_, _, cx| {
                                cx.dispatch_action(&OpenUrl { url: url.clone() })
                            }),
                    )
                    .child(
                        Button::new("created-pr-tab")
                            .ghost()
                            .label("Open in Pull requests")
                            .on_click(move |_, window, cx| {
                                window.close_dialog(cx);
                                cx.dispatch_action(&OpenPullRequests {
                                    session_id: Some(session.clone()),
                                    repo: None,
                                    number: Some(pr.number),
                                });
                            }),
                    )
            }))
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        Button::new("create-pr-cancel")
                            .ghost()
                            .label("Close")
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    )
                    .child(
                        Button::new("create-pr-regenerate")
                            .ghost()
                            .label("Regenerate")
                            .disabled(disabled || self.base.is_empty())
                            .on_click(cx.listener(|form, _, _, cx| form.generate(cx))),
                    )
                    .child(
                        Button::new("create-pr-submit")
                            .primary()
                            .label(if creating {
                                "Pushing & creating…"
                            } else {
                                "Push & Create PR"
                            })
                            .disabled(!can_create)
                            .on_click(cx.listener(|form, _, _, cx| form.create(cx))),
                    ),
            )
    }
}
#[cfg(test)]
#[path = "create_pr_dialog_tests.rs"]
mod tests;
