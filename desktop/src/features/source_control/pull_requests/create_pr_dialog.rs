use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::super::git_operation::GitOperation;
use super::pull_request::PullRequestCreated;
use crate::core::{
    intents::{open_pull_requests::OpenPullRequests, open_url::OpenUrl},
    socket::{client::SocketClient, outcome::Outcome},
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::input::{Input, InputEvent, InputState, Textarea, TextareaState};
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
#[serde(rename_all = "camelCase")]
struct Branches {
    default_branch: Option<String>,
}
struct CreatePrForm {
    session: String,
    base: Entity<InputState>,
    title: Entity<InputState>,
    description: Entity<TextareaState>,
    draft: bool,
    operation: Entity<GitOperation>,
    created: Option<PullRequestCreated>,
    unconfirmed: bool,
    error: Option<String>,
    base_revision: u64,
    branches: Option<Task<()>>,
    _inputs: Vec<Subscription>,
    _operation: Subscription,
}
pub fn open(session: String, window: &mut Window, cx: &mut App) {
    let form = cx.new(|cx| CreatePrForm::new(session, window, cx));
    window.open_dialog(cx, move |dialog, _, _| {
        dialog
            .title("Create pull request")
            .footer(Empty)
            .child(form.clone())
    });
}
impl CreatePrForm {
    fn new(session: String, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let base = cx.new(|cx| InputState::new(window, cx).placeholder("Base branch"));
        let title = cx.new(|cx| InputState::new(window, cx).placeholder("Title"));
        let description = cx.new(|cx| {
            TextareaState::new(window, cx)
                .placeholder("Description")
                .auto_grow(4, 10)
        });
        let operation = cx.new(|_| GitOperation::default());
        let heard=cx.observe(&operation,|form:&mut Self,_,cx|{
            if form.operation.read(cx).unconfirmed{form.unconfirmed=true;}
            if let Some(status)=&form.operation.read(cx).status {
                if status.state=="succeeded"&&form.created.is_none() {
                    match status.result.clone().and_then(|value|serde_json::from_value::<PullRequestCreated>(value).ok()).filter(|pr|pr.number>0&&!pr.url.is_empty()) {
                        Some(pr)=>form.created=Some(pr),
                        None=>{form.unconfirmed=true;form.error=Some("The creation result was not confirmed. Refresh before trying again.".into());},
                    }
                }
                if status.state=="uncertain"{form.unconfirmed=true;}
            }cx.notify();
        });
        let inputs = vec![
            cx.subscribe(&base, |form: &mut Self, _, event: &InputEvent, cx| {
                if matches!(event, InputEvent::Change) {
                    form.base_revision += 1;
                    cx.notify();
                }
            }),
            cx.subscribe(&title, |_: &mut Self, _, _: &InputEvent, cx| cx.notify()),
            cx.subscribe(&description, |_: &mut Self, _, _: &InputEvent, cx| {
                cx.notify()
            }),
        ];
        let mut form = Self {
            session,
            base,
            title,
            description,
            draft: false,
            operation,
            created: None,
            unconfirmed: false,
            error: None,
            base_revision: 0,
            branches: None,
            _inputs: inputs,
            _operation: heard,
        };
        form.load_base(window, cx);
        form
    }
    fn input(&self, cx: &App) -> CreatePrInput {
        CreatePrInput {
            base: self.base.read(cx).value().to_string(),
            title: self.title.read(cx).value().to_string(),
            description: self.description.read(cx).value().to_string(),
            draft: self.draft,
        }
    }
    fn load_base(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let revision = self.base_revision;
        let handle = window.window_handle();
        let call = socket.read(cx).call::<Branches>(
            "git",
            "branches",
            json!({"session":self.session}),
            None,
        );
        self.branches = Some(cx.spawn(async move |form, cx| {
            let answer = call.await;
            _ = handle.update(cx, |_, window, cx| {
                form.update(cx, |form, cx| {
                    form.branches = None;
                    if form.base_revision != revision {
                        return;
                    }
                    match answer {
                        Outcome::Ok(branches) => {
                            if let Some(base) = branches.default_branch {
                                form.base
                                    .update(cx, |input, cx| input.set_value(base, window, cx));
                            }
                        }
                        Outcome::Failed(error) => form.error = Some(error.message),
                        Outcome::Aborted => {}
                    }
                    cx.notify();
                })
            });
        }));
    }
    fn create(&mut self, cx: &mut Context<Self>) {
        if self.operation.read(cx).busy || self.created.is_some() || self.unconfirmed {
            return;
        }
        match self.input(cx).args() {
            Ok(args) => {
                self.error = None;
                let session = self.session.clone();
                self.operation.update(cx, |operation, cx| {
                    operation.start("createPr", Some(session), args, cx)
                });
            }
            Err(error) => self.error = Some(error),
        }
        cx.notify();
    }
}
impl Render for CreatePrForm {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let busy = self.operation.read(cx).busy;
        let disabled = busy || self.created.is_some() || self.unconfirmed;
        let danger = cx.theme().danger;
        let can_create = !disabled && self.input(cx).args().is_ok();
        let created = self.created.clone();
        v_flex()
            .gap_2()
            .child("Base branch")
            .child(Input::new(&self.base).disabled(disabled))
            .child(Input::new(&self.title).disabled(disabled))
            .child(Textarea::new(&self.description).disabled(disabled))
            .child(
                Checkbox::new("create-pr-draft")
                    .label("Draft")
                    .checked(self.draft)
                    .disabled(disabled)
                    .on_click(cx.listener(|form, value, _, cx| {
                        form.draft = *value;
                        cx.notify();
                    })),
            )
            .children(
                self.error
                    .clone()
                    .map(|error| div().text_role(TextRole::Body, cx).text_color(danger).child(error)),
            )
            .children(
                self.operation
                    .read(cx)
                    .error
                    .clone()
                    .map(|error| div().text_role(TextRole::Body, cx).text_color(danger).child(error)),
            )
            .children(self.unconfirmed.then(|| {
                let session = self.session.clone();
                Button::new("create-pr-refresh")
                    .ghost()
                    .label("Refresh pull requests")
                    .on_click(move |_, window, cx| {
                        window.close_dialog(cx);
                        cx.dispatch_action(&OpenPullRequests {
                            session_id: Some(session.clone()),
                            repo: None,
                            number: None,
                        });
                    })
            }))
            .children(created.map(|pr| {
                let url = pr.url.clone();
                let session = self.session.clone();
                v_flex()
                    .gap_1()
                    .child(format!("Created pull request #{}", pr.number))
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
                        Button::new("create-pr-submit")
                            .primary()
                            .label(if busy {
                                "Creating…"
                            } else {
                                "Push and create"
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
