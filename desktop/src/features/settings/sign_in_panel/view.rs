use super::*;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::Input;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
impl Render for SignInPanel {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let active = self
            .projection
            .operation
            .as_ref()
            .is_some_and(|row| row.active());
        let busy = self._command.is_some() || !self.projection.ready || self.identity.is_none();
        let mut content = v_flex().p_4().gap_3().w_full().child(
            v_flex()
                .gap_1()
                .child(
                    div()
                        .text_role(TextRole::Heading, cx)
                        .child(self.projection.target.provider_name.clone()),
                )
                .child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .text_color(cx.theme().muted_foreground)
                        .child(self.projection.target.machine_label.clone()),
                ),
        );
        let facts = self
            .projection
            .provider
            .as_ref()
            .and_then(|row| row.facts());
        if let Some(facts) = &facts {
            content = content.child(div().text_role(TextRole::Body, cx).child(
                format!("{} · {} · {}{}",facts.name,facts.adapter,facts.installation,
                facts.version.as_ref().map(|v|format!(" · {v}")).unwrap_or_default()),
            ));
            content = content.child(
                div()
                    .text_role(TextRole::Body, cx)
                    .child(format!("Authentication: {}", facts.auth)),
            );
        }
        if let Some(problem) = facts.as_ref().and_then(|facts| facts.problem.as_deref()) {
            content = content.child(
                div()
                    .text_role(TextRole::Body, cx)
                    .child(problem_text(problem)),
            );
        }
        if let Some(row) = &self.projection.operation {
            if let Some(method) = &row.method_id {
                content = content.child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .child(format!("Method: {method}")),
                );
            }
            if let Some(reason) = &row.reason {
                content = content.child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .child(problem_text(reason)),
                );
            }
        }
        let state = self
            .projection
            .operation
            .as_ref()
            .map(|row| match row.state.as_str() {
                "starting" => "Starting…",
                "waiting_for_person" => "Waiting for you",
                "finishing" => "Verifying…",
                "signed_in" => "Signed in",
                "cancelled" => "Cancelled",
                "expired" => "Timed out",
                "failed" => "Couldn't sign in",
                _ => "Status unavailable",
            })
            .unwrap_or(if self.projection.ready {
                "Ready to check this provider"
            } else {
                "Loading provider status…"
            });
        content = content.child(div().text_role(TextRole::Body, cx).child(state));
        let mut methods = h_flex().gap_2().flex_wrap();
        if let Some(facts) = &facts {
            for method in &facts.methods {
                let id = method.method_id.clone();
                methods = methods.child(
                    Button::new(SharedString::from(format!("auth:method:{id}")))
                        .compact()
                        .label(method.name.clone())
                        .when(self.method_id.as_ref() == Some(&id), |button| {
                            button.primary()
                        })
                        .disabled(busy || active)
                        .on_click(cx.listener(move |panel, _, _, cx| {
                            panel.method_id = Some(id.clone());
                            cx.notify();
                        })),
                );
            }
        }
        content = content.child(methods);
        let mut actions = h_flex().gap_2().flex_wrap().child(
            Button::new("auth:check")
                .label("Check")
                .disabled(busy || active || self.retry_command.is_some())
                .on_click(cx.listener(|panel, _, _, cx| panel.check(cx))),
        );
        if active {
            actions = actions.child(
                Button::new("auth:cancel")
                    .label("Cancel sign in")
                    .disabled(busy || self.retry_command.is_some())
                    .on_click(cx.listener(|panel, _, _, cx| panel.cancel(cx))),
            );
        } else {
            let available = facts.as_ref().is_some_and(|facts| {
                facts
                    .methods
                    .iter()
                    .any(|method| matches!(method.kind.as_str(), "agent" | "terminal"))
            });
            actions = actions.child(
                Button::new("auth:start")
                    .primary()
                    .label("Start sign in")
                    .disabled(busy || !available || self.retry_command.is_some())
                    .on_click(cx.listener(|panel, _, _, cx| panel.start(cx))),
            );
        }
        if self.retry_command.is_some() {
            actions = actions.child(
                Button::new("auth:retry")
                    .label("Retry same request")
                    .disabled(busy)
                    .on_click(cx.listener(|panel, _, _, cx| panel.retry(cx))),
            );
        }
        content = content.child(actions);
        if let Some(challenge) = &self.challenge.current {
            if let Some(url) = &challenge.link {
                let captured = super::challenge::DisplayedLink {
                    machine: self.projection.target.machine.clone(),
                    operation_id: challenge.operation_id.clone(),
                    generation: challenge.generation,
                    original_url: url.clone(),
                };
                content = content
                    .child(
                        div()
                            .text_role(TextRole::Body, cx)
                            .w_full()
                            .whitespace_normal()
                            .child(url.clone()),
                    )
                    .child(
                        Button::new("auth:open-link")
                            .label("Open sign-in page")
                            .disabled(self.challenge.opening || busy)
                            .on_click(cx.listener(move |panel, _, _, cx| {
                                panel.open_link(captured.clone(), cx)
                            })),
                    );
            }
            if challenge.wants_code {
                content = content
                    .child(
                        div()
                            .text_role(TextRole::Body, cx)
                            .child("Code from the sign-in page"),
                    )
                    .child(Input::new(&self.code))
                    .child(
                        Button::new("auth:submit-code")
                            .label("Submit code")
                            .disabled(busy || self._submit.is_some())
                            .on_click(
                                cx.listener(|panel, _, window, cx| panel.submit_code(window, cx)),
                            ),
                    );
            }
            if challenge.terminal_id.is_some() {
                content=content.child(div().text_role(TextRole::Body, cx).child("This method needs a terminal. Terminal sign-in attachment is not available in this panel yet."));
            }
        }
        if !self.error.is_empty() {
            content = content.child(
                div()
                    .text_role(TextRole::Body, cx)
                    .text_color(cx.theme().danger)
                    .child(self.error.clone()),
            );
        }
        content
    }
}

fn problem_text(reason: &str) -> &'static str {
    match reason {
        "not_installed" => "The provider adapter isn't installed.",
        "cannot_start" => "The provider adapter couldn't start.",
        "invalid_descriptor" => "The provider configuration is invalid.",
        "timed_out" => "The provider did not answer in time.",
        "adapter_crashed" => "The provider adapter stopped unexpectedly.",
        "network" => "The provider couldn't connect.",
        "version_unknown" => "The adapter version is unavailable.",
        "interrupted" => "The sign-in was interrupted.",
        "adapter_error" => "The provider adapter couldn't complete this request.",
        _ => "The provider couldn't complete this action.",
    }
}
