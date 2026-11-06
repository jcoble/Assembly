//! Connecting Notion: a card that says what's needed and shrinks once it's done. Sign-in runs in the person's browser,
//! which comes back through an `assembly://notion-oauth` link; only the sign-in started here takes that link. A
//! developer's token and data source go through a form, with the token masked. Neither secret is logged or kept here.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use std::rc::Rc;

use crate::activity::{CardBody, NeedsCard, NeedsState};
use crate::core::intents::open_url::OpenUrl;
use crate::core::navigation::external_urls::canonical_notion_id;
use crate::core::ui::row_drop_down::RowDropDowns;
use crate::inline_field::{
    FieldValue, FormFailure, FormSaveRequested, FormValues, InlineField, InlineForm,
};

use super::notion_client::NotionClient;
use super::tasks_state::error_text;

const CARD: &str = "notion-connect";
const SOURCE_FIELD: &str = "source";

struct Attempt {
    operation_id: String,
    state: String,
}

pub struct NotionSetup {
    client: NotionClient,
    attempt: Option<Attempt>,
    busy: bool,
    error: Option<String>,
    show_developer: bool,
    connected: bool,
    token: Entity<InputState>,
    form: Entity<InlineForm>,
    drops: Entity<RowDropDowns>,
    call: Option<Task<()>>,
    _heard: Vec<Subscription>,
}

/// A bare id, or nothing to let the app find the task database itself.
pub fn source_rule(value: &str) -> Result<(), String> {
    if value.trim().is_empty() || canonical_notion_id(value.trim()).is_some() {
        Ok(())
    } else {
        Err("That isn't a Notion data source ID.".into())
    }
}

