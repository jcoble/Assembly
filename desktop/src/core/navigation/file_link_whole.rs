//! A link whose line suffix was read (`a.rs:12`, `a.rs:12:4`) names a line, unless a file of the link's whole name
//! exists: that's asked first, on the file's machine, before the suffix is read. A relative link asks inside its
//! session's checkout, so it needs the session and its source; with neither it has nowhere to look, and the suffix is
//! read as a line. Either way the open then goes on as the intent it settled on.

use gpui_kit::*;
use serde_json::json;

use crate::core::intents::open_file::{OpenFile, names_its_own_place};
use crate::core::socket::client::SocketClient;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::outcome::Outcome;
use crate::features::files::entry::FileOwnership;

/// What the backend answers for a path that names no file, which is the one answer that reads the suffix as a line
/// (`FileOwner.RequireRegular`); it carries no code.
const NOT_THERE: &str = "that file isn't there";

/// The one link being checked; a newer link replaces it, which drops the older question.
#[derive(Default)]
struct Checking(Option<Task<()>>);

impl Global for Checking {}

/// Opens `intent`, whose `whole` holds the link as written: by that whole name when the file exists, else at its line.
/// It's called from the `OpenFile` listener, so the settled intent is dispatched once that listener has returned.
pub fn open_whole_first(mut intent: OpenFile, cx: &mut App) {
    let Some(whole) = intent.whole.take() else {
        return cx.defer(move |cx| cx.dispatch_action(&intent));
    };
    let source = intent.expected_source.clone();
    let session = Some(intent.session_id.clone()).filter(|id| !id.is_empty());
    let socket = SocketClient::try_global(cx);
    let relative = !names_its_own_place(&whole);
    let (Some(socket), false) = (socket, relative && (session.is_none() || source.is_none()))
    else {
        return cx.defer(move |cx| cx.dispatch_action(&intent));
    };
    let machine = source
        .as_ref()
        .and_then(|source| source.route_machine.clone());
    // A relative name is looked for in the session's checkout, which its machine names.
    let folder = (relative).then(|| {
        let args = json!({ "session": session, "expectedSource": source });
        socket
            .read(cx)
            .call::<FileOwnership>("files", "owner", args, None)
    });
    let task = cx.spawn(async move |cx| {
        let (path, inside_of) = match folder {
            Some(folder) => match folder.await {
                Outcome::Ok(owner) => {
                    let root = owner.checkout_root.trim_end_matches('/');
                    (format!("{root}/{whole}"), Some(owner.remote_session_id))
                }
                _ => return cx.update(|cx| cx.dispatch_action(&intent)),
            },
            None => (whole.clone(), None),
        };
        let target = json!({ "path": path, "machine": machine, "insideOf": inside_of });
        let call = cx.update(|cx| {
            socket.read(cx).call::<FileOwnership>(
                "files",
                "owner",
                json!({ "target": target }),
                None,
            )
        });
        let exists = match call.await {
            Outcome::Ok(_) => true,
            Outcome::Failed(failure) => {
                !(failure.code == ErrorCode::Failed && failure.message == NOT_THERE)
            }
            Outcome::Aborted => false,
        };
        if exists {
            intent.path = whole;
            intent.line = None;
            intent.column = None;
        }
        cx.update(|cx| cx.dispatch_action(&intent));
    });
    cx.default_global::<Checking>().0 = Some(task);
}
