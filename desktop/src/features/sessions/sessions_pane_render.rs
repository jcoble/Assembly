//! `SessionsPane`'s panel traits (name, title, focus) and its render: the header, filters, search and rail list.
use super::*;
use crate::activity::{DotChip, Tone};
use crate::core::ui::access::say;

impl EventEmitter<PanelEvent> for SessionsPane {}

impl Focusable for SessionsPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SessionsPane {
    /// The sessions pane's name in the saved layout (g0-shape's `SESSIONS`).
    fn panel_name(&self) -> &'static str {
        "Sessions"
    }

    /// The sessions column is a permanent pane.
    fn closable(&self, _cx: &App) -> bool {
        false
    }
}

impl Panel for SessionsPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        "Sessions"
    }
}

impl Render for SessionsPane {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let (muted, danger) = (cx.theme().muted_foreground, cx.theme().danger);
        let border = cx.theme().border.opacity(0.6);
        let state = SocketClient::try_global(cx).map(|socket| socket.read(cx).state().clone());
        let pane = cx.entity().downgrade();
        let on_view: OnView = Rc::new(move |view, _: &mut Window, cx: &mut App| {
            pane.update(cx, |pane, cx| pane.set_view(view, cx)).ok();
        });
        let new_session = header_button(Button::new("new-session"), cx)
            .control(ids::NEW_SESSION, "New session")
            .icon(IconName::Plus)
            .tooltip("New session")
            .on_click(cx.listener(|pane, _, window, cx| pane.show_draft(window, cx)));
        self.follow_entries(window, cx);
        self.running_drawn.set(false);
        let list = list(self.lines.clone(), self.line_renderer(cx)).flex_1();
        let empty = match self.rail.loaded {
            true => "No sessions here.",
            false => empty_line(state.as_ref()),
        };
        v_flex()
            .key_context("Sessions")
            .on_action(cx.listener(
                |pane, _: &super::super::search_keys::FocusSessionSearch, window, cx| {
                    pane.focus_search(window, cx)
                },
            ))
            .track_focus(&self.focus)
            .size_full()
            .child(self.search(on_view, new_session, cx))
            // The pills, then a rule across the card, as the mock's `.pills` has.
            .child(
                v_flex()
                    .border_b_1()
                    .border_color(border)
                    .child(self.pills.clone())
                    // Its own line under the pills, so it never covers one: the rows' amber question chip.
                    .children(needs_words(self.rail.totals.needs).map(|words| {
                        h_flex()
                            .justify_end()
                            .mt(px(-2.))
                            .pr(px(12.))
                            .pb(px(8.))
                            .child(DotChip::new(words, Tone::Warning).named(ids::NEEDS_YOU))
                    })),
            )
            // Above the list, which takes all the height there is and would push this out of the window.
            .when_some(self.row_error.clone(), |pane, error| {
                pane.child(
                    say(ids::ROW_ERROR, error)
                        .px_3()
                        .py_1()
                        .text_role(TextRole::Body, cx)
                        .text_color(danger),
                )
            })
            .map(|pane| match self.entries.is_empty() {
                false => pane.child(list),
                true => pane.child(
                    say(ids::EMPTY, empty)
                        .px_3()
                        .py_2()
                        .text_role(TextRole::Body, cx)
                        .text_color(muted),
                ),
            })
            .when_some(self.rail.error.clone(), |pane, error| {
                pane.child(
                    h_flex()
                        .px_3()
                        .py_1()
                        .gap_2()
                        .text_role(TextRole::Body, cx)
                        .text_color(danger)
                        .child(say(ids::RAIL_ERROR, error))
                        .child(
                            Button::new("retry-rail")
                                .control(ids::RETRY, "Retry loading sessions")
                                .ghost()
                                .compact()
                                .label("Retry")
                                .on_click(cx.listener(|pane, _, _, cx| {
                                    let read = pane.rail.changed();
                                    pane.send(read, cx);
                                })),
                        ),
                )
            })
    }
}
