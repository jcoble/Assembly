//! `GroupHeader` heads one of the list's three groups. Settled's folds and opens on a click; Working's and Done's
//! are only a label.

use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::status::Status;

/// A group's header line.
pub const GROUP_HEIGHT: Pixels = px(28.);

type OnToggle = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct GroupHeader {
    status: Status,
    open: bool,
    on_toggle: Option<OnToggle>,
}

impl GroupHeader {
    pub fn new(status: Status, open: bool) -> Self {
        GroupHeader {
            status,
            open,
            on_toggle: None,
        }
    }

    /// Makes the header fold and open its group (Settled's).
    pub fn on_toggle(
        mut self,
        on_toggle: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_toggle = Some(Box::new(on_toggle));
        self
    }
}

impl RenderOnce for GroupHeader {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let foldable = self.on_toggle.is_some();
        let chevron = foldable.then(|| {
            Icon::new(if self.open {
                IconName::ChevronDown
            } else {
                IconName::ChevronRight
            })
            .xsmall()
        });
        h_flex()
            .id(SharedString::from(format!("group:{}", self.status.wire())))
            .w_full()
            .h(GROUP_HEIGHT)
            .items_end()
            .gap_1()
            .px_3()
            .pb_1()
            .text_xs()
            .font_weight(FontWeight::SEMIBOLD)
            .text_color(theme.muted_foreground)
            .children(chevron)
            .child(self.status.label())
            .when_some(self.on_toggle, |header, on_toggle| {
                header
                    .cursor_pointer()
                    .hover(|header| header.text_color(theme.foreground))
                    .on_click(on_toggle)
            })
    }
}
