//! A live session's row and what it opens into: its questions, workflow, sub-agents and PRs. Only the selected tab's body
//! is made, and a tab's holds go when the row does.
use std::collections::{HashMap, HashSet};
use std::rc::Rc;

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, Size, h_flex, v_flex};
use gpui_kit::*;

use super::board_panel::{BoardPanel, local_midnight_ms};
use super::board_prefs::SessionTab;
use super::board_queries::{BoardData, session_questions};
use super::board_sections::machine_label;
use super::question_row::{QuestionRows, card_key, need_card};
use super::run_card::{RunCard, RunCardEvent};
use crate::activity::{
    CardBody, LinkChip, SessionRowValues, StatusChip, Tone, format_elapsed, session_row,
};
use crate::core::engine::engine_items::{PrItem, SessionItem};
use crate::core::engine::workflow_items::{RunItem, StepItem};
use crate::core::engine::{EngineFeed, EngineKey, SessionKey};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::activity::run_view::now_ms;
use crate::core::ui::row_drop_down::{GrowingBody, RowDropDowns};
use crate::core::ui::three_line_row::row_height;
use crate::features::sessions::drop_down::native_details::NativeDetails;
use gpui_kit::assets::IconName;

pub fn default_tab(open_questions: usize, has_run: bool) -> SessionTab {
    if open_questions > 0 {
        SessionTab::Questions
    } else if has_run {
        SessionTab::Workflow
    } else {
        SessionTab::SubAgents
    }
}

/// A question outranks what it's doing, since that's what the owner can act on.
pub fn row_chip(session: &SessionItem, open_owner_questions: usize) -> (String, Tone) {
    if open_owner_questions > 0 {
        let plural = if open_owner_questions == 1 { "" } else { "s" };
        return (
            format!("{open_owner_questions} question{plural}"),
            Tone::Warning,
        );
    }
    match session.state.as_str() {
        "running" | "starting" => ("Working".into(), Tone::Info),
        "lost" => ("Lost".into(), Tone::Danger),
        _ if session.run_id.is_some() => ("Waiting".into(), Tone::Neutral),
        _ => ("Idle".into(), Tone::Neutral),
    }
}

fn folder_name(path: &str) -> &str {
    path.rsplit('/')
        .find(|part| !part.is_empty())
        .unwrap_or(path)
}

/// "Step 3 of 9 · Workbox Test · tsk-1615-mute", else provider, model and folder.
pub fn second_line(
    session: &SessionItem,
    run: Option<&RunItem>,
    steps: &[&StepItem],
    machine: &str,
) -> String {
    if run.is_some() {
        let done = steps
            .iter()
            .filter(|step| step.state == "succeeded")
            .count();
        let at = format!(
            "Step {} of {}",
            (done + 1).min(steps.len().max(1)),
            steps.len()
        );
        let branch = run
            .and_then(|run| run.branch.as_deref())
            .or(session.branch.as_deref());
        return [Some(at.as_str()), Some(machine), branch]
            .into_iter()
            .flatten()
            .collect::<Vec<_>>()
            .join(" · ");
    }
    let mut parts = vec![session.provider.as_str()];
    parts.extend(session.model.as_deref());
    parts.push(folder_name(&session.folder));
    parts.join(" · ")
}

/// The task's key when its link names one, since that's what's read on other screens.
pub fn task_label(url: &str) -> SharedString {
    let found = url
        .split(|part: char| !(part.is_ascii_alphanumeric() || part == '-'))
        .find(|part| part.starts_with("TSK-"));
    found.map_or_else(|| "Task".into(), |key| key.to_owned().into())
}

fn open_owner_questions(session: &SessionKey, data: &BoardData) -> usize {
    data.needs
        .values()
        .filter(|need| {
            need.state == "open" && need.addressee_session_id.is_none() && need.session == *session
        })
        .count()
}

