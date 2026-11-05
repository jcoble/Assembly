use crate::core::intents::open_settings::OpenSettings;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::features::component_gallery::ComponentGallery;
use crate::features::projects;
use gpui_kit::component::button::Button;
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::*;

pub struct Preview {
    focus: FocusHandle,
    projects: Entity<projects::ProjectsSampleScreen>,
    show_gallery: bool,
    gallery: Entity<ComponentGallery>,
    _navigation: Subscription,
}

impl Render for Preview {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        v_flex()
            .size_full()
            .track_focus(&self.focus)
            .bg(cx.theme().background)
            .text_color(cx.theme().foreground)
            .child(
                h_flex()
                    .gap_2()
                    .p_3()
                    .border_b_1()
                    .border_color(cx.theme().border)
                    .child(
                        Button::new("home")
                            .outline()
                            .label("Projects")
                            .on_click(cx.listener(|s, _, _, cx| {
                                s.show_gallery = false;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("components")
                            .outline()
                            .label("Components")
                            .on_click(cx.listener(|s, _, _, cx| {
                                s.show_gallery = true;
                                cx.notify();
                            })),
                    )
                    .child(
                        div()
                            .text_role(TextRole::Body, cx)
                            .text_color(cx.theme().muted_foreground)
                            .child("Assembly UI Preview · sample data only"),
                    ),
            )
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .child(if self.show_gallery {
                        self.gallery.clone().into_any_element()
                    } else {
                        self.projects.clone().into_any_element()
                    }),
            )
    }
}

impl Preview {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let projects = cx.new(|cx| projects::ProjectsSampleScreen::new(window, cx));
        // The sample's Manage… opens the real Settings tab, where the machines are.
        let navigation =
            cx.subscribe_in(&projects, window, |_, _, event, window, cx| match event {
                projects::ProjectsEvent::ManageConnections => {
                    window.dispatch_action(Box::new(OpenSettings), cx)
                }
            });
        Self {
            focus: cx.focus_handle(),
            projects,
            show_gallery: false,
            gallery: cx.new(|cx| ComponentGallery::new(window, cx)),
            _navigation: navigation,
        }
    }
}
impl Focusable for Preview {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}
