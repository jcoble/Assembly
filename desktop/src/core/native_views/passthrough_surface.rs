//! `PassthroughSurface` hosts a full-window transparent `WKWebView` directly above `GPUIView` on macOS while passing
//! mouse, drag, hover, and scroll hit-testing inside the unoccluded Chat slot rectangle straight down to GPUI
//! (`SessionPane`) underneath.
//!
//! Why it mounts `wry::WebView` directly inside our own `AssemblyPassthroughSurfaceView` container instead of going
//! through `gpui-fast`'s `WindowCompositionState::platform_surfaces`:
//! - `gpui-fast`'s `composition::hit_test` intentionally breaks hit-testing for GPUI base views whenever a registered
//!   native surface covers `(x, y)` (assuming opaque native content).
//! - Here the full-window `WKWebView` is transparent over `#native-chat-slot` and wants clicks/scrolls in that hole
//!   to reach GPUI's `SessionPane` natively, while keeping ALL SvelteKit chrome, Editor, Git, AND floating SvelteKit
//!   dropdowns/modals/dimmed backdrops drawn natively ON TOP of live GPUI Chat with zero blank-hole hiding.
//! - GPUI still runs on `gpui-fast` (retained layout/paint caching for smooth spinners, shimmers, and low Chat RAM).

use std::rc::Rc;
use std::sync::{Arc, Mutex};

use futures_channel::mpsc::UnboundedSender;
use gpui_kit::component::ActiveTheme as _;
use gpui_kit::*;
use serde::Deserialize;
use wry::WebViewBuilder;

use super::native_surface::NativeSurface;
#[cfg(not(debug_assertions))]
use super::web_bootstrap::{APP_SCHEME, serve_embedded_asset};
use super::web_bootstrap::{hybrid_url, init_script};
use crate::core::socket::endpoint::Endpoint;

/// A rectangle in top-left window logical pixels (matching CSS `getBoundingClientRect()`).
#[derive(Clone, Copy, Debug, PartialEq, Deserialize)]
pub struct LogicalRect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

impl LogicalRect {
    /// True when logical window point `(px, py)` falls inside this non-empty rectangle.
    #[cfg_attr(not(target_os = "macos"), allow(dead_code))]
    pub fn contains(&self, px: f64, py: f64) -> bool {
        self.width > 0.0
            && self.height > 0.0
            && px >= self.x
            && px < self.x + self.width
            && py >= self.y
            && py < self.y + self.height
    }

    /// Converts to GPUI window-relative `Bounds<Pixels>`.
    pub fn to_gpui_bounds(self) -> Bounds<Pixels> {
        Bounds::new(
            point(px(self.x as f32), px(self.y as f32)),
            size(px(self.width as f32), px(self.height as f32)),
        )
    }
}

/// Live hit-test pass-through state shared between the SvelteKit IPC handler and macOS `hitTest:`.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct HoleGeometry {
    /// The transparent Chat slot rectangle; `None` when a non-Chat SvelteKit view covers the center.
    pub hole: Option<LogicalRect>,
    /// Floating SvelteKit popups, menus, dialogs, or modal backdrops that must intercept clicks over Chat.
    pub occluders: Vec<LogicalRect>,
    /// Empty top-bar window-drag strips (`[data-window-drag-region]`) that pass clicks straight through
    /// to GPUI's native macOS titlebar drag handler for zero-lag window movement and double-click zoom.
    pub drag_rects: Vec<LogicalRect>,
}

impl HoleGeometry {
    /// True when a pointer at window top-left logical `(x, y)` should pass through the transparent
    /// `WKWebView` directly to GPUI underneath (either the native `SessionPane` or a titlebar drag strip).
    #[cfg_attr(not(target_os = "macos"), allow(dead_code))]
    pub fn passes_through(&self, x: f64, y: f64) -> bool {
        let in_hole = self.hole.is_some_and(|hole| hole.contains(x, y));
        let in_drag = self.drag_rects.iter().any(|rect| rect.contains(x, y));
        (in_hole || in_drag) && !self.occluders.iter().any(|rect| rect.contains(x, y))
    }
}

