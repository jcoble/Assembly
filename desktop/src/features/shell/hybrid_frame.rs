//! `HybridFrame` is the opt-in (`ASSEMBLY_HYBRID_SHELL=1`) spike root view proving the inverted hybrid stack:
//! - Layer 1 (back, GPUI Metal): the real native GPUIKit `SessionPane` (Chat), sized to SvelteKit's transparent
//!   `#native-chat-slot` rectangle.
//! - Layer 2 (middle, transparent `PassthroughSurface` `WKWebView`): the full-window SvelteKit workbench (chrome,
//!   Sessions sidebar, Editor, Git, right drawer, AND floating SvelteKit popups/dropdowns/modals right over live Chat).
//! - Layer 3 (front, `gpui-fast` `GPUIOverlayView`): GPUI's own popovers/dropdowns inside `SessionPane` (model picker).
//!
//! Normal Assembly (`Frame`) is completely untouched when `ASSEMBLY_HYBRID_SHELL` is unset.

use std::rc::Rc;

use futures_channel::mpsc::unbounded;
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::native_views::native_view_id::NativeViewId;
use crate::core::native_views::passthrough_surface::HybridIpcMessage;
use crate::core::native_views::shared_webview::SharedWebView;
use crate::core::native_views::views::NativeViews;
use crate::core::theme::look::Look;
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::view::session_pane::SessionPane;

/// True when the user launched Assembly with `ASSEMBLY_HYBRID_SHELL=1` to test the full-window transparent
/// SvelteKit shell over native GPUIKit Chat.
pub fn enabled() -> bool {
    std::env::var("ASSEMBLY_HYBRID_SHELL")
        .ok()
        .is_some_and(|v| matches!(v.trim(), "1" | "true" | "TRUE" | "yes"))
}

const TOP_BAR_DRAG_HEIGHT: Pixels = px(56.0);
const CARD_CORNER_RADIUS: Pixels = px(10.0);

pub struct HybridFrame {
    /// The real native GPUIKit Chat pane rendered in Layer 1 behind SvelteKit's transparent Chat hole.
    chat: Entity<SessionPane>,
    /// The single full-window transparent WebView registered with `NativeViews`.
    webview_id: Option<NativeViewId>,
    /// Current `#native-chat-slot` bounds reported by SvelteKit's `ResizeObserver`.
    chat_rect: Option<Bounds<Pixels>>,
    /// Armed on a left mouse-down inside an empty top-bar drag strip so the next move starts a macOS window drag.
    should_move: bool,
    /// Processes `window.ipc.postMessage` slot and session-selection messages; dropped with the frame.
    _ipc: Task<()>,
    /// Releases the shared WebView handle cleanly before window teardown.
    _release: Subscription,
}

impl HybridFrame {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let chat = cx.new(|cx| SessionPane::chat(Rc::new(SocketSessionClient), window, cx));
        let (tx, mut rx) = unbounded::<HybridIpcMessage>();
        let webview_id = SharedWebView::open_hybrid(tx, window, cx);
        let _release = cx.on_release(|_, cx| SharedWebView::close(cx));

        let _ipc = cx.spawn_in(window, async move |this, cx| {
            while let Ok(message) = rx.recv().await {
                let updated = this.update_in(cx, |frame, window, cx| match message {
                    HybridIpcMessage::Slot { hole, .. } => {
                        let next = hole.map(|rect| rect.to_gpui_bounds());
                        if frame.chat_rect != next {
                            frame.chat_rect = next;
                            cx.notify();
                        }
                    }
                    HybridIpcMessage::SelectSession { id, machine } => {
                        frame.chat.update(cx, |pane, cx| {
                            pane.open_for_hybrid(id, machine, window, cx);
                        });
                    }
                });
                if updated.is_err() {
                    return;
                }
            }
        });

        Self {
            chat,
            webview_id,
            chat_rect: None,
            should_move: false,
            _ipc,
            _release,
        }
    }

    /// Native macOS titlebar drag strip rendered in Layer 1 behind SvelteKit's top chrome. Only clicks inside
    /// SvelteKit's `[data-window-drag-region]` spacers pass through `PassthroughSurface::hitTest:` to reach it.
    fn titlebar_drag_strip(&self, cx: &mut Context<Self>) -> Stateful<Div> {
        div()
            .id("hybrid-titlebar-drag")
            .absolute()
            .top_0()
            .left_0()
            .right_0()
            .h(TOP_BAR_DRAG_HEIGHT)
            .when(cfg!(target_os = "macos"), |area| {
                area.window_control_area(WindowControlArea::Drag)
                    .on_mouse_down(
                        MouseButton::Left,
                        cx.listener(|frame, _, _, _| frame.should_move = true),
                    )
                    .on_mouse_up(
                        MouseButton::Left,
                        cx.listener(|frame, _, _, _| frame.should_move = false),
                    )
                    .on_mouse_down_out(cx.listener(|frame, _, _, _| frame.should_move = false))
                    .on_mouse_move(cx.listener(|frame, _, window, _| {
                        if frame.should_move {
                            frame.should_move = false;
                            window.start_window_move();
                        }
                    }))
                    .on_click(|event, window, _| {
                        if event.click_count() == 2 {
                            window.titlebar_double_click();
                        }
                    })
            })
    }
}

impl Render for HybridFrame {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let look = Look::of(cx);
        div()
            .size_full()
            .relative()
            // Match SvelteKit's dark outer canvas (`--color-canvas`) so panel gutters and rounded Chat corners blend seamlessly.
            .bg(rgb(0x000000))
            // Record full-window bounds for the transparent WKWebView BEFORE painting Chat so nothing sits above
            // Chat in GPUI's element/hitbox order.
            .when_some(self.webview_id, |root, id| {
                root.child(div().absolute().inset_0().child(NativeViews::host(id)))
            })
            // Native macOS window drag + double-click zoom target for empty top-bar spacers.
            .child(self.titlebar_drag_strip(cx))
            // Layer 1 (back): native GPUIKit SessionPane positioned in SvelteKit's transparent Chat slot.
            // `capture_any_mouse_down` runs before children (such as the Composer input) stop propagation so clicking
            // anywhere in Chat always hands AppKit firstResponder back from WKWebView to GPUIView.
            .when_some(self.chat_rect, |root, rect| {
                root.child(
                    div()
                        .absolute()
                        .left(rect.origin.x)
                        .top(rect.origin.y)
                        .w(rect.size.width)
                        .h(rect.size.height)
                        .rounded(CARD_CORNER_RADIUS)
                        .overflow_hidden()
                        .bg(look.tile)
                        .capture_any_mouse_down(|_, _, cx| {
                            SharedWebView::focus_parent(cx);
                        })
                        .child(self.chat.clone()),
                )
            })
    }
}
