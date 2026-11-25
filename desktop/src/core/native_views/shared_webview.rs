//! `SharedWebView` owns the app's single full-window transparent SvelteKit WebView (`Assembly/web/`).
//!
//! Never spawns a second WebView per tab: `HybridFrame` opens one `PassthroughSurface` registered with
//! `NativeViews` (which owns its `NativeViewId`, bounds placement, and teardown). Whenever the `.NET`
//! backend starts or restarts, `set_backend` injects `window.__BACKEND__` and dispatches
//! `assembly:backend-ready` so SvelteKit reconnects directly over WebSockets.

use std::rc::Rc;

use futures_channel::mpsc::UnboundedSender;
use gpui_kit::*;

use super::native_view_id::NativeViewId;
use super::passthrough_surface::{HybridIpcMessage, PassthroughSurface};
use super::views::NativeViews;
use super::web_bootstrap::backend_script;
use crate::core::socket::endpoint::Endpoint;

/// App-wide global holding the single hybrid WebView's identity, JS handle, and latest `.NET` backend endpoint.
#[derive(Default)]
pub struct SharedWebView {
    /// The `NativeViews` handle for the one shared WebView, once created.
    id: Option<NativeViewId>,
    /// UI-thread handle to the underlying `wry::WebView` for `evaluate_script` and focus handoff calls.
    /// Dropped in `close()` before the parent window is destroyed.
    webview: Option<Rc<wry::WebView>>,
    /// Latest `.NET` backend port + token from `Launcher::start`'s ready callback.
    endpoint: Option<Endpoint>,
    #[cfg(target_os = "linux")]
    linux_shape: Option<super::linux_passthrough::LinuxShapeState>,
    /// Last initialization error when a native WebView could not be built (for example in headless tests).
    last_error: Option<SharedString>,
}

impl Global for SharedWebView {}

impl SharedWebView {
    /// Records the `.NET` backend port and token, and pushes `window.__BACKEND__` into the live WebView if it is
    /// already running. Called from `main.rs` on every backend ready line (initial start and restarts).
    pub fn set_backend(endpoint: Endpoint, cx: &mut App) {
        let js = backend_script(&endpoint);
        let state = cx.default_global::<Self>();
        state.endpoint = Some(endpoint);
        if let Some(webview) = &state.webview {
            let _ = webview.evaluate_script(&js);
            Self::pump_events();
        }
    }

    /// Re-injects the latest known `.NET` backend endpoint into the live WebView (used after SvelteKit mounts or
    /// reloads via Vite HMR so `window.__BACKEND__` is never lost).
    pub fn push_backend(cx: &App) {
        let Some(state) = cx.try_global::<Self>() else {
            return;
        };
        if let (Some(endpoint), Some(webview)) = (&state.endpoint, &state.webview) {
            let _ = webview.evaluate_script(&backend_script(endpoint));
            Self::pump_events();
        }
    }

    /// Opens the single shared WebView in full-window transparent pass-through mode (`ASSEMBLY_HYBRID_SHELL=1`),
    /// forwarding SvelteKit's `window.ipc.postMessage(...)` slot/session messages through `ipc_tx`.
    pub fn open_hybrid(
        ipc_tx: UnboundedSender<HybridIpcMessage>,
        window: &mut Window,
        cx: &mut App,
    ) -> Option<NativeViewId> {
        if let Some(id) = Self::id(cx) {
            return Some(id);
        }
        let endpoint = cx.default_global::<Self>().endpoint.clone();
        match PassthroughSurface::build(endpoint.as_ref(), ipc_tx, window, cx) {
            Ok(built) => {
                let id = cx
                    .default_global::<NativeViews>()
                    .open(Box::new(built.surface));
                let state = cx.default_global::<Self>();
                state.id = Some(id);
                state.webview = Some(built.webview);
                #[cfg(target_os = "linux")]
                {
                    state.linux_shape = Some(built.linux_shape);
                }
                state.last_error = None;
                Some(id)
            }
            Err(error) => {
                eprintln!("[hybrid] SharedWebView::open_hybrid failed: {error}");
                cx.default_global::<Self>().last_error = Some(error.into());
                None
            }
        }
    }

    /// Re-applies the Linux X11 visual + input shape cutout after a SvelteKit slot/occluder IPC update.
    pub fn sync_shape(cx: &App) {
        #[cfg(target_os = "linux")]
        if let Some(state) = cx.try_global::<Self>()
            && let (Some(shape), Some(webview)) = (&state.linux_shape, &state.webview)
        {
            shape.apply(webview);
        }
        #[cfg(not(target_os = "linux"))]
        let _ = cx;
    }

    /// Pumps pending GTK/WebKitGTK main-context events on Linux (no-op on other platforms).
    pub fn pump_events() {
        #[cfg(target_os = "linux")]
        super::linux_passthrough::pump_events();
    }

    /// Moves keyboard focus into the hosted `WKWebView` when SvelteKit receives a click while GPUI Chat held
    /// first responder (no-op if the WebView or one of its `<input>` descendants already holds focus).
    pub fn focus_webview(cx: &App) {
        if let Some(webview) = cx
            .try_global::<Self>()
            .and_then(|state| state.webview.as_ref())
        {
            super::passthrough_surface::focus_webview(webview);
        }
    }

    /// Returns keyboard focus from the `WKWebView` back to the parent GPUI window (for example when the user
    /// clicks into native GPUIKit Chat after interacting with SvelteKit).
    pub fn focus_parent(cx: &App) {
        if let Some(webview) = cx
            .try_global::<Self>()
            .and_then(|state| state.webview.as_ref())
        {
            let _ = webview.focus_parent();
        }
    }

    /// Returns the single shared WebView's `NativeViewId` once created.
    pub fn id(cx: &App) -> Option<NativeViewId> {
        cx.try_global::<Self>().and_then(|state| state.id)
    }

    /// Releases the single shared WebView and drops its native handle before window teardown.
    pub fn close(cx: &mut App) {
        let id = cx.try_global::<Self>().and_then(|state| state.id);
        if cx.has_global::<Self>() {
            let state = cx.global_mut::<Self>();
            // Drop the `wry::WebView` clone before releasing the owning surface so native teardown runs immediately.
            state.webview = None;
            #[cfg(target_os = "linux")]
            {
                state.linux_shape = None;
            }
            state.id = None;
        }
        if let Some(id) = id {
            NativeViews::close(id, cx);
        }
    }
}
