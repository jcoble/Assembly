#[path = "ui_preview/support.rs"]
mod core;
// The preview exercises a subset of the reusable field API.
#[allow(dead_code)]
#[path = "../src/components/inline_field/mod.rs"]
mod inline_field;
#[path = "../src/features/projects/mod.rs"]
mod projects;
#[path = "../src/features/settings/preview/mod.rs"]
mod settings_preview;

use gpui_kit::component::button::Button;
use gpui_kit::component::{ActiveTheme as _, Theme, ThemeMode, h_flex, v_flex};
use gpui_kit::*;

struct Preview {
    projects: Entity<projects::ProjectsSampleScreen>,
    settings: Entity<settings_preview::SettingsPreview>,
    show_settings: bool,
    show_fields: bool,
    fields: Entity<inline_field::preview::InlineFieldPreview>,
    _navigation: Subscription,
}

impl Render for Preview {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        v_flex()
            .size_full()
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
                                s.show_fields = false;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("settings")
                            .outline()
                            .label("Settings")
                            .on_click(cx.listener(|s, _, _, cx| {
                                s.show_settings = true;
                                s.show_fields = false;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("inline-fields")
                            .outline()
                            .label("Inline fields")
                            .on_click(cx.listener(|s, _, _, cx| {
                                s.show_fields = true;
                                cx.notify();
                            })),
                    )
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Assembly UI Preview · sample data only"),
                    ),
            )
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .child(if self.show_fields {
                        self.fields.clone().into_any_element()
                    } else if self.show_settings {
                        self.settings.clone().into_any_element()
                    } else {
                        self.projects.clone().into_any_element()
                    }),
            )
    }
}

fn main() {
    gpui_kit::application()
        .with_assets(core::assets::AppAssets)
        .with_quit_mode(QuitMode::LastWindowClosed)
        .run(|cx| {
            gpui_kit::init(cx);
            inline_field::init(cx);
            core::state::preferences::Preferences::init(cx);
            Theme::change(ThemeMode::Dark, None, cx);
            gpui_kit::open_window(
                WindowOptions {
                    titlebar: Some(TitlebarOptions {
                        title: Some("Assembly UI Preview — Sample Data".into()),
                        ..Default::default()
                    }),
                    window_bounds: Some(WindowBounds::Windowed(Bounds::centered(
                        None,
                        size(px(1160.), px(850.)),
                        cx,
                    ))),
                    ..Default::default()
                },
                cx,
                |window, cx| {
                    cx.new(|cx| {
                        let projects = cx.new(|cx| projects::ProjectsSampleScreen::new(window, cx));
                        let navigation = cx.subscribe(
                            &projects,
                            |view: &mut Preview, _, event, cx| match event {
                                projects::ProjectsEvent::ManageConnections { machine } => {
                                    let name = machine.map(projects::Machine::label);
                                    view.settings.update(cx, |settings, cx| {
                                        settings.focus_machine(name, cx)
                                    });
                                    view.show_settings = true;
                                    view.show_fields = false;
                                    cx.notify();
                                }
                            },
                        );
                        Preview {
                            projects,
                            settings: cx
                                .new(|cx| settings_preview::SettingsPreview::new(window, cx)),
                            show_settings: false,
                            show_fields: false,
                            fields: cx.new(|cx| {
                                inline_field::preview::InlineFieldPreview::new(window, cx)
                            }),
                            _navigation: navigation,
                        }
                    })
                },
            )
            .expect("open sample preview");
            cx.activate(true);
        });
}
