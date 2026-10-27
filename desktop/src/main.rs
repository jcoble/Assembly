//! Assembly's window in GPUI (GPUI Kit). It draws what the .NET backend computes, over one socket. For now it opens
//! one empty window and quits when that window closes; the G0 lanes fill in `core` and `features`.

mod core;
mod features;

use features::shell::frame::Frame;
use gpui_kit::*;

fn main() {
    // On macOS GPUI's default keeps the app running with no window open.
    gpui_kit::application()
        .with_quit_mode(QuitMode::LastWindowClosed)
        .run(|cx| {
            gpui_kit::init(cx);
            features::shell::placeholder_pane::PlaceholderPane::register_all(cx);
            let options = WindowOptions {
                titlebar: Some(TitlebarOptions {
                    title: Some("Assembly".into()),
                    ..Default::default()
                }),
                ..Default::default()
            };
            // The layout's read and write join the socket client when lane g0-socket lands. Until then the window
            // opens the default layout and keeps changes for this run only.
            gpui_kit::open_window(options, cx, |window, cx| {
                cx.new(|cx| Frame::new(Task::ready(None), Box::new(|_, _| {}), window, cx))
            })
            .expect("open the window");
            cx.activate(true);
        });
}
