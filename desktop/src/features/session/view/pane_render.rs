//! The session pane as a dock panel: registered as the Chat pane, following the selected session, and its drawing: the
//! header, the timeline, the plan card, the status line and the composer, top to bottom.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, panel_handle, register_panel,
};
use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use std::rc::Rc;

use super::SessionPane;
use crate::core::engine::SessionKey;
use crate::core::intents::open_session::OpenSession;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::center_panes::CenterPanes;
use crate::features::session::client::session_client::SessionClient;
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::core::ui::ask::form::AskForm;
use crate::features::session::composer::answer_panel::AnswerPanel;
use crate::features::session::composer::shape::next_answer;
use crate::features::session::view::ask_row::AskRow;
use crate::features::session::view::plan_card::{PlanCard, turn_file_changes};
use crate::features::session::view::session_action::SessionAction;

impl SessionPane {
    /// Registers the session pane as the layout's Chat pane, over the socket client, showing the selected session.
    /// It runs after the placeholders register, since the last registration for a name wins. The pane also owns
    /// the open session intent, which selects that session.
    pub fn register(cx: &mut App) {
        register_panel(cx, "Chat", |_, window, cx| {
            let pane = cx.new(|cx| SessionPane::chat(Rc::new(SocketSessionClient), window, cx));
            CenterPanes::remember("Chat", PanelId::from(pane.entity_id()), cx);
            panel_handle(pane)
        });
        cx.on_action(|intent: &OpenSession, cx| {
            let request=intent.clone();
            let Some(handle)=cx.active_window().or_else(||cx.windows().into_iter().next()) else {return;};
            cx.defer(move|cx|{let _=handle.update(cx,|_,window,cx| {
                if let Some(pane)=CenterPanes::find::<SessionPane>("Chat",cx) {
                    pane.update(cx,|pane,_|pane.uncover_route(request.route_machine.as_deref(),&request.id));
                    pane.update(cx,|pane,cx|pane.request_current_route(request.route_machine,request.id,request.request_id,
                        if request.historical {crate::core::intents::session_open::SessionOpenTarget::Historical} else {crate::core::intents::session_open::SessionOpenTarget::Engine},window,cx));
                } else {
                    SelectedSession::global(cx).update(cx,|_,cx|cx.emit(crate::core::intents::session_open::SessionOpenResult {
                        request_id:request.request_id,key:None,result:Err(super::opening::refused("chat_unavailable","The registered Chat pane is unavailable.")),
                    }));
                }
            });});
        });
        cx.on_action(|request:&crate::core::intents::session_open::SessionOpenRequest,cx| {
            let request=request.clone();
            let Some(handle)=cx.active_window().or_else(||cx.windows().into_iter().next()) else {return;};
            cx.defer(move|cx|{let _=handle.update(cx,|_,window,cx| {
                if let Some(pane)=CenterPanes::find::<SessionPane>("Chat",cx) {
                    pane.update(cx,|pane,_|pane.uncover(&request.key));
                    pane.update(cx,|pane,cx|pane.request_open(request,window,cx));
                } else {
                    SelectedSession::global(cx).update(cx,|_,cx|cx.emit(crate::core::intents::session_open::SessionOpenResult {
                        request_id:request.request_id,key:Some(request.key),result:Err(super::opening::refused("chat_unavailable","The registered Chat pane is unavailable.")),
                    }));
                }
            });});
        });
    }

    /// Only the navigator commits prepared rows; selection notifications never start another open.
    pub(super) fn chat(client:Rc<dyn SessionClient>,window:&mut Window,cx:&mut Context<Self>)->Self {
        SessionPane::new(client,window,cx)
    }
}

impl EventEmitter<PanelEvent> for SessionPane {}

impl Focusable for SessionPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SessionPane {
    /// The chat pane's name in the saved layout (g0-shape's `CHAT`).
    fn panel_name(&self) -> &'static str {
        "Chat"
    }

    /// A permanent tab, as the layout's other panes.
    fn closable(&self, _cx: &App) -> bool {
        false
    }
}

impl Panel for SessionPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        "Chat"
    }

    /// Alone in its group it draws no kit title row: its header is its title row, with the sessions button.
    fn title_bar(&self, _cx: &App) -> bool {
        false
    }
}

impl SessionPane {
    /// Puts a new session's draft in the Chat pane, in front. False when there's no Chat pane to hold it.
    pub fn open_draft(draft: AnyView, window: &mut Window, cx: &mut App) -> bool {
        CenterPanes::open("Chat", window, cx, |window, cx| {
            cx.new(|cx| SessionPane::chat(Rc::new(SocketSessionClient), window, cx))
        });
        let Some(pane) = CenterPanes::find::<SessionPane>("Chat", cx) else {
            return false;
        };
        pane.update(cx, |pane, cx| {
            pane.draft = Some(draft);
            cx.notify();
        });
        true
    }

