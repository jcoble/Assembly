//! Assembly's window in GPUI (GPUI Kit). It draws what the .NET backend computes, over one socket. For now it opens
//! one empty window and quits when that window closes; the G0 lanes fill in `core` and `features`.

mod core;
mod features;

use std::cell::{Cell, OnceCell};
use std::path::PathBuf;
use std::rc::Rc;

use features::shell::frame::Frame;
use features::shell::layout_store::{LAYOUT_KEY, WriteLayout, read_saved_layout};
use futures_channel::oneshot;
use gpui_kit::*;
use serde_json::{Value, json};

use crate::core::backend::backend_config::BackendConfig;
use crate::core::backend::launcher::Launcher;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::outcome::Outcome;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;
use crate::core::theme::{self, theme_id::ThemeId};
use crate::core::window_root::WindowRoot;
use crate::features::palette::palette_view::PaletteView;

fn main() {
    // On macOS GPUI's default keeps the app running with no window open.
    gpui_kit::application()
        // The kit's icons, such as the dock toggles, and the window's own; without them every icon draws blank.
        .with_assets(crate::core::assets::AppAssets)
        .with_quit_mode(QuitMode::LastWindowClosed)
        .run(|cx| {
            gpui_kit::init(cx);
            features::shell::placeholder_pane::PlaceholderPane::register_all(cx);
            SelectedSession::init(cx);
            features::session::view::session_pane::SessionPane::register(cx);
            features::sessions::sessions_pane::SessionsPane::register(cx);
            features::files::files_pane::FilesPane::register(cx);
            features::files::search_pane::SearchPane::register(cx);
            let (read_layout, write_layout) = start_backend(cx);
            PaletteView::init(cx);
            features::meter::start_if_on(cx);
            // The window opens once the theme files have loaded, already in its theme. The kit calls this once, so
            // the layout's read and write are taken out on that call.
            let layout = Cell::new(Some((read_layout, write_layout)));
            theme::init(theme::THEMES_DIR.into(), cx, move |cx| {
                let Some((read_layout, write_layout)) = layout.take() else {
                    return;
                };
                let options = WindowOptions {
                    titlebar: Some(TitlebarOptions {
                        title: Some("Assembly".into()),
                        ..Default::default()
                    }),
                    ..Default::default()
                };
                gpui_kit::open_window(options, cx, |window, cx| {
                    theme::set(ThemeId::DEFAULT, window, cx);
                    let frame = cx.new(|cx| Frame::new(read_layout, write_layout, window, cx));
                    cx.new(|_| WindowRoot::new(frame))
                })
                .expect("open the window");
                cx.activate(true);
            });
        });
}

/// Starts the backend from `ASSEMBLY_BACKEND_DIR`, or the `backend` folder beside the program, and the socket client on
/// its first ready line. Without a backend the window still opens, in the default layout, and the error says what's
/// missing. Returns the window layout's read and write, over the backend's preferences.
fn start_backend(cx: &mut App) -> (Task<Option<Value>>, WriteLayout) {
    let socket = Rc::new(OnceCell::<Entity<SocketClient>>::new());
    // Answered with the client on the first ready line; dropped unanswered when there's no backend or the launcher gives
    // up before one is ready, so the window opens the default layout instead of waiting.
    let (connected, client) = oneshot::channel();
    // Dropped when the launcher gives up, which also ends a read whose backend died after its ready line.
    let (give_up, launcher_gave_up) = oneshot::channel::<()>();
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
        Ok(config) => {
            let (socket, connected) = (socket.clone(), Rc::new(Cell::new(Some(connected))));
            let (gave_up, give_up) = (connected.clone(), Rc::new(Cell::new(Some(give_up))));
            Launcher::start(
                config,
                move |port, token, cx| {
                    let endpoint = Endpoint { port, token };
                    match socket.get() {
                        Some(client) => client.read(cx).reconnect(endpoint),
                        None => {
                            let client = SocketClient::start(endpoint, cx);
                            _ = socket.set(client.clone());
                            Sessions::start(cx);
                            Machines::start(cx);
                            if let Some(connected) = connected.take() {
                                _ = connected.send(client);
                            }
                        }
                    }
                },
                move |_| drop((gave_up.take(), give_up.take())),
                cx,
            )
        }
        Err(error) => eprintln!("no backend: {error}"),
    }
    // Waits for the client, then reads the saved layout from its preferences.
    let read = cx.spawn(async move |cx| {
        let client = client.await.ok()?;
        cx.update(|cx| {
            if matches!(client.read(cx).state(), ConnectionState::Refused { .. }) {
                return Task::ready(None);
            }
            let mut preferences = client.read(cx).subscribe("preferences", None);
            let first = async move { preferences.updates.recv().await.ok() };
            read_saved_layout(first, async { _ = launcher_gave_up.await }, &client, cx)
        })
        .await
    });
    let write: WriteLayout = Box::new(move |layout, cx| {
        let Some(client) = socket.get() else {
            return Task::ready(());
        };
        let save = client.read(cx).call::<Value>(
            "preferences",
            "set",
            json!({"key": LAYOUT_KEY, "value": layout}),
            None,
        );
        cx.spawn(async move |_| {
            // Aborted (the socket dropped mid-save) shows nothing: the next change saves again.
            if let Outcome::Failed(failure) = save.await {
                eprintln!("shell: the layout wasn't saved: {}", failure.message);
            }
        })
    });
    (read, write)
}
