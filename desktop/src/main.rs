//! Assembly's window in GPUI (GPUI Kit). It draws what the .NET backend computes, over one socket. For now it opens
//! one empty window and quits when that window closes; the G0 lanes fill in `core` and `features`.

mod core;
mod features;

use gpui_kit::*;

use crate::core::theme::{self, theme_id::ThemeId};

fn main() {
    // On macOS GPUI's default keeps the app running with no window open.
    gpui_kit::application()
        .with_quit_mode(QuitMode::LastWindowClosed)
        .run(|cx| {
            gpui_kit::init(cx);
            // The window opens once the theme files have loaded, already in its theme.
            theme::init(cx, |cx| {
                let options = WindowOptions {
                    titlebar: Some(TitlebarOptions {
                        title: Some("Assembly".into()),
                        ..Default::default()
                    }),
                    ..Default::default()
                };
                gpui_kit::open_window(options, cx, |window, cx| {
                    theme::set(ThemeId::DEFAULT, window, cx);
                    cx.new(|_| EmptyView)
                })
                .expect("open the window");
                cx.activate(true);
            });
        });
}
