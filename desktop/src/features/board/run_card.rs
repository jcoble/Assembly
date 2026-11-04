//! What a run opens into: its steps, a waiting step's question in place, its footer's actions and their toast.
use std::time::Duration;

use chrono::{Local, TimeZone as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;
use serde_json::{Value, json};
use uuid::Uuid;

use super::board_commands::{
    BoardRunAction, refusal_line, run_command, selected_after, undo_token,
};
use super::board_queries::BoardData;
use super::board_sections::offline_reason;
use super::question_row::{QuestionRows, need_card};
use super::run_footer::{Footer, RunAction, footer};
use super::run_steps::{card_steps, visible_steps};
use crate::activity::{StepView, step_list};
use crate::core::engine::workflow_items::{RunItem, StepItem};
use crate::core::engine::{EngineFeed, EngineKey};
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::activity::run_reader::{RunReader, RunSource};
use crate::core::ui::activity::run_view::now_ms;
use crate::core::ui::row_drop_down::RowDropDowns;

/// How long Keep's toast and Undo last. The engine's own expiry still wins.
const TOAST: Duration = Duration::from_secs(5);

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RunCardEvent {
    /// A new run the engine accepted, to select once its row arrives.
    Select(EngineKey),
}

struct Toast {
    words: SharedString,
    _expires: Task<()>,
}

pub struct RunCard {
    key: EngineKey,
    feed: Entity<EngineFeed>,
    reader: Entity<RunReader>,
    steps: Entity<RowDropDowns>,
    questions: Entity<QuestionRows>,
    show_all: bool,
    tools_called: Option<i64>,
    usage_version: Option<i64>,
    usage: Option<Task<()>>,
    action: Option<Task<()>>,
    refusal: Option<SharedString>,
    toast: Option<Toast>,
    undo: Option<String>,
    _watching: [Subscription; 3],
}

impl EventEmitter<RunCardEvent> for RunCard {}

impl RunCard {
    pub fn new(key: EngineKey, cx: &mut Context<Self>) -> Self {
        let feed = EngineFeed::global(cx);
        let reader = cx.new(|cx| {
            RunReader::new(
                RunSource::Run {
                    machine: key.machine.clone(),
                    id: key.id.clone(),
                },
                cx,
            )
        });
        let steps = cx.new(|_| RowDropDowns::default());
        let question_drops = cx.new(|_| RowDropDowns::default());
        let questions = cx.new(|_| QuestionRows::new(feed.clone(), question_drops, None));
        let watching = [
            cx.observe(&reader, |card, _, cx| {
                card.read_usage(cx);
                cx.notify();
            }),
            cx.observe(&steps, |_, _, cx| cx.notify()),
            cx.observe(&questions, |_, _, cx| cx.notify()),
        ];
        Self {
            key,
            feed,
            reader,
            steps,
            questions,
            show_all: false,
            tools_called: None,
            usage_version: None,
            usage: None,
            action: None,
            refusal: None,
            toast: None,
            undo: None,
            _watching: watching,
        }
    }

    /// One read per change of a run that finished, since that's the only footer showing it.
    fn read_usage(&mut self, cx: &mut Context<Self>) {
        let Some(run) = self.reader.read(cx).view().map(|page| page.run.clone()) else {
            return;
        };
        if run.state != "succeeded" || self.usage_version == Some(run.version) {
            return;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        self.usage_version = Some(run.version);
        let args = json!({"entityKind": "run", "entityId": run.id, "machine": run.machine});
        let call = socket.read(cx).call::<Value>("engine", "usage", args, None);
        self.usage = Some(cx.spawn(async move |card, cx| {
            let Outcome::Ok(totals) = call.await else {
                return;
            };
            let _ = card.update(cx, |card, cx| {
                card.tools_called = totals["toolCalls"].as_i64();
                cx.notify();
            });
        }));
    }

    fn act(&mut self, action: RunAction, cx: &mut Context<Self>) {
        if self.action.is_some() {
            return;
        }
        let Some(run) = self.reader.read(cx).view().map(|page| page.run.clone()) else {
            return;
        };
        let board_action = match action {
            RunAction::Pause => BoardRunAction::Pause,
            RunAction::Resume => BoardRunAction::Resume,
            RunAction::Stop => BoardRunAction::Cancel,
            RunAction::Rerun => BoardRunAction::Rerun,
            RunAction::Retry(_) => BoardRunAction::RetryFailed,
            RunAction::KeepSucceeded => BoardRunAction::Keep,
            RunAction::Undo => BoardRunAction::Undo {
                token: self.undo.clone().unwrap_or_default(),
            },
        };
        self.send(&run, board_action, cx);
    }

    fn send(&mut self, run: &RunItem, action: BoardRunAction, cx: &mut Context<Self>) {
        let command_id = Uuid::new_v4().to_string();
        let new_run = action
            .starts_a_new_run()
            .then(|| Uuid::new_v4().to_string());
        let request_key = Uuid::new_v4().to_string();
        let command = match run_command(
            run,
            &action,
            command_id.clone(),
            new_run.clone(),
            request_key,
        ) {
            Ok(command) => command,
            Err(error) => {
                self.refusal = Some(error.message.into());
                cx.notify();
                return;
            }
        };
        self.refusal = None;
        let call = self.feed.update(cx, |feed, cx| feed.submit(command, cx));
        let machine = run.machine.clone();
        self.action = Some(cx.spawn(async move |card, cx| {
            let outcome = call.await;
            let _ = card.update(cx, |card, cx| {
                card.action = None;
                card.refusal = refusal_line(&outcome).map(Into::into);
                if let Some(id) = selected_after(&action, new_run.as_deref(), &outcome) {
                    cx.emit(RunCardEvent::Select(EngineKey {
                        machine,
                        kind: "run".into(),
                        id,
                    }));
                }
                if let Some(token) = undo_token(&action, &command_id, &outcome) {
                    card.show_keep_toast(token, cx);
                }
                if matches!(action, BoardRunAction::Undo { .. })
                    && matches!(&outcome, Ok(receipt) if receipt.applied)
                {
                    card.undo = None;
                    card.toast = None;
                }
                if card.refusal.is_some() && matches!(action, BoardRunAction::Undo { .. }) {
                    card.undo = None;
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    fn show_keep_toast(&mut self, token: String, cx: &mut Context<Self>) {
        let steps = self
            .reader
            .read(cx)
            .view()
            .map(|page| page.steps.clone())
            .unwrap_or_default();
        let kept = steps
            .iter()
            .filter(|step| step.state == "succeeded")
            .count();
        let closed = steps
            .iter()
            .filter(|step| matches!(step.state.as_str(), "failed" | "cancelled"))
            .count();
        self.undo = Some(token);
        self.toast = Some(Toast {
            words: format!("Kept what succeeded · {kept} finished items kept, {closed} closed")
                .into(),
            _expires: cx.spawn(async move |card, cx| {
                cx.background_executor().timer(TOAST).await;
                let _ = card.update(cx, |card, cx| {
                    card.toast = None;
                    card.undo = None;
                    cx.notify();
                });
            }),
        });
    }

    fn grant(&mut self, step_id: String, scope: String, cx: &mut Context<Self>) {
        if self.action.is_some() {
            return;
        }
        let Some(run) = self.reader.read(cx).view().map(|page| page.run.clone()) else {
            return;
        };
        self.send(&run, BoardRunAction::Grant { step_id, scope }, cx);
    }

    pub(super) fn started_line(run: &RunItem, data: &BoardData) -> Option<String> {
        let owner = run.owner_session_id.as_ref().and_then(|id| {
            data.sessions
                .values()
                .find(|session| session.key.machine == run.machine && session.key.id == *id)
        });
        let owner = owner.map(|session| {
            session
                .title
                .clone()
                .unwrap_or_else(|| session.provider.clone())
        });
        let started = run
            .started_ms
            .and_then(|ms| Local.timestamp_millis_opt(ms).single())
            .map(|time| time.format("%H:%M").to_string());
        match (owner, started) {
            (Some(owner), Some(started)) => Some(format!("{owner} · Started {started}")),
            (None, Some(started)) => Some(format!("Started {started}")),
            (Some(owner), None) => Some(owner),
            (None, None) => None,
        }
    }

    fn menu(&self, run: &RunItem, cx: &mut Context<Self>) -> impl IntoElement + use<> {
        let weak = cx.weak_entity();
        let id = run.id.clone();
        let live = !matches!(run.state.as_str(), "succeeded" | "failed" | "cancelled");
        Button::new(SharedString::from(format!("run-menu:{}", run.id)))
            .ghost()
            .xsmall()
            .icon(IconName::Ellipsis)
            .accessibility_label("Run actions")
            .dropdown_menu(move |menu, _, _| {
                let id = id.clone();
                let menu =
                    menu.item(PopupMenuItem::new("Copy run id").on_click(move |_, _, cx| {
                        cx.write_to_clipboard(ClipboardItem::new_string(id.clone()))
                    }));
                if !live {
                    return menu;
                }
                let weak = weak.clone();
                menu.item(PopupMenuItem::new("Stop").on_click(move |_, _, cx| {
                    let _ = weak.update(cx, |card, cx| card.act(RunAction::Stop, cx));
                }))
            })
    }

    fn footer_row(
        &self,
        footer: Footer,
        offline: Option<SharedString>,
        cx: &mut Context<Self>,
    ) -> Div {
        let muted = cx.theme().muted_foreground;
        let busy = self.action.is_some();
        h_flex()
            .gap_2()
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .child(footer.summary),
            )
            .children(footer.actions.into_iter().map(|action| {
                let button = Button::new(SharedString::from(format!(
                    "run-action:{}:{}",
                    self.key.id,
                    action.label()
                )))
                .xsmall()
                .label(action.label())
                .disabled(busy || offline.is_some())
                .on_click(cx.listener(move |card, _, _, cx| card.act(action, cx)));
                let button = match action {
                    RunAction::Stop => button.ghost(),
                    RunAction::Retry(_) | RunAction::Rerun => button.primary(),
                    _ => button.outline(),
                };
                match &offline {
                    Some(reason) => button.tooltip(reason.clone()),
                    None => button,
                }
            }))
    }
}

/// The open question a waiting step shows in place, if any.
fn waiting_at(steps: &[&StepItem], data: &BoardData, machine: &str) -> Option<(usize, EngineKey)> {
    steps.iter().enumerate().find_map(|(index, step)| {
        step.needs_me_ids.iter().find_map(|id| {
            let key = EngineKey {
                machine: machine.to_owned(),
                kind: "needs_me".into(),
                id: id.clone(),
            };
            data.needs
                .get(&key)
                .filter(|need| need.state == "open")
                .map(|_| (index, key))
        })
    })
}

impl RunCard {
    /// Drawn by whoever holds it, from its current read of the shared rows.
    pub fn draw(&mut self, data: &BoardData, cx: &mut Context<Self>) -> AnyElement {
        self.reader.update(cx, |reader, cx| reader.refresh(cx));
        let muted = cx.theme().muted_foreground;
        let now = now_ms();
        let Some(page) = self.reader.read(cx).view().cloned() else {
            let words = self
                .reader
                .read(cx)
                .error()
                .map_or("Loading workflow…".to_owned(), str::to_owned);
            return div()
                .text_role(TextRole::Meta, cx)
                .text_color(muted)
                .child(words)
                .into_any_element();
        };
        let run = &page.run;
        let all = page.steps.iter().collect::<Vec<_>>();
        let (visible, more) = if self.show_all {
            (all.clone(), 0)
        } else {
            visible_steps(&all)
        };
        let waiting = waiting_at(&visible, data, &run.machine);
        let rows = card_steps(run, &visible, now);
        let list_key = SharedString::from(run.id.clone());
        let split = waiting
            .as_ref()
            .map_or(rows.len(), |(index, _)| index + 1)
            .min(rows.len());
        let (before, after): (Vec<StepView>, Vec<StepView>) =
            (rows[..split].to_vec(), rows[split..].to_vec());
        let question = waiting
            .as_ref()
            .and_then(|(_, key)| data.needs.get(key))
            .map(|need| {
                let feed = self.feed.read(cx);
                need_card(need, &self.questions, data, feed, now, cx)
            });
        let keep = waiting
            .iter()
            .map(|(_, key)| key.clone())
            .collect::<Vec<_>>();
        self.questions.update(cx, |rows, cx| rows.retain(&keep, cx));
        let summary = footer(
            run,
            &all,
            self.tools_called,
            waiting.is_some(),
            self.undo.is_some(),
            now,
        );
        let offline = offline_reason(&run.machine, self.feed.read(cx), cx);
        let grants = all
            .iter()
            .filter(|step| step.state == "waiting" || step.state == "failed")
            .filter_map(|step| {
                step.required_scope
                    .clone()
                    .filter(|_| !step.scope_granted)
                    .map(|scope| (step.id.clone(), scope))
            })
            .collect::<Vec<_>>();
        let started = Self::started_line(run, data);
        let menu = self.menu(run, cx);
        let footer = self.footer_row(summary, offline.clone(), cx);
        let steps = self.steps.clone();
        let busy = self.action.is_some();
        v_flex()
            .gap_2()
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .text_role(TextRole::Meta, cx)
                            .text_color(muted)
                            .children(started),
                    )
                    .child(menu),
            )
            .child(step_list(list_key.clone(), before, &steps, cx))
            .children(question)
            .children((!after.is_empty()).then(|| step_list(list_key, after, &steps, cx)))
            .children((more > 0).then(|| {
                Button::new(SharedString::from(format!("run-more:{}", run.id)))
                    .ghost()
                    .xsmall()
                    .label(format!("{more} more after these"))
                    .on_click(cx.listener(|card, _, _, cx| {
                        card.show_all = true;
                        cx.notify();
                    }))
            }))
            .children(grants.into_iter().map(|(step, scope)| {
                let target = step.clone();
                let granted = scope.clone();
                h_flex()
                    .gap_2()
                    .child(
                        div()
                            .flex_1()
                            .text_role(TextRole::Meta, cx)
                            .text_color(muted)
                            .child(format!("Needs {scope}")),
                    )
                    .child(
                        Button::new(SharedString::from(format!("run-grant:{step}")))
                            .xsmall()
                            .outline()
                            .label("I granted it")
                            .disabled(busy || offline.is_some())
                            .on_click(cx.listener(move |card, _, _, cx| {
                                card.grant(target.clone(), granted.clone(), cx)
                            })),
                    )
            }))
            .children(self.refusal.clone().map(|line| {
                div()
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().danger)
                    .child(line)
            }))
            .children(self.toast.as_ref().map(|toast| {
                div()
                    .text_role(TextRole::Meta, cx)
                    .child(toast.words.clone())
            }))
            .child(footer)
            .into_any_element()
    }
}
