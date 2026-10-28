//! `AttachmentChip` is one image attached to the draft, above it in the composer: a pill (the approved look's context
//! chip) with a spinner while it uploads, its name and size, and an × that removes it. One whose upload failed is red
//! and says why on hover. G0 draws no thumbnail. Replaces the chips in Composer.svelte.

use std::rc::Rc;

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::attachment::{Attachment, AttachmentState};

/// The widest a name shows before it's cut short.
const NAME_WIDTH: Pixels = px(220.);

/// Removes the chip's attachment.
pub type OnRemove = Rc<dyn Fn(&mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct AttachmentChip {
    pub id: u64,
    pub name: SharedString,
    pub size: u64,
    pub uploading: bool,
    /// Why its upload failed.
    pub failed: Option<SharedString>,
    pub on_remove: OnRemove,
}

impl AttachmentChip {
    pub fn of(attachment: &Attachment, on_remove: OnRemove) -> Self {
        AttachmentChip {
            id: attachment.id,
            name: attachment.name.clone(),
            size: attachment.size,
            uploading: matches!(attachment.state, AttachmentState::Uploading),
            failed: match &attachment.state {
                AttachmentState::Failed(why) => Some(why.clone()),
                _ => None,
            },
            on_remove,
        }
    }
}

impl RenderOnce for AttachmentChip {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let color = if self.failed.is_some() {
            theme.danger
        } else {
            theme.secondary_foreground
        };
        let on_remove = self.on_remove;
        let failed = self.failed.clone();
        h_flex()
            .id(("attachment", self.id))
            .h(px(22.))
            .pl_2()
            .pr_1()
            .gap_1p5()
            .rounded(theme.radius_full())
            .bg(theme.secondary)
            .text_xs()
            .text_color(color)
            .when_some(failed, |chip, why| {
                chip.tooltip(move |window, cx| Tooltip::new(why.clone()).build(window, cx))
            })
            .map(|chip| match (self.uploading, self.failed.is_some()) {
                (true, _) => chip.child(Spinner::new().xsmall()),
                (_, true) => chip.child(Icon::new(Glyph::CircleAlert).xsmall()),
                _ => chip.child(Icon::new(Glyph::Image).xsmall()),
            })
            .child(div().max_w(NAME_WIDTH).truncate().child(self.name.clone()))
            .child(
                div()
                    .text_color(theme.muted_foreground)
                    .child(Attachment::size_text(self.size)),
            )
            .child(
                div()
                    .id(("attachment-remove", self.id))
                    .size(px(16.))
                    .flex()
                    .items_center()
                    .justify_center()
                    .rounded(theme.radius_full())
                    .cursor_pointer()
                    .text_color(theme.muted_foreground)
                    .hover(|remove| {
                        remove
                            .bg(theme.secondary_hover)
                            .text_color(theme.foreground)
                    })
                    .tooltip(move |window, cx| {
                        Tooltip::new(format!("Remove {}", self.name)).build(window, cx)
                    })
                    .on_click(move |_, window, cx| on_remove(window, cx))
                    .child(Icon::new(Glyph::X).xsmall()),
            )
    }
}
