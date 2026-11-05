//! `ExternalUrls` holds `assembly://` links from the system until the window can act on them. The platform calls back
//! before the app is up and with no app context, so a link waits here, and a channel wakes the owner once it's running.
//! Links come from outside the app, so the queue is bounded and the parser accepts exact shapes only. A sign-in
//! callback's state and code are secrets: neither is logged, and `ExternalRoute`'s `Debug` leaves them out.

use std::cell::RefCell;
use std::collections::VecDeque;
use std::fmt;
use std::rc::Rc;

use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui_kit::*;
use percent_encoding::{AsciiSet, NON_ALPHANUMERIC, percent_decode_str, utf8_percent_encode};

use crate::core::intents::open_url::is_web_page;

pub const MAX_QUEUED: usize = 32;
pub const MAX_URL_BYTES: usize = 8 * 1024;
/// Everything in a link's id or machine is encoded except what a URL leaves alone anyway.
const LINK_VALUE: &AsciiSet = &NON_ALPHANUMERIC
    .remove(b'-')
    .remove(b'.')
    .remove(b'_')
    .remove(b'~');

#[derive(Clone, PartialEq, Eq)]
pub enum ExternalRoute {
    NotionOAuth {
        state: String,
        claim_code: String,
    },
    Task {
        page_id: String,
    },
    Session {
        id: String,
        machine: Option<String>,
    },
    Open {
        url: String,
    },
    File {
        path: String,
        machine: Option<String>,
    },
    Unsupported,
}

impl fmt::Debug for ExternalRoute {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotionOAuth { .. } => formatter.write_str("NotionOAuth"),
            Self::Task { page_id } => write!(formatter, "Task({page_id})"),
            Self::Session { id, machine } => write!(formatter, "Session({id}, {machine:?})"),
            Self::Open { url } => write!(formatter, "Open({url})"),
            Self::File { path, machine } => write!(formatter, "File({path}, {machine:?})"),
            Self::Unsupported => formatter.write_str("Unsupported"),
        }
    }
}

/// Notion's canonical id, dashed and lowercase, from either the dashed or the 32-digit form.
pub fn canonical_notion_id(text: &str) -> Option<String> {
    let digits: String = text.chars().filter(|c| *c != '-').collect();
    let dashes_fit = text.len() == 32
        || (text.len() == 36
            && [8, 13, 18, 23]
                .iter()
                .all(|&at| text.as_bytes()[at] == b'-'));
    if !dashes_fit || digits.len() != 32 || !digits.chars().all(|c| c.is_ascii_hexdigit()) {
        return None;
    }
    let digits = digits.to_ascii_lowercase();
    Some(format!(
        "{}-{}-{}-{}-{}",
        &digits[..8],
        &digits[8..12],
        &digits[12..16],
        &digits[16..20],
        &digits[20..]
    ))
}

pub fn parse_external(address: &str) -> ExternalRoute {
    let Ok(url) = url::Url::parse(address) else {
        return ExternalRoute::Unsupported;
    };
    if url.scheme() != "assembly" || !url.username().is_empty() || url.password().is_some() {
        return ExternalRoute::Unsupported;
    }
    match url.host_str() {
        Some("notion-oauth") if url.path().is_empty() || url.path() == "/" => oauth_route(&url),
        Some("task") => url
            .path()
            .strip_prefix('/')
            .and_then(canonical_notion_id)
            .filter(|_| url.query().is_none())
            .map_or(ExternalRoute::Unsupported, |page_id| ExternalRoute::Task {
                page_id,
            }),
        _ => exact_link(address).unwrap_or(ExternalRoute::Unsupported),
    }
}

/// A link from the system: as `parse_external`, and a `file:` URL is a file on this machine.
pub fn parse_incoming(address: &str) -> ExternalRoute {
    match address.strip_prefix("file:///") {
        Some(path) if is_bare_text(address) => {
            file_url_route(path).unwrap_or(ExternalRoute::Unsupported)
        }
        Some(_) => ExternalRoute::Unsupported,
        None => parse_external(address),
    }
}

/// The link that opens this session.
pub fn session_link(id: &str, machine: Option<&str>) -> String {
    let id = utf8_percent_encode(id, LINK_VALUE);
    match machine {
        Some(machine) => format!(
            "assembly://session/{id}?profile={}",
            utf8_percent_encode(machine, LINK_VALUE)
        ),
        None => format!("assembly://session/{id}"),
    }
}

/// The session, open and file shapes, read from the link's own text: `url::Url` cleans up dot segments, ports, empty
/// credentials, case and spare separators, so what it hands back isn't what the link says.
fn exact_link(address: &str) -> Option<ExternalRoute> {
    let rest = address
        .strip_prefix("assembly://")
        .filter(|_| is_bare_text(address))?;
    let (target, query) = match rest.split_once('?') {
        Some((target, query)) => (target, Some(query)),
        None => (rest, None),
    };
    match target {
        "open" | "open/" => open_route(query),
        "file" | "file/" => file_route(query),
        _ => session_route(target.strip_prefix("session/")?, query),
    }
}

