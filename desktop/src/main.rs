//! Assembly's window in GPUI (GPUI Kit). It draws what the .NET backend computes, over one socket. For now it opens
//! one empty window and quits when that window closes; the G0 lanes fill in `core` and `features`.

mod core;
mod features;

use std::cell::Cell;
use std::path::PathBuf;

use features::shell::frame::Frame;
use gpui_kit::*;

use crate::core::backend::backend_config::BackendConfig;
use crate::core::backend::launcher::Launcher;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::window_root::WindowRoot;
use crate::features::palette::palette_view::PaletteView;

fn main() {
    // On macOS GPUI's default keeps the app running with no window open.
    gpui_kit::application()
        // The kit's icons, such as the dock toggles; without them every icon draws blank.
        .with_assets(gpui_kit::assets::Assets)
        .with_quit_mode(QuitMode::LastWindowClosed)
        .run(|cx| {
            gpui_kit::init(cx);
            features::shell::placeholder_pane::PlaceholderPane::register_all(cx);
            start_backend(cx);
            PaletteView::init(cx);
            features::meter::start_if_on(cx);
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
                let frame =
                    cx.new(|cx| Frame::new(Task::ready(None), Box::new(|_, _| {}), window, cx));
                cx.new(|_| WindowRoot::new(frame))
            })
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
        // The first ready line starts the socket client; each later one (a restarted backend, whose token may be new)
        // has it reconnect.
        Ok(config) => {
            let started = Cell::new(false);
            Launcher::start(
                config,
                move |port, token, cx| {
                    let endpoint = Endpoint { port, token };
                    if started.replace(true) {
                        SocketClient::global(cx).read(cx).reconnect(endpoint);
                    } else {
                        SocketClient::start(endpoint, cx);
                    }
                },
                cx,
            )
        }
        Err(error) => eprintln!("no backend: {error}"),
    }
}
