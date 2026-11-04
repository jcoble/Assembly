//! Lazy row-local controls over held shared facts; dropping the body drops its input drafts and observer.
use super::need_row::NeedRow;
use crate::core::engine::{
    EngineChanged, EngineFeed, EngineKey, EngineScope, SessionKey, engine_items::SessionItem,
};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, Size, v_flex};
use gpui_kit::*;
use std::collections::HashMap;

pub struct RailDetails {
    /// Which tab shows. Both stay alive while the row is open, since the body is as tall as the taller of them.
    selected: usize,
    workflow: Entity<crate::core::ui::activity::run_view::RunActivity>,
    parent: SessionKey,
    feed: Entity<EngineFeed>,
    dirty: bool,
    needs: Vec<Entity<NeedRow>>,
    held: HashMap<EngineKey, Entity<NeedRow>>,
    children: std::rc::Rc<Vec<SessionItem>>,
    native: Entity<super::native_details::NativeDetails>,
    _changed: Subscription,
}
impl RailDetails {
    pub fn new(parent: SessionKey, route: Option<String>, cx: &mut Context<Self>) -> Self {
        let feed = EngineFeed::global(cx);
        let changed = cx.subscribe(&feed, |row, _, event: &EngineChanged, cx| {
            if event
                .keys
                .iter()
                .any(|key| matches!(key.kind.as_str(), "session" | "needs_me"))
                || event
                    .scopes
                    .iter()
                    .any(|scope| matches!(scope.topic.as_str(), "sessions" | "needs_me"))
            {
                row.dirty = true;
                cx.notify();
            }
        });
        let native =
            cx.new(|cx| super::native_details::NativeDetails::new(parent.clone(), route, cx));
        let workflow =
            cx.new(|cx| crate::core::ui::activity::run_view::RunActivity::new(parent.clone(), cx));
        Self {
            selected: 0,
            workflow,
            native,
            parent,
            feed,
            dirty: true,
            needs: Vec::new(),
            held: HashMap::new(),
            children: std::rc::Rc::default(),
            _changed: changed,
        }
    }
    fn refresh(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.dirty {
            return;
        }
        self.dirty = false;
        self.children = std::rc::Rc::new(self.feed.read(cx).children_of(&self.parent));
        let scope = EngineScope {
            machine: self.parent.machine.clone(),
            topic: "needs_me".into(),
            key: None,
        };
        if !self.feed.read(cx).loaded(&scope) {
            self.needs.clear();
            self.held.clear();
            return;
        }
        let facts = self.feed.read(cx).needs_for(&self.parent);
        let keys = facts
            .iter()
            .map(|need| need.key.clone())
            .collect::<std::collections::HashSet<_>>();
        self.held.retain(|key, _| keys.contains(key));
        self.needs.clear();
        for need in facts {
            if !self.held.contains_key(&need.key)
                && let Ok(row) = NeedRow::create_for(
                    self.parent.clone(),
                    need.key.clone(),
                    EngineScope {
                        machine: need.key.machine.clone(),
                        topic: "needs_me".into(),
                        key: None,
                    },
                    self.feed.clone(),
                    window,
                    cx,
                )
            {
                self.held.insert(need.key.clone(), row);
            }
            if let Some(row) = self.held.get(&need.key) {
                self.needs.push(row.clone());
            }
        }
    }
}
impl Render for RailDetails {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let tabs = TabBar::new("rail-details-tabs")
            .underline()
            .with_size(Size::Small)
            .selected_index(self.selected)
            .child(
                Tab::new()
                    .label("Activity")
                    .on_click(cx.listener(|row, _, _, cx| {
                        row.selected = 0;
                        cx.notify();
                    })),
            )
            .child(
                Tab::new()
                    .label("Workflow")
                    .on_click(cx.listener(|row, _, _, cx| {
                        row.selected = 1;
                        cx.notify();
                    })),
            );
        self.refresh(window, cx);
        let mut column = v_flex()
            .gap_2()
            .children(self.needs.iter().cloned())
            .child(self.native.clone());
        let on_open = std::rc::Rc::new(|key: SessionKey, window: &mut Window, cx: &mut App| {
            window.dispatch_action(
                Box::new(crate::core::intents::session_open::SessionOpenRequest {
                    key,
                    request_id: uuid::Uuid::new_v4().to_string(),
                    target: crate::core::intents::session_open::SessionOpenTarget::Engine,
                }),
                cx,
            )
        });
        column = column.child(super::started_sessions::StartedSessions {
            children: self.children.clone(),
            on_open,
        });
        if self.needs.is_empty() && self.children.is_empty() {
            column = column.child(div().text_color(cx.theme().muted_foreground).child(
                if self.feed.read(cx).loaded(&EngineScope {
                    machine: self.parent.machine.clone(),
                    topic: "needs_me".into(),
                    key: None,
                }) {
                    "No current questions or started sessions."
                } else {
                    "The current owning facts are unavailable."
                },
            ));
        }
        // Everything in the body reads at the size of the row's own detail lines.
        let bodies = crate::core::ui::tab_stack::tab_stack(
            self.selected,
            [
                column.into_any_element(),
                self.workflow.clone().into_any_element(),
            ],
        );
        v_flex()
            .px_3()
            .pb_2()
            .gap_2()
            .text_role(TextRole::Meta, cx)
            .child(tabs)
            .child(bodies)
    }
}

impl crate::features::sessions::sessions_pane::SessionsPane {
    pub(in crate::features::sessions) fn dropdowns(
        cx: &mut Context<Self>,
    ) -> (
        Entity<crate::core::ui::row_drop_down::RowDropDowns>,
        Subscription,
    ) {
        let drops = cx.new(|_| crate::core::ui::row_drop_down::RowDropDowns::default());
        let dropdowns = cx.subscribe(
            &drops,
            |pane, _, event: &crate::core::ui::row_drop_down::RowDropDownEvent, cx| {
                if let crate::core::ui::row_drop_down::RowDropDownEvent::Moved(key) = event
                    && let Some(at) = pane.entries.iter().position(|entry| match entry {
                        crate::features::sessions::list_entry::ListEntry::Session(row) => {
                            super::rail_details_key(row) == *key
                        }
                        _ => false,
                    })
                {
                    pane.lines.remeasure_items(at..at + 1);
                }
                cx.notify();
            },
        );
        (drops, dropdowns)
    }
}
