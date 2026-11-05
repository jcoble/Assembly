//! A need on a card: what it asks, who asked and how long ago, with the shared answer row as its body.
use std::collections::HashMap;
use std::rc::Rc;

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, v_flex};
use gpui_kit::*;

use super::board_queries::BoardData;
use super::question_actions::explain_more;
use crate::activity::{CardBody, NeedsCard, NeedsState, StatusChip, Tone};
use crate::core::engine::engine_items::{NeedItem, WORK_REDONE};
use crate::core::engine::{EngineFeed, EngineKey, EngineScope, SessionKey};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::ask::answer_words::answered_words;
use crate::core::ui::ask::need_row::{NeedRow, NeedRowEvent};
use crate::core::ui::row_drop_down::RowDropDowns;

const DAY_MS: i64 = 86_400_000;

pub fn card_key(key: &EngineKey) -> SharedString {
    format!("{}:{}", key.machine, key.id).into()
}

fn kind_label(need: &NeedItem) -> &'static str {
    match need.kind.as_str() {
        "permission" => "Permission",
        "confirmation" | "workflow_gate" => "Approval",
        "choice" => "Choice",
        _ => "Question",
    }
}

/// What the row is called. A number or short title shows only when the engine recorded one.
pub fn title(need: &NeedItem) -> String {
    let words = need
        .presentation
        .as_ref()
        .and_then(|presentation| presentation.title.clone())
        .or_else(|| need.text.clone())
        .unwrap_or_else(|| "Loading the question…".into());
    let first_line = words.lines().next().unwrap_or_default().to_owned();
    match need.display_number {
        Some(number) => format!("Q{number} · {first_line}"),
        None => first_line,
    }
}

/// "4m ago", and once it's more than a day old, the stale "asked 2 days ago".
pub fn age(asked_ms: i64, now_ms: i64) -> (String, bool) {
    let ago = (now_ms - asked_ms).max(0);
    if ago >= DAY_MS {
        let days = ago / DAY_MS;
        return (
            format!("asked {days} day{} ago", if days == 1 { "" } else { "s" }),
            true,
        );
    }
    let minutes = ago / 60_000;
    let words = match minutes {
        0 => "just now".to_owned(),
        1..=59 => format!("{minutes}m ago"),
        _ => format!("{}h ago", minutes / 60),
    };
    (words, false)
}

/// What an answered card shrinks to. Who answered comes only from what the engine recorded.
pub fn card_state(need: &NeedItem) -> NeedsState {
    let by = need
        .answered_origin
        .as_ref()
        .map(|origin| match origin.surface.as_deref() {
            Some(surface) => format!("Answered in the {surface}"),
            None => format!("Answered on {}", origin.channel),
        })
        .or_else(|| {
            need.answered_by
                .as_ref()
                .map(|by| format!("Answered by {by}"))
        });
    match need.state.as_str() {
        "open" => NeedsState::Open,
        "answered" => NeedsState::Answered {
            summary: need
                .answer_text
                .as_deref()
                .map_or_else(|| "Answered".into(), answered_words)
                .into(),
            delivery: Some(by.unwrap_or_else(|| "Answered elsewhere".into()).into()),
        },
        "dismissed" => NeedsState::Answered {
            summary: "Dismissed".into(),
            delivery: None,
        },
        "cancelled" => NeedsState::Answered {
            summary: "Session ended".into(),
            delivery: None,
        },
        "superseded" => NeedsState::Answered {
            summary: WORK_REDONE.into(),
            delivery: None,
        },
        other => NeedsState::Answered {
            summary: other.to_owned().into(),
            delivery: None,
        },
    }
}

/// "for Main orchestrator" when it's addressed to a parent, then who asked and where.
pub fn asked_by(asker: &str, machine: &str, parent: Option<&str>) -> String {
    match parent {
        Some(parent) => format!("{asker} · {machine} · for {parent}"),
        None => format!("{asker} · {machine}"),
    }
}

/// Who's asking, in words, from the shared rows. A row that hasn't arrived reads as a plain session.
pub fn need_card(
    need: &NeedItem,
    rows: &Entity<QuestionRows>,
    data: &BoardData,
    feed: &EngineFeed,
    now_ms: i64,
    cx: &App,
) -> NeedsCard {
    let name = |key: &SessionKey| {
        data.session(key).map_or_else(
            || "A session".to_owned(),
            |session| {
                session
                    .title
                    .clone()
                    .unwrap_or_else(|| session.provider.clone())
            },
        )
    };
    let asker = name(&need.session);
    let parent = need.addressee().map(|parent| name(&parent));
    let machine = super::board_sections::machine_label(&need.key.machine, feed, cx);
    let time = need.created_ms.map(|asked| age(asked, now_ms).0.into());
    let stale = need.created_ms.is_some_and(|asked| age(asked, now_ms).1);
    let recommended = need
        .presentation
        .as_ref()
        .and_then(|presentation| presentation.recommendation.clone());
    let reader = rows.read(cx);
    NeedsCard {
        key: card_key(&need.key),
        kind: kind_label(need).into(),
        title: title(need).into(),
        why: asked_by(&asker, &machine, parent.as_deref()).into(),
        next_step: recommended.map(|text| format!("Recommended: {text}").into()),
        chips: if stale {
            vec![StatusChip::new("Old", Tone::Warning).into_any_element()]
        } else {
            Vec::new()
        },
        time,
        state: card_state(need),
        drops: reader.drops().clone(),
        body: QuestionRows::body(rows.downgrade(), need.key.clone(), need),
    }
}