/// Messages posted from the SvelteKit hybrid shell via `window.ipc.postMessage(JSON.stringify(...))`.
#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(tag = "t", rename_all = "snake_case")]
pub enum HybridIpcMessage {
    /// Updates the transparent Chat slot bounds, floating SvelteKit overlays, and top-bar drag strips.
    Slot {
        hole: Option<LogicalRect>,
        #[serde(default)]
        occluders: Vec<LogicalRect>,
        #[serde(default)]
        drag_rects: Vec<LogicalRect>,
    },
    /// Tells native GPUIKit `SessionPane` to open the session picked in SvelteKit's sidebar.
    SelectSession {
        id: String,
        #[serde(default)]
        machine: Option<String>,
    },
    /// Opens the native GPUIKit `NewSessionForm` draft composer inside `#native-chat-slot`.
    NewSession,
    /// Sent by SvelteKit on mount/HMR reload when `window.__BACKEND__` needs to be pushed again.
    Ready,
    /// Moves native keyboard focus (`NSWindow.firstResponder`) into `WKWebView` when the user clicks
    /// SvelteKit chrome while GPUI Chat held focus, so `Escape` and shortcuts reach the web side.
    FocusWeb,
}

/// Full-window transparent `wry::WebView` with selective hit-test pass-through for the Chat hole.
pub(super) struct PassthroughSurface {
    webview: Rc<wry::WebView>,
    #[cfg(target_os = "macos")]
    mac_host: Option<macos_hit_test::PassthroughContainer>,
}

impl PassthroughSurface {
    /// Builds the full-window transparent `wry::WebView` directly as a child of `window` (bypassing `gpui-fast`'s
    /// opaque native-surface hitbox blocker), mounts it inside our `AssemblyPassthroughSurfaceView` container on
    /// macOS, and wires `window.ipc.postMessage(...)` to update `HoleGeometry` synchronously.
    pub(super) fn build(
        endpoint: Option<&Endpoint>,
        ipc_tx: UnboundedSender<HybridIpcMessage>,
        window: &mut Window,
        cx: &mut App,
    ) -> Result<(Self, Rc<wry::WebView>), String> {
        let geometry = Arc::new(Mutex::new(HoleGeometry::default()));
        let ipc_geometry = geometry.clone();

        let mut builder = WebViewBuilder::new().with_transparent(true);
        let bootstrap = init_script(endpoint);
        if !bootstrap.is_empty() {
            builder = builder.with_initialization_script(bootstrap);
        }

        #[cfg(not(debug_assertions))]
        {
            builder = builder.with_custom_protocol(APP_SCHEME.to_owned(), |_id, request| {
                serve_embedded_asset(request.uri().path())
            });
        }

        builder = builder.with_ipc_handler(move |request| {
            let Ok(message) = serde_json::from_str::<HybridIpcMessage>(request.body()) else {
                return;
            };
            // Update `hitTest:` state synchronously so popups, drag strips, and resizes take effect immediately.
            if let HybridIpcMessage::Slot {
                hole,
                occluders,
                drag_rects,
            } = &message
                && let Ok(mut guard) = ipc_geometry.lock()
            {
                if debug_enabled() {
                    eprintln!(
                        "[hybrid] slot update: hole={hole:?} occluders={occluders:?} drag_rects={drag_rects:?}"
                    );
                }
                guard.hole = *hole;
                guard.occluders = occluders.clone();
                guard.drag_rects = drag_rects.clone();
            }
            let _ = ipc_tx.unbounded_send(message);
        });

        builder = builder.with_url(hybrid_url());

        let raw = builder
            .build_as_child(&*window)
            .map_err(|error| error.to_string())?;
        let webview = Rc::new(raw);
        let is_dark = cx.theme().is_dark();

        #[cfg(target_os = "macos")]
        let mac_host = macos_hit_test::mount(&webview, geometry, is_dark);

        #[cfg(not(target_os = "macos"))]
        {
            let _ = is_dark;
            let _ = webview.set_visible(false);
            let _ = webview.set_bounds(wry::Rect::default());
        }

        Ok((
            Self {
                webview: webview.clone(),
                #[cfg(target_os = "macos")]
                mac_host,
            },
            webview,
        ))
    }
}

