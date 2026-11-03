use super::{
    post_confirm::{self, PostRequest},
    pr_detail::PrDetail,
    review_draft::ReviewIdentity,
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{InputEvent, Textarea, TextareaState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::*;
struct Composer {
    session: String,
    identity: ReviewIdentity,
    reply: Option<u64>,
    owner: WeakEntity<PrDetail>,
    input: Entity<TextareaState>,
    failure: Option<String>,
    _input: Subscription,
}
pub fn open(
    session: String,
    identity: ReviewIdentity,
    reply: Option<u64>,
    owner: WeakEntity<PrDetail>,
    window: &mut Window,
    cx: &mut App,
) {
    let form = cx.new(|cx| {
        let input = cx.new(|cx| {
            TextareaState::new(window, cx)
                .placeholder(if reply.is_some() { "Reply" } else { "Comment" })
                .auto_grow(4, 10)
        });
        let heard = cx.subscribe(&input, |_: &mut Composer, _, _: &InputEvent, cx| {
            cx.notify()
        });
        Composer {
            session,
            identity,
            reply,
            owner,
            input,
            failure: None,
            _input: heard,
        }
    });
    window.open_dialog(cx, move |dialog, _, _| {
        dialog
            .title(if reply.is_some() {
                "Reply to review thread"
            } else {
                "Comment on pull request"
            })
            .footer(Empty)
            .child(form.clone())
    });
}
impl Composer {
    fn request(&self, cx: &App) -> Result<PostRequest, String> {
        let body = self.input.read(cx).value().to_string();
        if let Some(id) = self.reply {
            PostRequest::reply(self.identity.clone(), id, body)
        } else {
            PostRequest::comment(self.identity.clone(), body)
        }
    }
    fn preview(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let current = self
            .owner
            .upgrade()
            .and_then(|detail| detail.read(cx).current_identity());
        let request = self.request(cx).and_then(|request| {
            let current = current
                .as_ref()
                .ok_or("Refresh the pull request before posting. Your draft is kept.")?;
            request.args(current)?;
            Ok(request)
        });
        match request {
            Ok(request) => {
                window.close_dialog(cx);
                post_confirm::open(
                    self.session.clone(),
                    request,
                    self.owner.clone(),
                    window,
                    cx,
                );
            }
            Err(error) => {
                self.failure = Some(error);
                cx.notify();
            }
        }
    }
}
impl Render for Composer {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let danger = cx.theme().danger;
        v_flex()
            .gap_2()
            .child(Textarea::new(&self.input))
            .children(
                self.failure
                    .clone()
                    .map(|error| div().text_color(danger).child(error)),
            )
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        Button::new("compose-close")
                            .ghost()
                            .label("Cancel")
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    )
                    .child(
                        Button::new("compose-preview")
                            .primary()
                            .label("Preview post")
                            .disabled(self.request(cx).is_err())
                            .on_click(cx.listener(|view, _, window, cx| view.preview(window, cx))),
                    ),
            )
    }
}
