//! `ProjectHeader` heads one project inside a group: its name, its machine when it isn't this computer, and a New
//! session button that opens the new-session form with the project's folder and machine filled in.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::*;

use super::list_entries::project_label;
use super::session_row::ROW_HEIGHT;
use super::status::Status;

type OnNew = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct ProjectHeader {
    status: Status,
    folder: String,
    machine: Option<String>,
    on_new: OnNew,
}

impl ProjectHeader {
    pub fn new(
        status: Status,
        folder: String,
        machine: Option<String>,
        on_new: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        ProjectHeader {
            status,
            folder,
            machine,
            on_new: Box::new(on_new),
        }
    }
}

impl RenderOnce for ProjectHeader {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let name = match &self.machine {
            Some(machine) => format!("{} · {machine}", project_label(&self.folder)),
            None => project_label(&self.folder).to_string(),
        };
        let id = format!(
            "project:{}:{}:{}",
            self.status.wire(),
            self.machine.as_deref().unwrap_or(""),
            self.folder
        );
        h_flex()
            .w_full()
            .h(ROW_HEIGHT)
            .items_end()
            .justify_between()
            .gap_2()
            .pl_3()
            .pr_1()
            .child(
                div()
                    .pb_1()
                    .min_w_0()
                    .text_sm()
                    .font_weight(FontWeight::MEDIUM)
                    .text_color(theme.foreground)
                    .truncate()
                    .child(name),
            )
            .child(
                Button::new(SharedString::from(id))
                    .ghost()
                    .compact()
                    .icon(IconName::Plus)
                    .tooltip("New session in this project")
                    .on_click(self.on_new),
            )
    }
}
