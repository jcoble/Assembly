//! Linux X11 WebKitGTK host for `PassthroughSurface` (`ASSEMBLY_HYBRID_SHELL=1`).
//!
//! Bridges GPUI's `XcbWindowHandle` to `lb-wry`'s `XlibWindowHandle`, initializes GTK once, and applies an X11
//! visual + input shape cutout (`XShapeCombineRectangles`) on the child X11 container window so `#native-chat-slot`
//! reveals and passes clicks/scrolls through to native GPUIKit `SessionPane` underneath even on `Xvfb` with no
//! compositor, while floating SvelteKit dialogs and menus (`occluders`) stay solid and interactive on top.

use std::ffi::{c_int, c_ulong, c_void};
use std::sync::{Arc, Mutex};

use gdk::prelude::{Cast as _, ObjectType as _};
use gtk::prelude::WidgetExt as _;
use raw_window_handle::{
    HandleError, HasWindowHandle, RawWindowHandle, WindowHandle, XlibWindowHandle,
};
use wry::{WebViewBuilder, WebViewExtUnix as _};

use super::passthrough_surface::{HoleGeometry, LogicalRect};

const SHAPE_BOUNDING: c_int = 0;
const SHAPE_INPUT: c_int = 2;
const SHAPE_SET: c_int = 0;
const UNSORTED: c_int = 0;

#[repr(C)]
#[derive(Clone, Copy, Debug)]
struct XRectangle {
    x: i16,
    y: i16,
    width: u16,
    height: u16,
}

#[link(name = "Xext")]
unsafe extern "C" {
    fn XShapeCombineRectangles(
        display: *mut c_void,
        dest: c_ulong,
        dest_kind: c_int,
        x_off: c_int,
        y_off: c_int,
        rectangles: *const XRectangle,
        n_rects: c_int,
        op: c_int,
        ordering: c_int,
    );
}

#[link(name = "X11")]
unsafe extern "C" {
    fn XFlush(display: *mut c_void) -> c_int;
}

struct XlibParent(XlibWindowHandle);

impl HasWindowHandle for XlibParent {
    fn window_handle(&self) -> Result<WindowHandle<'_>, HandleError> {
        // SAFETY: The parent X11 window is owned by GPUI's live Window for the duration of `build_as_child`.
        Ok(unsafe { WindowHandle::borrow_raw(RawWindowHandle::Xlib(self.0)) })
    }
}

pub(super) fn build_webview(
    builder: WebViewBuilder<'_>,
    window: &gpui_kit::Window,
) -> Result<wry::WebView, String> {
    gtk::init().map_err(|error| format!("gtk::init failed: {error}"))?;
    let raw = HasWindowHandle::window_handle(window)
        .map_err(|error| error.to_string())?
        .as_raw();
    let webview = match raw {
        RawWindowHandle::Xcb(xcb) => {
            let mut xlib = XlibWindowHandle::new(xcb.window.get().into());
            xlib.visual_id = xcb.visual_id.map_or(0, |id| id.get().into());
            builder.build_as_child(&XlibParent(xlib))
        }
        _ => builder.build_as_child(window),
    }
    .map_err(|error| error.to_string())?;
    pump_events();
    Ok(webview)
}

pub(super) fn pump_events() {
    while gtk::events_pending() {
        gtk::main_iteration_do(false);
    }
}

fn to_cairo_rect(rect: LogicalRect) -> Option<cairo::RectangleInt> {
    if rect.width <= 0.0 || rect.height <= 0.0 {
        return None;
    }
    let x = rect.x.floor() as i32;
    let y = rect.y.floor() as i32;
    let width = rect.width.ceil().max(1.0) as i32;
    let height = rect.height.ceil().max(1.0) as i32;
    Some(cairo::RectangleInt::new(x, y, width, height))
}

fn region_to_xrects(region: &cairo::Region) -> Vec<XRectangle> {
    let count = region.num_rectangles();
    let mut rects = Vec::with_capacity(count.max(0) as usize);
    for idx in 0..count {
        let r = region.rectangle(idx);
        if r.width() > 0 && r.height() > 0 {
            rects.push(XRectangle {
                x: r.x().clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                y: r.y().clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                width: r.width().min(u16::MAX as i32) as u16,
                height: r.height().min(u16::MAX as i32) as u16,
            });
        }
    }
    rects
}

fn apply_xshape(gdk_win: &gdk::Window, rects: &[XRectangle]) {
    let display = gdk_win.display();
    let Ok(x11_display) = display.downcast::<gdkx11::X11Display>() else {
        return;
    };
    let Ok(x11_win) = gdk_win.clone().downcast::<gdkx11::X11Window>() else {
        return;
    };
    let xdisplay =
        unsafe { gdkx11::ffi::gdk_x11_display_get_xdisplay(x11_display.as_ptr()) as *mut c_void };
    let xid = x11_win.xid();
    if xdisplay.is_null() || xid == 0 {
        return;
    }
    unsafe {
        XShapeCombineRectangles(
            xdisplay,
            xid,
            SHAPE_BOUNDING,
            0,
            0,
            rects.as_ptr(),
            rects.len() as c_int,
            SHAPE_SET,
            UNSORTED,
        );
        XShapeCombineRectangles(
            xdisplay,
            xid,
            SHAPE_INPUT,
            0,
            0,
            rects.as_ptr(),
            rects.len() as c_int,
            SHAPE_SET,
            UNSORTED,
        );
        XFlush(xdisplay);
    }
}

#[derive(Clone)]
pub(super) struct LinuxShapeState {
    geometry: Arc<Mutex<HoleGeometry>>,
    size: Arc<Mutex<Option<(i32, i32)>>>,
}

impl LinuxShapeState {
    pub(super) fn new(geometry: Arc<Mutex<HoleGeometry>>) -> Self {
        Self {
            geometry,
            size: Arc::new(Mutex::new(None)),
        }
    }

    pub(super) fn set_size(&self, size: Option<(i32, i32)>) {
        if let Ok(mut guard) = self.size.lock() {
            *guard = size;
        }
    }

    pub(super) fn apply(&self, webview: &wry::WebView) {
        let size = self.size.lock().ok().and_then(|guard| *guard);
        let Some((width, height)) = size else {
            return;
        };
        if width <= 0 || height <= 0 {
            return;
        }
        let Ok(geometry) = self.geometry.lock() else {
            return;
        };
        let widget = webview.webview();
        let Some(toplevel) = widget.toplevel() else {
            return;
        };
        let Some(gdk_win) = toplevel.window() else {
            return;
        };

        let region =
            cairo::Region::create_rectangle(&cairo::RectangleInt::new(0, 0, width, height));
        if let Some(hole) = geometry.hole
            && let Some(rect) = to_cairo_rect(hole)
        {
            let _ = region.subtract_rectangle(&rect);
        }
        for occ in &geometry.occluders {
            if let Some(rect) = to_cairo_rect(*occ) {
                let _ = region.union_rectangle(&rect);
            }
        }
        let rects = region_to_xrects(&region);
        apply_xshape(&gdk_win, &rects);
        if let Some(widget_win) = widget.window() {
            apply_xshape(&widget_win, &rects);
        }
        pump_events();
    }
}
