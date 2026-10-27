//! Assembly's window in GPUI (GPUI Kit). It draws what the .NET backend computes, over one socket. For now it opens
//! one empty window and quits when that window closes; the G0 lanes fill in `core` and `features`.

mod core;
mod features;

use gpui_kit::*;

fn main() {
    // On macOS GPUI's default keeps the app running with no window open.
    gpui_kit::application()
        .with_quit_mode(QuitMode::LastWindowClosed)
        .run(|cx| {
            gpui_kit::init(cx);
            let options = WindowOptions {
                titlebar: Some(TitlebarOptions {
                    title: Some("Assembly".into()),
                    ..Default::default()
                }),
                ..Default::default()
            };
            gpui_kit::open_window(options, cx, |_, cx| cx.new(|_| EmptyView))
                .expect("open the window");
            cx.activate(true);
        });
}
