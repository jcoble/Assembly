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

- `src/core/`: what every feature shares. The socket client is `core/socket/` and the backend's clock is
  `core/clock/`; with the state (one owner per fact), the theme and the registries, that's all of it.
- `src/features/<feature>/`: one folder per feature, which never uses another feature's folder.
- One type per file, and no file over about 300 lines.
- The root module is named `core`, so in `main.rs` the `core` crate is `::core::`.

## The guard

`scripts/guard.sh` fails, with no allow lists, on:

- timers, `Timer`, `.timer(`, frame waits (`on_next_frame`, `request_animation_frame`) and other waits on time
  (`recv_timeout`, `park_timeout`) outside `src/core/clock/`;
- `tungstenite`, `TcpStream`, `TcpListener`, `UdpSocket` or an HTTP client outside `src/core/socket/`;
- sleeping (`std::thread::sleep` or any `sleep(`) anywhere;
- file reads (`std::fs`, `fs::`, `File::`) in a file that draws: one that implements `Render` or `RenderOnce` or
  has a `fn render`.

Its probe lines test every rule, and the names a rule must not catch, each time it runs.
