//! Keyboard behavior through the actual input/action handler, not a layout assertion.
use super::sessions_pane::SessionsPane;
use crate::core::state::selected_session::SelectedSession;
use gpui::TestAppContext;
use gpui_kit::{
    AppContext as _, Context, Entity, Focusable as _, InteractiveElement as _, IntoElement,
    ParentElement as _, Render, Styled as _, Window, div, px,
};

#[gpui::test]
fn escape_clears_the_search_first_then_returns_focus_to_the_session_list(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let (pane, cx) = cx.add_window_view(SessionsPane::new);
    pane.update_in(cx, |pane, window, cx| {
        pane.filter.update(cx, |input, cx| {
            input.set_value("actual query", window, cx);
            input.focus(window, cx);
        })
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.simulate_keystrokes("escape");
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.update(|window, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.filter.read(cx).value(), "");
        assert!(pane.filter.read(cx).focus_handle(cx).is_focused(window));
    });
    cx.simulate_keystrokes("escape");
    cx.run_until_parked();
    cx.update(|window, cx| {
        let pane = pane.read(cx);
        assert!(!pane.filter.read(cx).focus_handle(cx).is_focused(window));
        assert!(pane.focus_handle(cx).is_focused(window));
    });
}

#[gpui::test]
fn find_in_the_session_list_focuses_its_search(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    cx.update(super::search_keys::register_search_keys);
    let (pane, cx) = cx.add_window_view(SessionsPane::new);
    pane.update_in(cx, |pane, window, cx| {
        window.focus(&pane.focus_handle(cx), cx)
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.simulate_keystrokes("secondary-f");
    cx.run_until_parked();
    cx.update(|window, cx| {
        assert!(
            pane.read(cx)
                .filter
                .read(cx)
                .focus_handle(cx)
                .is_focused(window)
        )
    });
}

struct OutsideSearch {
    pane: Entity<SessionsPane>,
    other: Entity<gpui_kit::component::input::InputState>,
}
impl Render for OutsideSearch {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        gpui_kit::component::h_flex()
            .size_full()
            .child(div().w(px(350.)).h_full().child(self.pane.clone()))
            .child(
                div()
                    .key_context("AnotherFeature")
                    .child(gpui_kit::component::input::Input::new(&self.other)),
            )
    }
}
#[gpui::test]
fn find_outside_the_session_context_does_not_steal_input_focus(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    cx.update(super::search_keys::register_search_keys);
    let (host, cx) = cx.add_window_view(|window, cx| OutsideSearch {
        pane: cx.new(|cx| SessionsPane::new(window, cx)),
        other: cx.new(|cx| gpui_kit::component::input::InputState::new(window, cx)),
    });
    host.update_in(cx, |host, window, cx| {
        host.other.update(cx, |other, cx| other.focus(window, cx))
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.simulate_keystrokes("secondary-f");
    cx.run_until_parked();
    cx.update(|window, cx| {
        let host = host.read(cx);
        assert!(host.other.read(cx).focus_handle(cx).is_focused(window));
        assert!(
            !host
                .pane
                .read(cx)
                .filter
                .read(cx)
                .focus_handle(cx)
                .is_focused(window)
        );
    });
}
