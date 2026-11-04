//! `AttachmentCard` is one image attached to the draft, above it in the box: its thumbnail, an × on the thumbnail that
//! removes it, its name and size. A click on the thumbnail opens it full size. While it uploads a spinner sits on the
//! thumbnail; one whose upload failed is red and says why on hover.

use std::rc::Rc;
use std::sync::Arc;

use gpui_kit::assets::IconName as Glyph;
use crate::core::ui::spinner::Spinner;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::features::session::composer::attachment::{Attachment, AttachmentState};

/// Removes the card's attachment, or opens it full size.
pub type OnCard = Rc<dyn Fn(&mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct AttachmentCard {
    pub id: u64,
    pub name: SharedString,
    pub size: u64,
    pub uploading: bool,
    pub preview: Option<Arc<Image>>,
    /// Why its upload failed.
    pub failed: Option<SharedString>,
    pub on_open: OnCard,
    pub on_remove: OnCard,
}

impl AttachmentCard {
    pub fn of(attachment: &Attachment, on_open: OnCard, on_remove: OnCard) -> Self {
        AttachmentCard {
            id: attachment.id,
            name: attachment.name.clone(),
            size: attachment.size,
            preview: attachment.shown_image(),
            uploading: matches!(attachment.state, AttachmentState::Uploading),
            failed: match &attachment.state {
                AttachmentState::Failed(why) => Some(why.clone()),
                _ => None,
            },
            on_open,
            on_remove,
        }
    }
}

impl RenderOnce for AttachmentCard {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (on_open, on_remove) = (self.on_open, self.on_remove);
        let thumbnail = div()
            .id(("attachment-open", self.id))
            .relative()
            .size_full()
            .flex()
            .items_center()
            .justify_center()
            .rounded(theme.radius)
            .overflow_hidden()
            .bg(theme.muted)
            .cursor_pointer()
            .on_click(move |_, window, cx| on_open(window, cx))
            .map(|thumbnail| match self.preview {
                Some(image) => thumbnail.child(img(image).size_full().object_fit(ObjectFit::Cover)),
                None => thumbnail.child(
                    Icon::new(if self.failed.is_some() {
                        Glyph::CircleAlert
                    } else {
                        Glyph::Image
                    })
                    .small()
                    .text_color(theme.muted_foreground),
                ),
            })
            .when(self.uploading, |thumbnail| {
                thumbnail.child(
                    div()
                        .absolute()
                        .size_full()
                        .flex()
                        .items_center()
                        .justify_center()
                        .child(Spinner::new().small()),
                )
            });
        let name = self.name.clone();
        let remove = div()
            .id(("attachment-remove", self.id))
            .absolute()
            .top(px(3.))
            .right(px(3.))
            .size(px(20.))
            .flex()
            .items_center()
            .justify_center()
            .rounded(theme.radius_full())
            .cursor_pointer()
            .bg(theme.background.opacity(0.72))
            .text_color(theme.foreground)
            .hover(|remove| remove.bg(theme.danger).text_color(theme.danger_foreground))
            .tooltip(move |window, cx| Tooltip::new(format!("Remove {name}")).build(window, cx))
            .on_click(move |_, window, cx| on_remove(window, cx))
            .child(Icon::new(Glyph::X).size(px(13.)));
        let failed = self.failed.clone();
        h_flex()
            .id(("attachment", self.id))
            .min_w(px(210.))
            .max_w(px(280.))
            .gap_2()
            .p(px(6.))
            .border_1()
            .border_color(theme.border)
            .rounded(theme.radius)
            .bg(theme.secondary)
            .when_some(failed, |card, why| {
                card.tooltip(move |window, cx| Tooltip::new(why.clone()).build(window, cx))
            })
            .child(
                div()
                    .relative()
                    .flex_none()
                    .w(px(60.))
                    .h(px(52.))
                    .child(thumbnail)
                    .child(remove),
            )
            .child(
                v_flex()
                    .flex_1()
                    .min_w_0()
                    .gap(px(2.))
                    .child(
                        div()
                            .truncate()
                            .text_size(px(13.))
                            .text_color(if self.failed.is_some() {
                                theme.danger
                            } else {
                                theme.foreground
                            })
                            .child(self.name),
                    )
                    .when(self.size > 0, |caption| {
                        caption.child(
                            div()
                                .text_size(px(12.))
                                .text_color(theme.muted_foreground)
                                .child(Attachment::size_text(self.size)),
                        )
                    }),
            )
    }
}