/// The answer rows behind the cards in one list. Each is made when its card first opens and dropped when it leaves the list.
pub struct QuestionRows {
    feed: Entity<EngineFeed>,
    drops: Entity<RowDropDowns>,
    rows: HashMap<EngineKey, (Entity<NeedRow>, Subscription)>,
    refusals: HashMap<EngineKey, SharedString>,
    explaining: HashMap<EngineKey, Task<()>>,
    viewed: Option<SessionKey>,
}

impl QuestionRows {
    pub fn new(
        feed: Entity<EngineFeed>,
        drops: Entity<RowDropDowns>,
        viewed: Option<SessionKey>,
    ) -> Self {
        Self {
            feed,
            drops,
            rows: HashMap::new(),
            refusals: HashMap::new(),
            explaining: HashMap::new(),
            viewed,
        }
    }

    pub fn drops(&self) -> &Entity<RowDropDowns> {
        &self.drops
    }

    /// Only rows still in the list stay alive, so a draft goes with its row and nothing leaks past a close.
    pub fn retain(&mut self, keys: &[EngineKey], cx: &mut Context<Self>) {
        self.rows.retain(|key, _| keys.contains(key));
        self.refusals.retain(|key, _| keys.contains(key));
        self.explaining.retain(|key, _| keys.contains(key));
        let shown = keys.iter().map(card_key).collect::<Vec<_>>();
        self.drops.update(cx, |drops, cx| {
            drops.retain_keys(|key| shown.contains(key), cx)
        });
    }

    fn row(
        &mut self,
        key: &EngineKey,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Option<Entity<NeedRow>> {
        if let Some((row, _)) = self.rows.get(key) {
            return Some(row.clone());
        }
        let scope = EngineScope {
            machine: key.machine.clone(),
            topic: "needs_me".into(),
            key: None,
        };
        let made = match self.viewed.clone() {
            Some(viewed) => NeedRow::create_for(
                viewed,
                key.clone(),
                scope,
                self.feed.clone(),
                "board",
                window,
                cx,
            ),
            None => NeedRow::create(key.clone(), scope, self.feed.clone(), "board", window, cx),
        };
        let row = made.ok()?;
        row.update(cx, |row, cx| row.open(window, cx));
        let drops = self.drops.clone();
        let card = card_key(key);
        let settled = cx.subscribe(&row, move |_, _, _: &NeedRowEvent, cx| {
            NeedsCard::settle(&card, &drops, cx);
            cx.notify();
        });
        self.rows.insert(key.clone(), (row.clone(), settled));
        Some(row)
    }

    /// Sends one message. A second press waits for the first, and a refusal shows and isn't sent again.
    pub fn explain(&mut self, key: &EngineKey, cx: &mut Context<Self>) {
        if self.explaining.contains_key(key) {
            return;
        }
        let feed = self.feed.read(cx);
        let need = feed.need(key);
        let asker = need.as_ref().and_then(|need| feed.session(&need.session));
        let (Some(need), Some(asker)) = (need, asker) else {
            self.refusals.insert(
                key.clone(),
                "The session that asked is not available.".into(),
            );
            cx.notify();
            return;
        };
        let command = match explain_more(&need, &asker) {
            Ok(command) => command,
            Err(error) => {
                self.refusals.insert(key.clone(), error.message.into());
                cx.notify();
                return;
            }
        };
        let call = self.feed.update(cx, |feed, cx| feed.submit(command, cx));
        let held = key.clone();
        self.refusals.remove(key);
        self.explaining.insert(
            key.clone(),
            cx.spawn(async move |rows, cx| {
                let outcome = call.await;
                let _ = rows.update(cx, |rows, cx| {
                    rows.explaining.remove(&held);
                    if let Some(line) = super::board_commands::refusal_line(&outcome) {
                        rows.refusals.insert(held, line.into());
                    }
                    cx.notify();
                });
            }),
        );
        cx.notify();
    }

    /// What the card opens into: the shared answer row, and Explain more for a question still open.
    pub fn body(this: WeakEntity<Self>, key: EngineKey, need: &NeedItem) -> CardBody {
        let explainable = need.kind == "question" || need.kind == "choice";
        Rc::new(move |window, cx| {
            let Some(rows) = this.upgrade() else {
                return Empty.into_any_element();
            };
            let row = rows.update(cx, |rows, cx| rows.row(&key, window, cx));
            let Some(row) = row else {
                return div()
                    .text_role(TextRole::Body, cx)
                    .text_color(cx.theme().muted_foreground)
                    .child("Loading the question…")
                    .into_any_element();
            };
            let offline = {
                let feed = rows.read(cx).feed.read(cx);
                super::board_sections::offline_reason(&key.machine, feed, cx)
            };
            row.update(cx, |row, cx| row.set_offline(offline, cx));
            let open = row.read(cx).need().state == "open";
            let refusal = rows.read(cx).refusals.get(&key).cloned();
            let (sending, for_click, target) = (
                rows.read(cx).explaining.contains_key(&key),
                rows.downgrade(),
                key.clone(),
            );
            v_flex()
                .gap_2()
                .child(row)
                .children((open && explainable).then(|| {
                    Button::new(SharedString::from(format!("explain:{}", card_key(&key))))
                        .ghost()
                        .label("Explain more")
                        .disabled(sending)
                        .on_click(move |_, _, cx| {
                            let _ = for_click.update(cx, |rows, cx| rows.explain(&target, cx));
                        })
                }))
                .children(refusal.map(|line| {
                    div()
                        .text_role(TextRole::Meta, cx)
                        .text_color(cx.theme().muted_foreground)
                        .child(line)
                }))
                .into_any_element()
        })
    }
}

#[cfg(test)]
#[path = "question_row_tests.rs"]
mod tests;
