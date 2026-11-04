use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::features::component_gallery::ComponentGallery;
use crate::features::{projects, settings::preview as settings_preview};
use gpui_kit::component::button::Button;
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::*;

pub struct Preview {
    focus: FocusHandle,
    projects: Entity<projects::ProjectsSampleScreen>,
    settings: Entity<settings_preview::SettingsPreview>,
    show_settings: bool,
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
                                s.show_settings = false;
                                s.show_gallery = false;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("settings")
                            .outline()
                            .label("Settings")
                            .on_click(cx.listener(|s, _, _, cx| {
                                s.show_settings = true;
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
                    } else if self.show_settings {
                        self.settings.clone().into_any_element()
                    } else {
                        self.projects.clone().into_any_element()
                    }),
            )
    }
}

impl Preview {
    pub fn show_settings(&mut self, settings: bool, cx: &mut Context<Self>) {
        self.show_settings = settings;
        self.show_gallery = false;
        cx.notify();
    }

    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let projects = cx.new(|cx| projects::ProjectsSampleScreen::new(window, cx));
        let navigation = cx.subscribe(&projects, |view: &mut Preview, _, event, cx| match event {
            projects::ProjectsEvent::ManageConnections { machine } => {
                let name = machine.map(projects::Machine::label);
                view.settings
                    .update(cx, |settings, cx| settings.focus_machine(name, cx));
                view.show_settings = true;
                view.show_gallery = false;
                cx.notify();
            }
        });
        Self {
            focus: cx.focus_handle(),
            projects,
            settings: cx.new(|cx| settings_preview::SettingsPreview::new(window, cx)),
            show_settings: false,
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
