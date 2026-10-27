//! Assembly's window in GPUI (GPUI Kit). It draws what the .NET backend computes, over one socket. For now it opens
//! one empty window and quits when that window closes; the G0 lanes fill in `core` and `features`.

mod core;
mod features;

use std::path::PathBuf;

use gpui_kit::*;

use crate::core::backend::backend_config::BackendConfig;
use crate::core::backend::launcher::Launcher;

fn main() {
    // On macOS GPUI's default keeps the app running with no window open.
    gpui_kit::application()
        .with_quit_mode(QuitMode::LastWindowClosed)
        .run(|cx| {
            gpui_kit::init(cx);
            start_backend(cx);
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

/// Starts the backend from `ASSEMBLY_BACKEND_DIR`, or the `backend` folder beside the program. Without one the window
/// still opens, and the error says what's missing.
fn start_backend(cx: &mut App) {
    let backend = std::env::var_os("ASSEMBLY_BACKEND_DIR")
        .map(PathBuf::from)
        .or_else(|| {
            let program = std::env::current_exe().ok()?;
            Some(program.parent()?.join("backend"))
        });
    let remote = std::env::var("ASSEMBLY_REMOTE")
        .ok()
        .filter(|remote| !remote.is_empty());
    match backend
        .ok_or("no backend folder".to_string())
        .and_then(|dir| BackendConfig::read(&dir, remote))
    {
        // The socket client (lane g0-socket) starts on the first ready and reconnects on each later one.
        Ok(config) => Launcher::start(
            config,
            |port, _token, _cx| println!("backend ready on port {port}"),
            cx,
        ),
        Err(error) => eprintln!("no backend: {error}"),
    }
}
