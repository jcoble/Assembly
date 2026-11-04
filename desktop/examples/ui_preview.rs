#[path = "ui_preview/support.rs"]
mod core;
// The preview exercises a subset of the reusable pieces.
#[allow(dead_code)]
#[path = "../src/components/activity/mod.rs"]
mod activity;
#[allow(dead_code)]
#[path = "../src/features/source_control/git/git_operation.rs"]
mod git_operation;
#[allow(dead_code)]
#[path = "../src/features/source_control/git/git_target.rs"]
mod git_target;
#[allow(dead_code)]
#[path = "../src/components/inline_field/mod.rs"]
mod inline_field;
#[allow(dead_code)]
#[path = "../src/features/projects/mod.rs"]
mod projects;
#[path = "../src/features/settings/preview/mod.rs"]
mod settings_preview;

#[path = "../src/features/component_gallery.rs"]
mod component_gallery;
#[allow(dead_code)]
#[path = "../src/features/preview_screen.rs"]
mod preview_screen;
mod features {
    pub(crate) use crate::component_gallery;
    pub(crate) use crate::projects;
    pub(crate) mod source_control {
        pub(crate) use crate::git_operation;
    }
    pub(crate) mod settings {
        pub(crate) use crate::settings_preview as preview;
    }
}

use gpui_kit::component::{Theme, ThemeMode};
use gpui_kit::*;

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
                |window, cx| cx.new(|cx| preview_screen::Preview::new(window, cx)),
            )
            .expect("open sample preview");
            cx.activate(true);
        });
}