/// No whitespace, control character or fragment anywhere in the raw text.
fn is_bare_text(address: &str) -> bool {
    !address
        .chars()
        .any(|c| c.is_control() || c.is_whitespace() || c == '#')
}

fn session_route(segment: &str, query: Option<&str>) -> Option<ExternalRoute> {
    let id = decoded(segment)
        .filter(|id| !id.is_empty() && !id.contains('/') && id != "." && id != "..")?;
    let [machine] = query_values(query, ["profile"])?;
    Some(ExternalRoute::Session { id, machine })
}

fn open_route(query: Option<&str>) -> Option<ExternalRoute> {
    let [address] = query_values(query, ["url"])?;
    let address = address?;
    is_web_page(&address).then_some(ExternalRoute::Open { url: address })
}

fn file_route(query: Option<&str>) -> Option<ExternalRoute> {
    let [path, machine] = query_values(query, ["path", "profile"])?;
    let path = path?;
    is_clean_path(&path).then_some(ExternalRoute::File { path, machine })
}

/// What follows `file:///`. No query or backslash, and each component is written out: an encoded slash would add one.
fn file_url_route(text: &str) -> Option<ExternalRoute> {
    if text.contains(['?', '\\']) {
        return None;
    }
    let path = decoded(text)?;
    if path.split('/').count() != text.split('/').count() || path.split('/').any(str::is_empty) {
        return None;
    }
    let path = format!("/{path}");
    is_clean_path(&path).then_some(ExternalRoute::File {
        path,
        machine: None,
    })
}

/// Absolute, with no `.` or `..` component.
fn is_clean_path(path: &str) -> bool {
    path.starts_with('/') && !path.split('/').any(|part| part == "." || part == "..")
}

/// The value of each key, in the order asked. Each key comes at most once, spelled exactly, with a non-empty value. A
/// query holding anything else is refused, even an empty pair.
fn query_values<const N: usize>(
    query: Option<&str>,
    keys: [&str; N],
) -> Option<[Option<String>; N]> {
    let mut values = [const { None }; N];
    let Some(query) = query else {
        return Some(values);
    };
    for pair in query.split('&') {
        let (key, value) = pair.split_once('=')?;
        let at = keys.iter().position(|known| *known == key)?;
        if values[at].is_some() || value.is_empty() {
            return None;
        }
        values[at] = Some(decoded(value)?);
    }
    Some(values)
}

/// Percent-decoded, and only escapes decode: a `+` stays a `+`. Bytes that aren't UTF-8, or a control character, are
/// refused.
fn decoded(text: &str) -> Option<String> {
    percent_decode_str(text)
        .decode_utf8()
        .ok()
        .filter(|value| !value.contains(char::is_control))
        .map(|value| value.into_owned())
}

/// Exactly one state and one code, each present and non-empty.
fn oauth_route(url: &url::Url) -> ExternalRoute {
    let (mut state, mut code) = (None, None);
    for (key, value) in url.query_pairs() {
        let slot = match key.as_ref() {
            "state" => &mut state,
            "code" => &mut code,
            _ => return ExternalRoute::Unsupported,
        };
        if slot.is_some() || value.is_empty() {
            return ExternalRoute::Unsupported;
        }
        *slot = Some(value.into_owned());
    }
    match (state, code) {
        (Some(state), Some(claim_code)) => ExternalRoute::NotionOAuth { state, claim_code },
        _ => ExternalRoute::Unsupported,
    }
}

struct Inner {
    queue: VecDeque<String>,
    wake: UnboundedSender<()>,
    woken: Option<UnboundedReceiver<()>>,
}

#[derive(Clone)]
pub struct ExternalUrls(Rc<RefCell<Inner>>);

impl ExternalUrls {
    pub fn new() -> Self {
        let (wake, woken) = unbounded();
        Self(Rc::new(RefCell::new(Inner {
            queue: VecDeque::new(),
            wake,
            woken: Some(woken),
        })))
    }

    /// From the platform. A link too long, already waiting, or past the bound is dropped.
    pub fn receive(&self, urls: Vec<String>) {
        let mut inner = self.0.borrow_mut();
        for url in urls {
            if url.len() <= MAX_URL_BYTES
                && inner.queue.len() < MAX_QUEUED
                && !inner.queue.contains(&url)
            {
                inner.queue.push_back(url);
            }
        }
        let _ = inner.wake.unbounded_send(());
    }

    pub fn take(&self) -> Vec<String> {
        self.0.borrow_mut().queue.drain(..).collect()
    }

    /// Call once the window is up. Hands each waiting link to `handle` now, and each later one as it comes.
    pub fn start(&self, handle: fn(ExternalRoute, &mut App), cx: &mut App) {
        let Some(mut woken) = self.0.borrow_mut().woken.take() else {
            return;
        };
        let urls = self.clone();
        let listening = cx.spawn(async move |cx| {
            loop {
                cx.update(|cx| {
                    for url in urls.take() {
                        handle(parse_incoming(&url), cx);
                    }
                });
                if woken.recv().await.is_err() {
                    return;
                }
            }
        });
        cx.set_global(Listening { _task: listening });
    }
}

/// The app owns the listener for as long as it runs.
struct Listening {
    _task: Task<()>,
}
impl Global for Listening {}