impl NotionSetup {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let token = cx.new(|cx| {
            InputState::new(window, cx)
                .masked(true)
                .placeholder("Developer token")
        });
        let field = cx.new(|cx| {
            InlineField::text("Data source ID", "", window, cx)
                .in_form()
                .rule(Rc::new(|value| match value {
                    FieldValue::Text(text) => source_rule(text),
                    _ => Ok(()),
                }))
        });
        let form = cx.new(|cx| InlineForm::new(vec![(SOURCE_FIELD.into(), field)], cx));
        let drops = cx.new(|_| RowDropDowns::default());
        let heard = vec![
            cx.subscribe_in(&form, window, Self::save_developer),
            cx.observe(&drops, |_, _, cx| cx.notify()),
        ];
        Self {
            client: NotionClient,
            attempt: None,
            busy: false,
            error: None,
            show_developer: false,
            connected: false,
            token,
            form,
            drops,
            call: None,
            _heard: heard,
        }
    }

    /// Follows the connection: the card opens while it's needed and shrinks once it's made.
    pub fn sync(&mut self, connected: bool, cx: &mut Context<Self>) {
        let key = SharedString::from(CARD);
        if connected {
            NeedsCard::settle(&key, &self.drops, cx);
        } else {
            NeedsCard::open(key, &self.drops, Self::body(cx.entity().downgrade()), cx);
        }
        self.connected = connected;
        if connected {
            self.error = None;
        }
        cx.notify();
    }

    fn connect(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.busy = true;
        self.error = None;
        let begin = self.client.oauth_begin(cx);
        self.call = Some(cx.spawn_in(window, async move |setup, cx| {
            let result = begin.await;
            let _ = setup.update_in(cx, |setup, window, cx| {
                setup.busy = false;
                match result {
                    Ok(start) => {
                        setup.attempt = Some(Attempt {
                            operation_id: start.operation_id,
                            state: start.state,
                        });
                        window.dispatch_action(
                            Box::new(OpenUrl {
                                url: start.authorization_url,
                            }),
                            cx,
                        );
                    }
                    Err(error) => setup.error = Some(error_text(&error.code)),
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    /// The browser's link back. A link for any other sign-in, or a second copy of this one, was not started here.
    pub fn receive(&mut self, state: String, claim_code: String, cx: &mut Context<Self>) {
        if self
            .attempt
            .as_ref()
            .is_none_or(|attempt| attempt.state != state)
        {
            return;
        }
        let Some(attempt) = self.attempt.take() else {
            return;
        };
        self.busy = true;
        self.error = None;
        let claim = self
            .client
            .oauth_claim(attempt.operation_id, state, claim_code, cx);
        self.call = Some(cx.spawn(async move |setup, cx| {
            let result = claim.await;
            let _ = setup.update(cx, |setup, cx| {
                setup.busy = false;
                if let Err(error) = result {
                    setup.show_developer |= error.code == "ambiguous_data_source";
                    setup.error = Some(error_text(&error.code));
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    fn cancel(&mut self, cx: &mut Context<Self>) {
        if let Some(attempt) = self.attempt.take() {
            let cancel = self.client.oauth_cancel(attempt.operation_id, cx);
            self.call = Some(cx.spawn(async move |_, _| {
                let _ = cancel.await;
            }));
        }
        cx.notify();
    }

    fn disconnect(&mut self, cx: &mut Context<Self>) {
        self.busy = true;
        self.error = None;
        let clear = self.client.clear_settings(cx);
        self.call = Some(cx.spawn(async move |setup, cx| {
            let result = clear.await;
            let _ = setup.update(cx, |setup, cx| {
                setup.busy = false;
                if let Err(error) = result {
                    setup.error = Some(error_text(&error.code));
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    fn save_developer(
        &mut self,
        form: &Entity<InlineForm>,
        event: &FormSaveRequested,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let (form, request, values) = (form.clone(), event.request, event.values.clone());
        let source = match values.get(SOURCE_FIELD) {
            Some(FieldValue::Text(text)) => text.trim().to_owned(),
            _ => String::new(),
        };
        let token = self.token.read(cx).value().trim().to_string();
        if token.is_empty() && !self.connected {
            form.update(cx, |form, cx| {
                let refused = FormFailure::Refused("Enter a token first.".into());
                form.complete_save(request, Err(refused), window, cx);
            });
            return;
        }
        let call = self.client.save_settings(
            (!source.is_empty()).then_some(source),
            (!token.is_empty()).then_some(token),
            cx,
        );
        self.call = Some(cx.spawn_in(window, async move |setup, cx| {
            let result = call.await;
            let _ = setup.update_in(cx, |setup, window, cx| {
                let done: Result<FormValues, FormFailure> = match result {
                    Ok(_) => {
                        setup
                            .token
                            .update(cx, |token, cx| token.set_value("", window, cx));
                        Ok(values)
                    }
                    Err(error) => Err(FormFailure::Refused(error_text(&error.code))),
                };
                form.update(cx, |form, cx| {
                    form.complete_save(request, done, window, cx);
                });
                cx.notify();
            });
        }));
    }

    fn body(this: WeakEntity<Self>) -> CardBody {
        Rc::new(move |_, cx| {
            let Some(this) = this.upgrade() else {
                return Empty.into_any_element();
            };
            this.read(cx).draw_body(&this, cx).into_any_element()
        })
    }

    fn draw_body(&self, this: &Entity<Self>, cx: &App) -> impl IntoElement {
        let waiting = self.attempt.is_some();
        let connect = this.clone();
        let cancel = this.clone();
        let developer = this.clone();
        let mut column = v_flex().gap_2().child(
            h_flex()
                .gap_2()
                .child(
                    Button::new("notion-connect")
                        .small()
                        .primary()
                        .disabled(self.busy || waiting)
                        .label(if waiting {
                            "Waiting for Notion…"
                        } else {
                            "Connect Notion"
                        })
                        .on_click(move |_, window, cx| {
                            connect.update(cx, |setup, cx| setup.connect(window, cx));
                        }),
                )
                .when(waiting, |row| {
                    row.child(
                        Button::new("notion-cancel")
                            .small()
                            .ghost()
                            .label("Cancel")
                            .on_click(move |_, _, cx| {
                                cancel.update(cx, |setup, cx| setup.cancel(cx))
                            }),
                    )
                }),
        );
        if let Some(error) = &self.error {
            column = column.child(div().text_color(cx.theme().danger).child(error.clone()));
        }
        column = column.child(
            Button::new("notion-developer")
                .small()
                .ghost()
                .label("Developer token setup")
                .on_click(move |_, _, cx| {
                    developer.update(cx, |setup, cx| {
                        setup.show_developer = !setup.show_developer;
                        cx.notify();
                    });
                }),
        );
        if self.show_developer {
            column = column
                .child(div().text_color(cx.theme().muted_foreground).child(
                    "For local development. Leave the ID blank to find the database named Tasks.",
                ))
                .child(Input::new(&self.token).small())
                .child(self.form.clone());
        }
        column
    }
}

impl Render for NotionSetup {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let state = if self.connected {
            NeedsState::Answered {
                summary: "Connected to Notion".into(),
                delivery: None,
            }
        } else {
            NeedsState::Open
        };
        let disconnect = cx.entity();
        v_flex()
            .gap_2()
            .child(NeedsCard {
                key: CARD.into(),
                kind: "Connection".into(),
                title: "Connect Notion".into(),
                why: "Sign in through your browser, choose the pages Assembly can read, then come back here.".into(),
                next_step: None,
                chips: Vec::new(),
                time: None,
                state,
                drops: self.drops.clone(),
                body: Self::body(cx.entity().downgrade()),
            })
            .when(self.connected, |column| {
                column.child(
                    Button::new("notion-disconnect")
                        .small()
                        .ghost()
                        .disabled(self.busy)
                        .label("Disconnect")
                        .on_click(move |_, _, cx| {
                            disconnect.update(cx, |setup, cx| setup.disconnect(cx));
                        }),
                )
            })
    }
}

#[cfg(test)]
mod tests {
    use super::source_rule;

    #[test]
    fn a_source_is_blank_or_a_real_id() {
        assert!(source_rule("").is_ok());
        assert!(source_rule("  ").is_ok());
        assert!(source_rule("11111111-1111-1111-1111-111111111111").is_ok());
        assert!(source_rule("11111111111111111111111111111111").is_ok());
        assert!(source_rule("tasks").is_err());
    }
}
