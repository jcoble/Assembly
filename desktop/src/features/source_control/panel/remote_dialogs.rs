use super::git_action::GitAction;
use super::git_target::GitTarget;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::*;
use serde_json::{Value, json};
fn remote_args(session: &str, name: &str, url: &str) -> Result<Value, String> {
    if name.trim().is_empty() || url.trim().is_empty() {
        return Err("A remote name and URL are required.".into());
    }
    Ok(json!({"session":session,"name":name,"url":url}))
}
struct RemoteDialog {
    target: GitTarget,
    name: Entity<InputState>,
    url: Entity<InputState>,
    action: Entity<GitAction>,
    submitted: bool,
    _heard: Vec<Subscription>,
}
pub fn add(target: GitTarget, action: Entity<GitAction>, window: &mut Window, cx: &mut App) {
    let form = cx.new(|cx| {
        let name = cx.new(|cx| InputState::new(window, cx));
        name.update(cx, |name, cx| name.set_value("origin", window, cx));
        let url = cx.new(|cx| InputState::new(window, cx).placeholder("git@github.com:owner/repo.git or a path"));
        let heard = vec![
            cx.subscribe(&name, |_: &mut RemoteDialog, _, _: &InputEvent, cx| {
                cx.notify()
            }),
            cx.subscribe(&url, |_: &mut RemoteDialog, _, _: &InputEvent, cx| {
                cx.notify()
            }),
            // A remote that was added is done with the dialog; a refused one stays to be fixed and tried again.
            cx.observe_in(&action, window, |view: &mut RemoteDialog, action, window, cx| {
                let action = action.read(cx);
                if view.submitted && action.pending.is_none() && action.failure_sentence().is_none() {
                    view.submitted = false;
                    window.close_dialog(cx);
                }
                cx.notify();
            }),
        ];
        RemoteDialog {
            target,
            name,
            url,
            action,
            submitted: false,
            _heard: heard,
        }
    });
    window.open_dialog(cx, move |dialog, _, _| {
        dialog.title("Add remote").width(px(440.)).footer(Empty).child(form.clone())
    });
}
impl Render for RemoteDialog {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let busy = self.action.read(cx).pending.is_some();
        let name = self.name.read(cx).value().to_string();
        let url = self.url.read(cx).value().to_string();
        let danger = cx.theme().danger;
        let field = |label: &'static str, input: &Entity<InputState>| {
            v_flex()
                .gap_1()
                .child(div().text_sm().font_weight(FontWeight::MEDIUM).child(label))
                .child(Input::new(input).disabled(busy))
        };
        v_flex()
            .gap_3()
            .child(field("Name", &self.name))
            .child(field("URL", &self.url))
            .children(
                self.action
                    .read(cx)
                    .failure_sentence()
                    .filter(|_| self.submitted)
                    .map(|error| div().text_color(danger).child(error)),
            )
            .child(
                h_flex()
                    .justify_end()
                    .gap_2()
                    .child(
                        Button::new("remote-cancel")
                            .ghost()
                            .small()
                            .label("Cancel")
                            .disabled(busy)
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    )
                    .child(
                        Button::new("remote-add")
                            .primary()
                            .small()
                            .label(if busy { "Adding…" } else { "Add" })
                            .disabled(busy || remote_args(&self.target.session, &name, &url).is_err())
                            .on_click(cx.listener(|view, _, _, cx| {
                                let name = view.name.read(cx).value().to_string();
                                let url = view.url.read(cx).value().to_string();
                                if let Ok(args) = remote_args(&view.target.session, &name, &url) {
                                    let target = view.target.clone();
                                    view.submitted = view.action.update(cx, |action, cx| {
                                        action.run(&target, "addRemote", args, cx)
                                    });
                                }
                            })),
                    ),
            )
    }
}
#[cfg(test)]
#[path = "remote_dialogs_tests.rs"]
mod tests;
