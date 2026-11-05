//! A file link opens through the selected session, the only way the backend reads a file, or says why it can't. The
//! selection stays as it is and nothing waits: the one call asks for the folder, and its answer is the end of the link.
//! A newer link that gets as far as asking replaces one still asking, which drops that call.

use std::path::Path;

use gpui_kit::*;
use serde_json::json;

use super::deep_links::show_notice;
use crate::core::engine::SessionKey;
use crate::core::intents::open_file::OpenFile;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;
use crate::features::files::entry::FileOwnership;

struct Asking {
    _call: Task<()>,
}

impl Global for Asking {}

/// Checks a file link against the selected session, then asks the backend for that session's folder.
pub fn open(path: String, machine: Option<String>, cx: &mut App) {
    let selected = SelectedSession::global(cx).read(cx);
    let (Some(key), Some(source)) = (selected.key().cloned(), selected.source().cloned()) else {
        return show_notice("Select a session to open this file.".into(), cx);
    };
    if machine != source.route_machine {
        let on = machine.as_deref().unwrap_or("this Mac");
        return show_notice(format!("That file is on {on}."), cx);
    }
    let Some(socket) = SocketClient::try_global(cx) else {
        return show_notice(didnt_open("The backend isn't running."), cx);
    };
    let owner = socket.read(cx).call::<FileOwnership>(
        "files",
        "owner",
        json!({ "session": key.id, "expectedSource": source }),
        None,
    );
    let asking = cx.spawn(async move |cx| {
        let answer = owner.await;
        cx.update(|cx| answered(answer, &path, key, source, cx))
    });
    cx.set_global(Asking { _call: asking });
}

fn answered(
    answer: Outcome<FileOwnership>,
    path: &str,
    key: SessionKey,
    source: ExpectedRowSource,
    cx: &mut App,
) {
    // The person has moved on.
    let selected = SelectedSession::global(cx).read(cx);
    if selected.key() != Some(&key) || selected.source() != Some(&source) {
        return;
    }
    let owner = match answer {
        Outcome::Ok(owner) => owner,
        Outcome::Failed(failure) => return show_notice(didnt_open(&failure.message), cx),
        Outcome::Aborted => return show_notice(didnt_open("The connection dropped."), cx),
    };
    let Some(path) = inside(path, &owner.checkout_root) else {
        return show_notice("That file isn't in this session's folder.".into(), cx);
    };
    cx.dispatch_action(&OpenFile {
        session_id: key.id,
        path,
        expected_source: Some(source),
        line: None,
        column: None,
        pinned: true,
    });
}

/// The path from the folder, comparing whole components. The folder itself isn't inside.
fn inside(path: &str, root: &str) -> Option<String> {
    let relative = Path::new(path).strip_prefix(root).ok()?.to_str()?;
    (!relative.is_empty()).then(|| relative.to_string())
}

fn didnt_open(reason: &str) -> String {
    format!("That file didn't open: {reason}")
}
