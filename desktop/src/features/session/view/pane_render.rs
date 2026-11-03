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
use crate::core::intents::open_session::OpenSession;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::center_panes::CenterPanes;
use crate::features::session::client::session_client::SessionClient;
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::view::plan_card::PlanCard;
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

impl Render for SessionPane {
    /// The header, the timeline, the plan card, the status (why the last call failed, or "Offline, showing the saved
    /// session") and the composer, top to bottom.
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if self.reveal.is_some() && !self.frame_asked {
            self.frame_asked = true;
            cx.on_next_frame(window, |pane, window, cx| pane.reveal_frame(window, cx));
        }
        if !self.growing.is_empty() && !self.grow_asked {
            self.grow_asked = true;
            cx.on_next_frame(window, |pane, window, cx| pane.grow_frame(window, cx));
        }
        div()
            .size_full()
            .flex()
            .flex_col()
            .child(self.header.clone())
            .child(div().flex_1().min_h_0().child(self.timeline.clone()))
            .when_some(
                self.view
                    .plan
                    .as_ref()
                    .filter(|(_, items)| !items.is_empty()),
                |pane, (_, items)| {
                    pane.child(PlanCard {
                        items: items.clone(),
                        open: self.plan_open,
                        on_action: self.on_action.clone(),
                    })
                },
            )
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
    }
}