impl NativeSurface for PassthroughSurface {
    fn place(&mut self, frame: Option<Bounds<Pixels>>) {
        #[cfg(target_os = "macos")]
        if let Some(host) = &self.mac_host {
            host.place(&self.webview, frame);
            return;
        }

        match frame {
            Some(bounds) => {
                let rect = wry::Rect {
                    position: wry::dpi::LogicalPosition::new(
                        f64::from(bounds.origin.x),
                        f64::from(bounds.origin.y),
                    )
                    .into(),
                    size: wry::dpi::LogicalSize::new(
                        f64::from(bounds.size.width),
                        f64::from(bounds.size.height),
                    )
                    .into(),
                };
                let _ = self.webview.set_bounds(rect);
                let _ = self.webview.set_visible(true);
            }
            None => {
                let _ = self.webview.set_visible(false);
                let _ = self.webview.set_bounds(wry::Rect::default());
            }
        }
    }

    fn composites_under_overlays(&self) -> bool {
        true
    }

    #[cfg_attr(not(target_os = "macos"), allow(unused_mut))]
    fn release(mut self: Box<Self>) {
        #[cfg(target_os = "macos")]
        if let Some(host) = self.mac_host.take() {
            host.release(&self.webview);
        }
        let _ = self.webview.set_visible(false);
    }
}

/// Gives keyboard focus to the hosted `WebView` unless it (or a descendant input) already owns focus.
pub(super) fn focus_webview(webview: &wry::WebView) {
    #[cfg(target_os = "macos")]
    macos_hit_test::focus_webview(webview);
    #[cfg(not(target_os = "macos"))]
    {
        let _ = webview.focus();
    }
}

fn debug_enabled() -> bool {
    std::env::var("ASSEMBLY_HYBRID_DEBUG")
        .ok()
        .is_some_and(|v| matches!(v.trim(), "1" | "true" | "TRUE" | "yes"))
}

#[cfg(target_os = "macos")]
mod macos_hit_test {
    //! macOS AppKit container `NSView` (`AssemblyPassthroughSurfaceView`) hosting the transparent `WKWebView` as a
    //! direct subview of `GPUIView`. Because it is not registered in `gpui-fast`'s `WindowCompositionState`, GPUI
    //! hit-tests `SessionPane` normally whenever `hitTest:` returns `nil` for points inside the unoccluded Chat hole.

    use std::collections::HashMap;
    use std::ptr;
    use std::sync::{Arc, Mutex, OnceLock};

    use gpui_kit::{Bounds, Pixels};
    use objc2::rc::Retained;
    use objc2::runtime::{AnyClass, AnyObject, Bool, ClassBuilder, Sel};
    use objc2::{class, msg_send, sel};
    use objc2_app_kit::{
        NSAppearance, NSAppearanceCustomization as _, NSAppearanceNameAqua,
        NSAppearanceNameDarkAqua, NSView,
    };
    use objc2_foundation::{NSNumber, NSPoint, NSRect, NSSize, ns_string};
    use wry::WebViewExtMacOS as _;

    use super::HoleGeometry;

    type Id = *mut AnyObject;

    static REGISTRY: OnceLock<Mutex<HashMap<usize, Arc<Mutex<HoleGeometry>>>>> = OnceLock::new();