/// The row the Sessions tab and a parent's Sub-agents tab both draw, with its body beneath it.
pub fn session_entry(
    session: &SessionItem,
    data: &BoardData,
    feed: &EngineFeed,
    drops: &Entity<RowDropDowns>,
    body: CardBody,
    now: i64,
    cx: &App,
) -> AnyElement {
    let key = card_key(&session.key);
    let run = data.run_of(session);
    let steps = run.map(|run| data.steps_of(run)).unwrap_or_default();
    let (chip, tone) = row_chip(session, open_owner_questions(&session.session_key(), data));
    let prs = data.prs_for(session);
    let machine = machine_label(&session.key.machine, feed, cx);
    let task = session
        .task_url
        .as_deref()
        .or(run.and_then(|run| run.task_url.as_deref()));
    let url = task.map(str::to_owned);
    let lead_color = tone.color(cx);
    let lead = div()
        .size(px(6.))
        .rounded_full()
        .bg(lead_color)
        .into_any_element();
    let mut chips = vec![StatusChip::new(chip, tone).into_any_element()];
    if let Some(url) = url {
        let label = task_label(&url);
        chips.push(
            LinkChip::new(format!("task:{key}"), IconName::ExternalLink, label)
                .on_click(move |_, _, cx| cx.open_url(&url))
                .into_any_element(),
        );
    }
    let values = SessionRowValues {
        key: key.clone(),
        lead,
        title: session
            .title
            .clone()
            .unwrap_or_else(|| session.provider.clone())
            .into(),
        chips,
        task: Some(second_line(session, run, &steps, &machine).into()),
        pr: prs.first().map(|pr| pr.label().into()),
        worktree: Some(
            folder_name(
                run.and_then(|run| run.worktree.as_deref())
                    .unwrap_or(&session.folder),
            )
            .to_owned()
            .into(),
        ),
        project: session
            .project_id
            .clone()
            .or(run.and_then(|run| run.project_id.clone()))
            .map(Into::into),
        step: steps
            .iter()
            .find(|step| step.state == "running")
            .map(|step| step.label_present.clone().into()),
        time: session.created_ms.map(|start| format_elapsed(now - start)),
        body,
    };
    let row = session_row(values, drops, cx);
    v_flex()
        .w_full()
        .child(row)
        .child(GrowingBody::new(key, drops.clone(), row_height(cx)).max_rows(None))
        .into_any_element()
}

pub struct SessionTabs {
    session: SessionKey,
    host: WeakEntity<BoardPanel>,
    feed: Entity<EngineFeed>,
    selected: Option<SessionTab>,
    questions: Entity<QuestionRows>,
    children: Entity<RowDropDowns>,
    /// Only the selected tab's holds exist, so a row's read and watches go when its tab or the row does.
    run_card: Option<(EngineKey, Entity<RunCard>, Subscription)>,
    native: Option<Entity<NativeDetails>>,
    child_tabs: HashMap<EngineKey, Entity<SessionTabs>>,
    _watching: [Subscription; 3],
}

impl SessionTabs {
    pub fn new(
        session: SessionKey,
        host: WeakEntity<BoardPanel>,
        selected: Option<SessionTab>,
        cx: &mut Context<Self>,
    ) -> Self {
        let feed = EngineFeed::global(cx);
        let question_drops = cx.new(|_| RowDropDowns::default());
        let viewed = Some(session.clone());
        let questions = cx.new(|_| QuestionRows::new(feed.clone(), question_drops.clone(), viewed));
        let children = cx.new(|_| RowDropDowns::default());
        let watching = [
            cx.observe(&question_drops, |_, _, cx| cx.notify()),
            cx.observe(&questions, |_, _, cx| cx.notify()),
            cx.observe(&children, |_, _, cx| cx.notify()),
        ];
        Self {
            session,
            host,
            feed,
            selected,
            questions,
            children,
            run_card: None,
            native: None,
            child_tabs: HashMap::new(),
            _watching: watching,
        }
    }

    pub fn selected(&self, data: &BoardData, session: &SessionItem) -> SessionTab {
        self.selected.unwrap_or_else(|| {
            let open = data
                .needs
                .values()
                .filter(|need| {
                    need.state == "open"
                        && (need.session == self.session
                            || need.addressee().as_ref() == Some(&self.session))
                })
                .count();
            default_tab(open, session.run_id.is_some())
        })
    }

    fn select(&mut self, tab: SessionTab, cx: &mut Context<Self>) {
        self.selected = Some(tab);
        if tab != SessionTab::SubAgents {
            self.native = None;
            self.child_tabs.clear();
        }
        if tab != SessionTab::Workflow {
            self.run_card = None;
        }
        let key = format!("{}:{}", self.session.machine, self.session.id);
        let _ = self
            .host
            .update(cx, |panel, cx| panel.remember_session_tab(key, tab, cx));
        cx.notify();
    }

