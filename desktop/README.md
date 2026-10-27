# desktop/: Assembly's window in GPUI

The window, in Rust on GPUI Kit (Longbridge's components on Zed's GPUI). It replaces `app/` and `shell/` during step
G0 of the GPUI switch plan. The .NET backend in `dotnet/` computes; this crate draws what it sends over one socket.
For now it opens one empty window and quits when the window closes.

## Build

Rust is pinned in `rust-toolchain.toml` (1.98.0) and every crate is pinned exactly in `Cargo.toml`, with
`Cargo.lock` committed. On Linux (agent-workbox and CI), GPUI needs these packages:

```sh
sudo apt-get install -y build-essential pkg-config libxkbcommon-dev libxkbcommon-x11-dev libx11-xcb-dev \
  libxcb1-dev libwayland-dev libvulkan-dev libfontconfig-dev libfreetype-dev libssl-dev clang cmake
rustup toolchain install 1.98.0 --profile minimal -c clippy -c rustfmt
```

Then, in `desktop/`:

```sh
cargo build --locked
cargo test --locked
cargo clippy --locked --all-targets -- -D warnings
bash scripts/guard.sh
```

CI's `desktop` job runs the first three on Linux; the guard runs before every commit and in CI's lint job
(`scripts/lint.d/60-desktop-guard.sh`). agent-workbox has no window: windows run on the Mac.

## Layout

- `src/core/`: what every feature shares. The socket client is `core/socket/`, the only code that opens a socket.
  The state (one owner per fact), the theme and the registries are the rest.
- `src/features/<feature>/`: one folder per feature, which never uses another feature's folder.
- One type per file, and no file over about 300 lines.
- A timer or animation is a `Task` held by the view that started it, never detached, so closing the view stops it.
  App-wide subscriptions (window closed, app quit) are held in the root view's `_subscriptions: Vec<Subscription>`.
- Anything that ticks on a schedule (elapsed or relative times) subscribes to the backend's `clock` topic.
  Window timers are only for animations, frame waits and a debounce.
- The one exception is the lag meter (`features/meter/`): its 100 ms stall tick and 5 s report timer run only while
  `ASSEMBLY_METER` is set (Jesse, 2026-10-27).
- The root module is named `core`, so in `main.rs` the `core` crate is `::core::`.

## The guard

`scripts/guard.sh` fails, with no allow lists, on:

- `tungstenite`, `TcpStream`, `TcpListener`, `UdpSocket` or an HTTP client outside `src/core/socket/`;
- a native view API (`gpui_webview`, Ghostty, `NSView`, `WKWebView`, `objc`, `cocoa`, `raw_window_handle`) outside
  `src/core/native_views/`, the one module that shows, hides and places the terminal and browser views;
- `.detach()` (or `detach_and_log_err`) on a task or subscription anywhere: whatever starts one holds it;
- starting an OS thread (`thread::spawn`, `thread::Builder`) outside `src/core/socket/` and `src/core/backend/` (the
  backend launcher), including a grouped `thread::{` import split over lines; `thread::scope` always joins, so it's
  fine anywhere;
- file reads (`std::fs` in any form, `File::`) in a file that draws: one that implements `Render` or `RenderOnce` or
  has a `fn render`.

Its probe lines test every rule, and the names a rule must not catch, each time it runs.