    fn registry() -> &'static Mutex<HashMap<usize, Arc<Mutex<HoleGeometry>>>> {
        REGISTRY.get_or_init(|| Mutex::new(HashMap::new()))
    }

    fn passthrough_class() -> &'static AnyClass {
        static CLASS: OnceLock<&'static AnyClass> = OnceLock::new();
        CLASS.get_or_init(|| {
            if let Some(existing) = AnyClass::get(c"AssemblyPassthroughSurfaceView") {
                return existing;
            }
            let mut decl = ClassBuilder::new(c"AssemblyPassthroughSurfaceView", class!(NSView))
                .expect("register AssemblyPassthroughSurfaceView");
            unsafe {
                decl.add_method(
                    sel!(hitTest:),
                    passthrough_hit_test as extern "C" fn(_, Sel, NSPoint) -> Id,
                );
            }
            decl.register()
        })
    }

    extern "C" fn passthrough_hit_test(this: &AnyObject, _: Sel, point: NSPoint) -> Id {
        let key = this as *const AnyObject as usize;
        let geometry = registry()
            .lock()
            .ok()
            .and_then(|map| map.get(&key).cloned());

        if let Some(geometry) = geometry {
            let (px, py) = unsafe {
                let parent: Id = msg_send![this, superview];
                let local: NSPoint = if parent.is_null() {
                    point
                } else {
                    msg_send![this, convertPoint: point, fromView: parent]
                };
                let bounds: NSRect = msg_send![this, bounds];
                let flipped: Bool = msg_send![this, isFlipped];
                let y = if flipped.as_bool() {
                    local.y
                } else {
                    bounds.size.height - local.y
                };
                (local.x, y)
            };

            let pass = geometry
                .lock()
                .is_ok_and(|state| state.passes_through(px, py));
            if super::debug_enabled() {
                eprintln!(
                    "[hybrid] hitTest px={px:.1} py={py:.1} -> {}",
                    if pass { "PASS(GPUI)" } else { "WEBVIEW" }
                );
            }
            if pass {
                // Returning `nil` lets AppKit fall straight through to `GPUIView` (`SessionPane`) underneath.
                return ptr::null_mut();
            }
        }

        unsafe { msg_send![super(this, class!(NSView)), hitTest: point] }
    }

    pub(super) struct PassthroughContainer {
        container: Retained<NSView>,
        gpui_view: Retained<NSView>,
    }

    impl PassthroughContainer {
        pub(super) fn place(&self, webview: &wry::WebView, frame: Option<Bounds<Pixels>>) {
            let wk = webview.webview();
            unsafe {
                match frame {
                    Some(bounds) => {
                        let parent_bounds: NSRect = msg_send![&*self.gpui_view, bounds];
                        let w = f64::from(bounds.size.width);
                        let h = f64::from(bounds.size.height);
                        let x = f64::from(bounds.origin.x);
                        let y = parent_bounds.size.height - f64::from(bounds.origin.y) - h;
                        let container_frame = NSRect::new(NSPoint::new(x, y), NSSize::new(w, h));
                        let child_frame = NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(w, h));
                        let _: () = msg_send![&*self.container, setFrame: container_frame];
                        let _: () = msg_send![&*wk, setFrame: child_frame];
                        let _: () = msg_send![&*self.container, setHidden: Bool::NO];
                        let _: () = msg_send![&*wk, setHidden: Bool::NO];
                    }
                    None => {
                        let zero = NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(0.0, 0.0));
                        let _: () = msg_send![&*self.container, setHidden: Bool::YES];
                        let _: () = msg_send![&*wk, setHidden: Bool::YES];
                        let _: () = msg_send![&*self.container, setFrame: zero];
                        let _: () = msg_send![&*wk, setFrame: zero];
                    }
                }
            }
        }

        pub(super) fn release(self, webview: &wry::WebView) {
            let key = (&*self.container as *const NSView) as usize;
            if let Ok(mut map) = registry().lock() {
                map.remove(&key);
            }
            let wk = webview.webview();
            unsafe {
                let _: () = msg_send![&*wk, removeFromSuperview];
                let _: () = msg_send![&*self.container, removeFromSuperview];
            }
        }
    }

    pub(super) fn focus_webview(webview: &wry::WebView) {
        let wk = webview.webview();
        unsafe {
            let window: Id = msg_send![&*wk, window];
            if window.is_null() {
                return;
            }
            let responder: Id = msg_send![window, firstResponder];
            let already_in_web = !responder.is_null() && {
                let is_view: Bool = msg_send![responder, isKindOfClass: class!(NSView)];
                if is_view.as_bool() {
                    let descendant: Bool = msg_send![responder, isDescendantOf: &*wk];
                    descendant.as_bool()
                } else {
                    false
                }
            };
            if !already_in_web {
                let _: Bool = msg_send![window, makeFirstResponder: &*wk];
            }
        }
    }

    pub(super) fn mount(
        webview: &wry::WebView,
        geometry: Arc<Mutex<HoleGeometry>>,
        is_dark: bool,
    ) -> Option<PassthroughContainer> {
        let wk = webview.webview();
        unsafe {
            // Ensure WKWebView itself paints no opaque background behind transparent HTML regions.
            let no = NSNumber::new_bool(false);
            let _: () = msg_send![&*wk, setValue: &*no, forKey: ns_string!("drawsBackground")];

            let appearance_name = if is_dark {
                NSAppearanceNameDarkAqua
            } else {
                NSAppearanceNameAqua
            };
            let appearance = NSAppearance::appearanceNamed(appearance_name);
            wk.setAppearance(appearance.as_deref());

            let Some(gpui_view) = wk.superview() else {
                eprintln!("[hybrid] mount failed: WKWebView has no GPUIView superview");
                return None;
            };

            let cls = passthrough_class();
            let raw_container: Id = msg_send![cls, alloc];
            let zero = NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(0.0, 0.0));
            let raw_container: Id = msg_send![raw_container, initWithFrame: zero];
            let container = Retained::from_raw(raw_container.cast::<NSView>())?;
            let _: () = msg_send![&*container, setWantsLayer: Bool::YES];
            let _: () = msg_send![&*container, setHidden: Bool::YES];

            let key = (&*container as *const NSView) as usize;
            if let Ok(mut map) = registry().lock() {
                map.insert(key, geometry);
            }

            // Mount container directly on GPUIView and reparent WKWebView inside it.
            gpui_view.addSubview(&container);
            container.addSubview(&wk);

            if super::debug_enabled() {
                eprintln!(
                    "[hybrid] mounted direct PassthroughContainer={key:#x} on GPUIView={:#x}",
                    (&*gpui_view as *const NSView) as usize
                );
            }

            Some(PassthroughContainer {
                container,
                gpui_view,
            })
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{HoleGeometry, HybridIpcMessage, LogicalRect};

    #[test]
    fn passes_through_only_inside_unoccluded_chat_hole() {
        let hole = LogicalRect {
            x: 260.0,
            y: 52.0,
            width: 800.0,
            height: 700.0,
        };
        let popup = LogicalRect {
            x: 300.0,
            y: 60.0,
            width: 240.0,
            height: 220.0,
        };
        let geometry = HoleGeometry {
            hole: Some(hole),
            occluders: vec![popup],
            drag_rects: vec![LogicalRect {
                x: 420.0,
                y: 0.0,
                width: 300.0,
                height: 44.0,
            }],
        };

        // Inside left SvelteKit sidebar -> stays in WKWebView.
        assert!(!geometry.passes_through(100.0, 200.0));
        // Inside top bar button area (outside drag strip) -> stays in WKWebView.
        assert!(!geometry.passes_through(120.0, 20.0));
        // Inside empty top bar drag strip -> passes through to GPUI native window drag.
        assert!(geometry.passes_through(500.0, 20.0));
        // Inside unoccluded Chat hole -> passes straight through to GPUI SessionPane!
        assert!(geometry.passes_through(700.0, 400.0));
        // Inside SvelteKit dropdown floating OVER Chat -> stays in WKWebView!
        assert!(!geometry.passes_through(350.0, 120.0));

        // When Editor/Git tab covers Chat (`hole: None`) -> nothing passes through.
        let editor_mode = HoleGeometry::default();
        assert!(!editor_mode.passes_through(700.0, 400.0));
    }

    #[test]
    fn parses_slot_and_select_session_ipc_messages() {
        let slot: HybridIpcMessage = serde_json::from_str(
            r#"{"t":"slot","hole":{"x":240,"y":48,"width":600,"height":500},"occluders":[{"x":300,"y":50,"width":200,"height":150}],"drag_rects":[{"x":420,"y":0,"width":240,"height":44}]}"#,
        )
        .unwrap();
        assert!(
            matches!(slot, HybridIpcMessage::Slot { hole: Some(_), occluders, drag_rects } if occluders.len() == 1 && drag_rects.len() == 1)
        );

        let pick: HybridIpcMessage =
            serde_json::from_str(r#"{"t":"select_session","id":"sess-42"}"#).unwrap();
        assert_eq!(
            pick,
            HybridIpcMessage::SelectSession {
                id: "sess-42".into(),
                machine: None
            }
        );

        let new_session: HybridIpcMessage = serde_json::from_str(r#"{"t":"new_session"}"#).unwrap();
        assert_eq!(new_session, HybridIpcMessage::NewSession);

        let ready: HybridIpcMessage = serde_json::from_str(r#"{"t":"ready"}"#).unwrap();
        assert_eq!(ready, HybridIpcMessage::Ready);

        let focus_web: HybridIpcMessage = serde_json::from_str(r#"{"t":"focus_web"}"#).unwrap();
        assert_eq!(focus_web, HybridIpcMessage::FocusWeb);
    }
}
