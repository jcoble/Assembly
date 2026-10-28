//! The bottom dock's Terminal tab: the selected session's terminal on the backend's terminal host
//! (dotnet/Assembly.Application/Features/Terminal/), emulated by alacritty's VT parser and drawn by GPUI. The behavior
//! list is docs/behavior/terminal.md; the wire is dotnet/notes/terminal-shape.md.

mod keys;
mod live;
mod output;
mod palette;
mod screen;
pub mod terminal_pane;
