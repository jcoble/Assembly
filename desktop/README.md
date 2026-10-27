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

Rust tests live in a `tests.rs` or `*_tests.rs` file beside the code (`#[cfg(test)] mod frames_tests;`), or in `desktop/tests/`.

## Layout

- `src/core/`: what every feature shares. The socket client is `core/socket/`, the only code that opens a socket.
  The state (one owner per fact), the theme and the registries are the rest.
- `src/core/clock/` holds exactly two things: the subscriber to the backend's `clock` topic, and the one named
  debounce. There's no debounce yet; the first save that needs one adds it there. Nothing in it ticks on its own,
  and it's the only code that may wait on time.
- `src/features/<feature>/`: one folder per feature, which never uses another feature's folder.
- One type per file, and no file over about 300 lines.
- The root module is named `core`, so in `main.rs` the `core` crate is `::core::`.

## The guard

`scripts/guard.sh` fails, with no allow lists, on:

- timers (`Timer`, `.timer(`, `::timer(`), frame waits (`on_next_frame`, `request_animation_frame`) and other waits
  on time (`recv_timeout`, `park_timeout`, `with_timeout`, `block_with_timeout`) outside `src/core/clock/`;
- `tungstenite`, `TcpStream`, `TcpListener`, `UdpSocket` or an HTTP client outside `src/core/socket/`;
- sleeping (`std::thread::sleep`, or `sleep` imported or called any other way) anywhere;
- file reads (`std::fs` in any form, `File::`) in a file that draws: one that implements `Render` or `RenderOnce` or
  has a `fn render`.

Its probe lines test every rule, and the names a rule must not catch, each time it runs.
