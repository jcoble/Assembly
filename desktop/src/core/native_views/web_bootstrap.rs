//! Bootstrap JS injection and release-mode `app://` static asset serving for `Assembly/web/`.
//!
//! - In debug builds (`cfg!(debug_assertions)`), the hybrid WebView loads Vite's HMR dev server
//!   (`http://127.0.0.1:5180/`, or `ASSEMBLY_WEBVIEW_URL` when set) so SvelteKit edits hot-reload
//!   without recompiling Rust (leaving port `5177` free for the old Tauri reference app).
//! - In release builds (`cfg!(not(debug_assertions))`), the WebView registers an `app://` custom
//!   protocol backed by `rust-embed` assets compiled from `Assembly/web/build`, with SPA fallback
//!   to `index.html`.

#[cfg(not(debug_assertions))]
use std::borrow::Cow;

use serde_json::json;

use crate::core::socket::endpoint::Endpoint;

/// Default Vite dev server URL used in debug builds for `Assembly/web/` (port 5180 so the old Tauri reference on 5177 can run side-by-side).
#[cfg(debug_assertions)]
pub(super) const DEFAULT_DEV_URL: &str = "http://127.0.0.1:5180/";

/// Release-only custom protocol scheme and origin serving embedded `Assembly/web/build` assets.
#[cfg(not(debug_assertions))]
pub(super) const APP_SCHEME: &str = "app";
#[cfg(not(debug_assertions))]
pub(super) const APP_ORIGIN: &str = "app://localhost/";

/// Embedded static SvelteKit SPA build bundled into release binaries from `Assembly/web/build`.
#[cfg(not(debug_assertions))]
#[derive(rust_embed::RustEmbed)]
#[folder = "../web/build"]
struct FrontendAssets;

/// Resolves the hybrid shell URL: `ASSEMBLY_WEBVIEW_URL` (or Vite `127.0.0.1:5180/`) in debug
/// builds, and `app://localhost/` in release builds.
pub(super) fn hybrid_url() -> String {
    #[cfg(debug_assertions)]
    {
        std::env::var("ASSEMBLY_WEBVIEW_URL")
            .or_else(|_| std::env::var("ASSEMBLY_HYBRID_URL"))
            .ok()
            .map(|value| value.trim().to_owned())
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| DEFAULT_DEV_URL.to_owned())
    }
    #[cfg(not(debug_assertions))]
    {
        APP_ORIGIN.to_owned()
    }
}

/// Builds the JS snippet that populates `window.__BACKEND__` and notifies any waiting SvelteKit client.
/// The token is JSON-encoded so special characters can never break out of the string literal.
pub(super) fn backend_script(endpoint: &Endpoint) -> String {
    let payload = json!({
        "url": format!("ws://127.0.0.1:{}/socket", endpoint.port),
        "bulkUrl": format!("ws://127.0.0.1:{}/socket/bulk", endpoint.port),
        "terminalUrl": format!("ws://127.0.0.1:{}/socket/terminal", endpoint.port),
        "port": endpoint.port,
        "token": endpoint.token,
    });
    format!(
        "window.__BACKEND__={payload};window.dispatchEvent(new CustomEvent('assembly:backend-ready',{{detail:window.__BACKEND__}}));"
    )
}

/// Builds the initialization script injected before `Assembly/web/` scripts execute.
pub(super) fn init_script(endpoint: Option<&Endpoint>) -> String {
    endpoint.map(backend_script).unwrap_or_default()
}

/// Normalizes a custom-protocol request path into a relative asset key (`"/"` -> `"index.html"`).
#[cfg(any(not(debug_assertions), test))]
pub(super) fn normalize_asset_path(raw_path: &str) -> &str {
    let trimmed = raw_path.trim_start_matches('/');
    if trimmed.is_empty() {
        "index.html"
    } else {
        trimmed
    }
}

/// Guesses the HTTP `Content-Type` header for an embedded asset path.
#[cfg(any(not(debug_assertions), test))]
pub(super) fn asset_mime(path: &str) -> String {
    mime_guess::from_path(path)
        .first_or_octet_stream()
        .essence_str()
        .to_owned()
}

/// Serves an embedded SvelteKit file for `app://localhost/...`, falling back to `index.html` for SPA routes.
#[cfg(not(debug_assertions))]
pub(super) fn serve_embedded_asset(raw_path: &str) -> wry::http::Response<Cow<'static, [u8]>> {
    let requested = normalize_asset_path(raw_path);
    let (resolved_path, asset) = match FrontendAssets::get(requested) {
        Some(file) => (requested, Some(file)),
        None => ("index.html", FrontendAssets::get("index.html")),
    };
    match asset {
        Some(file) => wry::http::Response::builder()
            .status(200)
            .header(wry::http::header::CONTENT_TYPE, asset_mime(resolved_path))
            .body(file.data)
            .unwrap_or_else(|_| {
                wry::http::Response::new(Cow::Borrowed(&b"failed to build asset response"[..]))
            }),
        None => wry::http::Response::builder()
            .status(404)
            .header(wry::http::header::CONTENT_TYPE, "text/plain; charset=utf-8")
            .body(Cow::Borrowed(&b"not found"[..]))
            .unwrap_or_else(|_| wry::http::Response::new(Cow::Borrowed(&b"not found"[..]))),
    }
}

#[cfg(test)]
mod tests {
    use super::{asset_mime, backend_script, normalize_asset_path};
    use crate::core::socket::endpoint::Endpoint;

    #[test]
    fn backend_script_exposes_socket_urls_and_escapes_token() {
        let endpoint = Endpoint {
            port: 18877,
            token: "tok\"en\\1".into(),
        };
        let js = backend_script(&endpoint);
        assert!(js.contains("ws://127.0.0.1:18877/socket"));
        assert!(js.contains("ws://127.0.0.1:18877/socket/bulk"));
        assert!(js.contains("ws://127.0.0.1:18877/socket/terminal"));
        assert!(js.contains(r#""token":"tok\"en\\1""#));
        assert!(js.contains("assembly:backend-ready"));
    }

    #[test]
    fn normalizes_paths_and_guesses_mime_types() {
        assert_eq!(normalize_asset_path("/"), "index.html");
        assert_eq!(normalize_asset_path(""), "index.html");
        assert_eq!(normalize_asset_path("/_app/app.js"), "_app/app.js");
        assert_eq!(asset_mime("index.html"), "text/html");
        assert!(asset_mime("_app/entry.js").contains("javascript"));
        assert_eq!(asset_mime("styles.css"), "text/css");
    }
}
