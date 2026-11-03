//! Find belongs to the focused Sessions pane; another feature retains its own key context.
use super::sessions_pane::SessionsPane;
use gpui_kit::*;

#[derive(Clone, Default, PartialEq, Action)]
#[action(namespace = sessions, no_json)]
pub struct FocusSessionSearch;

pub(super) fn register_search_keys(cx: &mut App) {
    let context =
        KeyBindingContextPredicate::parse("Sessions").expect("the Sessions key context parses");
    let binding = KeyBinding::load(
        "secondary-f",
        Box::new(FocusSessionSearch),
        Some(context.into()),
        false,
        None,
        cx,
    )
    .expect("the Sessions Find key binding parses");
    cx.bind_keys([binding]);
}
impl SessionsPane {
    pub(super) fn focus_search(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.filter.update(cx, |input, cx| input.focus(window, cx));
    }
}