    /// Takes this draft back out, showing the session under it.
    pub fn close_draft(draft: EntityId, cx: &mut App) {
        if let Some(pane) = CenterPanes::find::<SessionPane>("Chat", cx) {
            pane.update(cx, |pane, cx| {
                if pane.draft.as_ref().is_some_and(|shown| shown.entity_id() == draft) {
                    pane.draft = None;
                    cx.notify();
                }
            });
        }
    }

    /// This exact session is on screen. An id alone doesn't say so: another Engine can hold the same one.
    pub(in crate::features::session::view) fn shows(&self, key: &SessionKey) -> bool {
        self.session.as_deref() == Some(key.id.as_str())
            && self.row_source.as_ref().is_some_and(|source| source.machine == key.machine)
    }

    /// Going to the session already under the draft uncovers it; it opens no differently, so nothing else would.
    fn uncover(&mut self, key: &SessionKey) {
        if self.shows(key) {
            self.draft = None;
        }
    }

    /// The same for a request that names the route to the session, not its Engine.
    fn uncover_route(&mut self, route: Option<&str>, session: &str) {
        if self.session.as_deref() == Some(session)
            && self.row_source.as_ref().is_some_and(|source| source.route_machine.as_deref() == route)
        {
            self.draft = None;
        }
    }

    /// The ask the box answers next, its form made the first time it's drawn: text fields need the window.
    /// The chip shows for a plan, or for a turn that changed files without one.
    fn plan_chip(&self) -> Option<PlanCard> {
        let items = (self.view.plan.as_ref()).map_or_else(Vec::new, |(_, items)| items.clone());
        let changes = turn_file_changes(&self.view.items);
        (!items.is_empty() || changes.is_some()).then(|| PlanCard {
            items,
            changes,
            open: self.plan_open,
            running: self.view.active_run(self.known_run.as_deref()).is_some(),
            on_action: self.on_action.clone(),
        })
    }

    fn answer_panel(&mut self, window: &mut Window, cx: &mut App) -> Option<AnswerPanel> {
        let (ask_id, ask, count) = next_answer(&self.view.items)
            .map(|(ask_id, ask, count)| (ask_id.to_string(), ask.clone(), count))?;
        if !self.forms.contains_key(&ask_id)
            && let Some(form) = AskForm::of(&ask, window, cx)
        {
            self.forms.insert(ask_id.clone(), form);
        }
        let form = self.forms.get(&ask_id).cloned();
        let on_action = AskRow {
            ask_id: ask_id.clone(),
            ask: ask.clone(),
            form: None,
            on_action: self.on_action.clone(),
        }
        .on_control();
        Some(AnswerPanel { ask_id, ask, form, count, on_action })
    }
}

impl Render for SessionPane {
    /// The header, the timeline, the plan card, the status (why the last call failed, or "Offline, showing the saved
    /// session") and the composer, top to bottom.
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if let Some(draft) = self.draft.clone() {
            return div().size_full().child(draft).into_any_element();
        }
        if self.reveal.is_some() && !self.frame_asked {
            self.frame_asked = true;
            cx.on_next_frame(window, |pane, window, cx| pane.reveal_frame(window, cx));
        }
        if !self.growing.is_empty() && !self.grow_asked {
            self.grow_asked = true;
            cx.on_next_frame(window, |pane, window, cx| pane.grow_frame(window, cx));
        }
        let answer = self.answer_panel(window, cx);
        self.composer.update(cx, |composer, _| composer.set_answer(answer));
        div()
            .size_full()
            .flex()
            .flex_col()
            .on_key_down(cx.listener(|pane, event: &KeyDownEvent, _, cx| {
                if pane.plan_open && event.keystroke.key == "escape" {
                    pane.plan_open = false;
                    cx.notify();
                }
            }))
            .child(self.header.clone())
            .child(div().flex_1().min_h_0().child(self.timeline.clone()))
            .when_some(self.plan_chip(), |pane, chip| pane.child(chip))
            .when(!self.status.is_empty(), |pane| {
                let on_action = self.on_action.clone();
                pane.child(
                    h_flex()
                        .px_4()
                        .py_1()
                        .gap_2()
                        .text_sm()
                        .text_color(cx.theme().danger)
                        .child(self.status.clone())
                        .when(self.retry_first.is_some(), |line| {
                            line.child(
                                Button::new("retry-page")
                                    .ghost()
                                    .compact()
                                    .label("Try again")
                                    .on_click(move |_, window, cx| {
                                        on_action(SessionAction::RetryPage, window, cx)
                                    }),
                            )
                        }),
                )
            })
            .when(!self.read_only, |pane| pane.child(self.composer.clone()))
            .into_any_element()
    }
}

#[cfg(test)]
#[path = "navigation/draft_tests.rs"]
mod draft_tests;