    fn muted(words: impl Into<SharedString>, cx: &App) -> AnyElement {
        div()
            .text_role(TextRole::Meta, cx)
            .text_color(cx.theme().muted_foreground)
            .child(words.into())
            .into_any_element()
    }

    fn questions_body(
        &mut self,
        data: &BoardData,
        midnight: i64,
        now: i64,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let keys = session_questions(data, &self.session, midnight);
        self.questions.update(cx, |rows, cx| rows.retain(&keys, cx));
        if keys.is_empty() {
            return Self::muted("No questions", cx);
        }
        let feed = self.feed.read(cx);
        let cards = keys
            .iter()
            .filter_map(|key| data.needs.get(key))
            .map(|need| need_card(need, &self.questions, data, feed, now, cx))
            .collect::<Vec<_>>();
        v_flex().gap_2().children(cards).into_any_element()
    }

    fn workflow_body(
        &mut self,
        data: &BoardData,
        session: &SessionItem,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let Some(run) = data.run_of(session) else {
            return Self::muted("No workflow", cx);
        };
        let key = run.key();
        if self.run_card.as_ref().is_none_or(|(held, ..)| *held != key) {
            let card = cx.new(|cx| RunCard::new(key.clone(), cx));
            let watched = cx.subscribe(&card, |view, _, event: &RunCardEvent, cx| {
                let RunCardEvent::Select(key) = event;
                let _ = view
                    .host
                    .update(cx, |panel, cx| panel.select_run(key.clone(), cx));
            });
            self.run_card = Some((key, card, watched));
        }
        let card = self
            .run_card
            .as_ref()
            .map(|(_, card, _)| card.clone())
            .expect("held above");
        card.update(cx, |card, cx| card.draw(data, cx))
    }

    fn agents_body(&mut self, data: &BoardData, now: i64, cx: &mut Context<Self>) -> AnyElement {
        let native = match &self.native {
            Some(native) => native.clone(),
            None => {
                let (parent, route) = (
                    self.session.clone(),
                    self.feed
                        .read(cx)
                        .machine_name(&self.session.machine)
                        .map(str::to_owned),
                );
                let native = cx.new(|cx| NativeDetails::new(parent, route, cx));
                self.native = Some(native.clone());
                native
            }
        };
        let children = self.feed.read(cx).children_of(&self.session);
        let showing = children
            .iter()
            .map(|child| child.key.clone())
            .filter(|key| self.children.read(cx).is_showing(&card_key(key)))
            .collect::<HashSet<_>>();
        self.child_tabs.retain(|key, _| showing.contains(key));
        let this = cx.weak_entity();
        let rows = children
            .iter()
            .map(|child| {
                session_entry(
                    child,
                    data,
                    self.feed.read(cx),
                    &self.children,
                    Self::child_body(this.clone(), child.session_key()),
                    now,
                    cx,
                )
            })
            .collect::<Vec<_>>();
        v_flex()
            .gap_1()
            .children(rows)
            .child(native)
            .into_any_element()
    }

    /// A sub-agent opens into the same tabs as its parent.
    fn child_body(this: WeakEntity<Self>, child: SessionKey) -> CardBody {
        Rc::new(move |_, cx| {
            let Some(this) = this.upgrade() else {
                return Empty.into_any_element();
            };
            this.update(cx, |view, cx| {
                let key = EngineKey {
                    machine: child.machine.clone(),
                    kind: "session".into(),
                    id: child.id.clone(),
                };
                let host = view.host.clone();
                let selected = host
                    .upgrade()
                    .and_then(|host| host.read(cx).remembered_tab(&card_key(&key)));
                let child = child.clone();
                view.child_tabs
                    .entry(key)
                    .or_insert_with(|| cx.new(|cx| SessionTabs::new(child, host, selected, cx)))
                    .clone()
                    .into_any_element()
            })
        })
    }

