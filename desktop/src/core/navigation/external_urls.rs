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

pub const MAX_QUEUED: usize = 32;
pub const MAX_URL_BYTES: usize = 8 * 1024;

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
        _ => ExternalRoute::Unsupported,
    }
}

/// A link from the system: as `parse_external`, and a `file:` URL is a file on this machine.
pub fn parse_incoming(address: &str) -> ExternalRoute {
    parse_external(address)
}

/// The link that opens this session.
pub fn session_link(_id: &str, _machine: Option<&str>) -> String {
    String::new()
}

/// Exactly one state and one code, each present and non-empty.
fn oauth_route(url: &url::Url) -> ExternalRoute {
    let (mut state, mut code) = (None, None);
    for (key, value) in url.query_pairs() {
        let slot = match key.as_ref() {
            "state" => &mut state,
            "claimCode" => &mut code,
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
                        handle(parse_external(&url), cx);
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