    fn prs_body(prs: &[&PrItem], list: &SharedString, cx: &App) -> AnyElement {
        if prs.is_empty() {
            return Self::muted("No PRs", cx);
        }
        v_flex()
            .gap_1()
            .children(prs.iter().enumerate().map(|(index, pr)| {
                let muted = cx.theme().muted_foreground;
                let observed = pr
                    .observed_head
                    .as_deref()
                    .map(|head| head.chars().take(7).collect::<String>());
                let tone = match pr.state.as_str() {
                    "merged" => Tone::Success,
                    "closed" => Tone::Neutral,
                    "open" => Tone::Info,
                    _ => Tone::Warning,
                };
                let link: AnyElement = match pr.url.clone() {
                    Some(url) => LinkChip::new(
                        format!("pr:{list}:{index}"),
                        IconName::ExternalLink,
                        pr.label(),
                    )
                    .on_click(move |_, _, cx| cx.open_url(&url))
                    .into_any_element(),
                    None => div().child(pr.label()).into_any_element(),
                };
                h_flex()
                    .gap_2()
                    .child(link)
                    .child(StatusChip::new(pr.state.clone(), tone))
                    .children(observed.map(|head| {
                        div()
                            .text_role(TextRole::Meta, cx)
                            .text_color(muted)
                            .child(head)
                    }))
            }))
            .into_any_element()
    }
}

/// How many each tab holds, for its label: open questions, "x of y" steps, live children and open PRs.
pub fn tab_label(
    tab: SessionTab,
    session: &SessionItem,
    children: &[SessionItem],
    data: &BoardData,
) -> String {
    match tab {
        SessionTab::Questions => {
            let key = session.session_key();
            let open = data
                .needs
                .values()
                .filter(|need| {
                    need.state == "open"
                        && (need.session == key || need.addressee().as_ref() == Some(&key))
                })
                .count();
            format!("Questions · {open}")
        }
        SessionTab::Workflow => match data.run_of(session) {
            Some(run) => {
                let steps = data.steps_of(run);
                format!(
                    "Workflow · {} of {}",
                    steps
                        .iter()
                        .filter(|step| step.state == "succeeded")
                        .count(),
                    steps.len()
                )
            }
            None => "Workflow".to_owned(),
        },
        SessionTab::SubAgents => format!(
            "Sub-agents · {}",
            children
                .iter()
                .filter(|child| child.state != "ended")
                .count()
        ),
        SessionTab::Prs => format!(
            "PRs · {}",
            data.prs_for(session)
                .iter()
                .filter(|pr| pr.state == "open")
                .count()
        ),
    }
}

#[cfg(test)]
impl SessionTabs {
    /// What's been made for this row: its workflow card, then its sub-agents' reader.
    pub(super) fn test_held(&self) -> (bool, bool) {
        (self.run_card.is_some(), self.native.is_some())
    }
}

impl Render for SessionTabs {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let Some(host) = self.host.upgrade() else {
            return v_flex();
        };
        let data = host.read(cx).data();
        let Some(session) = data.session(&self.session).cloned() else {
            return v_flex().child(Self::muted("This session is no longer listed.", cx));
        };
        let selected = self.selected(&data, &session);
        let children = self.feed.read(cx).children_of(&self.session);
        let bar = TabBar::new(SharedString::from(format!(
            "session-tabs:{}",
            card_key(&session.key)
        )))
        .underline()
        .with_size(Size::Small)
        .selected_index(
            SessionTab::ALL
                .iter()
                .position(|tab| *tab == selected)
                .unwrap_or(0),
        )
        .children(SessionTab::ALL.into_iter().map(|tab| {
            Tab::new()
                .label(tab_label(tab, &session, &children, &data))
                .on_click(cx.listener(move |view, _, _, cx| view.select(tab, cx)))
        }));
        let now = now_ms();
        let body = match selected {
            SessionTab::Questions => self.questions_body(&data, local_midnight_ms(), now, cx),
            SessionTab::Workflow => self.workflow_body(&data, &session, cx),
            SessionTab::SubAgents => self.agents_body(&data, now, cx),
            SessionTab::Prs => Self::prs_body(&data.prs_for(&session), &card_key(&session.key), cx),
        };
        let (key, host) = (self.session.clone(), self.host.clone());
        let open = Button::new(SharedString::from(format!(
            "session-open:{}",
            card_key(&session.key)
        )))
        .ghost()
        .xsmall()
        .label("Open session")
        .on_click(move |_, window, cx| {
            let _ = host.update(cx, |panel, cx| panel.open_session(key.clone(), window, cx));
        });
        v_flex()
            .gap_2()
            .child(bar)
            .child(body)
            .child(h_flex().justify_end().child(open))
    }
}

#[cfg(test)]
#[path = "session_tabs_tests.rs"]
mod tests;
