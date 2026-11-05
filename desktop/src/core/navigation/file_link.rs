//! The one resolver for file links, from deep links, chat links and tool rows. A relative path opens through the
//! session it came with. An absolute or `~` path goes to the backend as a path target on the link's machine, which
//! decides whether it's a file at all and whether it lies inside the session's checkout: inside opens as that session's
//! file, anything else by machine and real path. The desktop never expands `~`.
//!
//! The selection stays as it is and nothing waits: the calls ask, and their answer is the end of the link. Every
//! link's first call goes out when it arrives and each link waits for the one before, so files open in the order they
//! came. A link under another selection than the one before it doesn't wait: the newer selection drops the older
//! links, whose calls are cancelled. A dropped connection answers every call at once; a call the backend never answers
//! while the connection stays up has no event at all, so each link has a deadline, which cancels it.

use std::path::Path;
use std::time::Duration;

use gpui_kit::*;
use serde_json::json;

use super::deep_links::show_notice;
use crate::core::engine::SessionKey;
use crate::core::intents::open_file::{OpenFile, OutsideFile};
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::client::SocketClient;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;
use crate::features::files::entry::FileOwnership;

/// How long the backend has to answer a link's calls.
const ANSWER_WITHIN: Duration = Duration::from_secs(30);

/// The newest link's task, which holds the one before it until that has finished, and the selection they came under.
#[derive(Default)]
struct Asking {
    key: Option<SessionKey>,
    last: Option<Task<()>>,
}

impl Global for Asking {}

/// The selected session as a link found it.
type Selection = (Option<SessionKey>, Option<ExpectedRowSource>);

/// Whether a path goes to the backend as a path target rather than opening relative to a session.
pub(crate) fn names_its_own_place(path: &str) -> bool {
    Path::new(path).is_absolute() || path.starts_with('~')
}

/// A line and column to open at.
pub type Jump = (Option<u32>, Option<u32>);

/// Opens `path` from a link, at `jump` when given. `machine` is the route machine the link names, `None` for this
/// machine; `from` is the session the link came with, if any.
pub fn open(
    path: String,
    machine: Option<String>,
    from: Option<(SessionKey, ExpectedRowSource)>,
    jump: Jump,
    cx: &mut App,
) {
    if !names_its_own_place(&path) {
        let Some((key, source)) = from else {
            return show_notice("Select a session to open this file.".into(), cx);
        };
        // A relative path is only in this session's checkout when the link is for the session's machine.
        if machine != source.route_machine {
            let on = machine.as_deref().unwrap_or("this Mac");
            return show_notice(format!("That file is on {on}."), cx);
        }
        return cx.dispatch_action(&OpenFile {
            session_id: key.id,
            path,
            expected_source: Some(source),
            line: jump.0,
            column: jump.1,
            pinned: true,
            outside: None,
        });
    }
    if let (true, Some(on)) = (Machines::missing(machine.as_deref(), cx), &machine) {
        return show_notice(not_connected(on), cx);
    }
    let Some(socket) = SocketClient::try_global(cx) else {
        return show_notice(didnt_open("The backend isn't running."), cx);
    };
    // The session's own id on its machine says whether the file is inside its checkout; a session elsewhere can't.
    let hint = from
        .as_ref()
        .filter(|(_, source)| source.route_machine == machine)
        .map(|(key, source)| {
            socket.read(cx).call::<FileOwnership>(
                "files",
                "owner",
                json!({ "session": key.id, "expectedSource": source }),
                None,
            )
        });
    let selected = SelectedSession::global(cx).read(cx);
    let at: Selection = (selected.key().cloned(), selected.source().cloned());
    let asking = cx.default_global::<Asking>();
    // Dropping the older selection's links cancels their calls; they'd end quietly anyway, as the person moved on.
    let earlier = asking.last.take().filter(|_| asking.key == at.0);
    asking.key = at.0.clone();
    let task = cx.spawn(async move |cx| {
        let (target_machine, app) = (machine.clone(), &*cx);
        let resolve = async move {
            let inside_of = match hint {
                Some(hint) => match hint.await {
                    Outcome::Ok(owner) => Some(owner.remote_session_id).filter(|id| !id.is_empty()),
                    // A session with no folder to compare against can't hold the file, so it opens outside, which
                    // means locked until Edit anyway: the safe default when the session can't say.
                    Outcome::Failed(_) => None,
                    Outcome::Aborted => return Outcome::Aborted,
                },
                None => None,
            };
            let target = json!({ "path": path, "machine": target_machine, "insideOf": inside_of });
            let call = app.update(|cx| {
                socket.read(cx).call::<FileOwnership>(
                    "files",
                    "owner",
                    json!({ "target": target }),
                    None,
                )
            });
            call.await
        };
        let answer = resolve
            .with_timeout(ANSWER_WITHIN, cx.background_executor())
            .await
            .unwrap_or_else(|_| {
                Outcome::Failed(CallFailure {
                    code: ErrorCode::Failed,
                    message: format!(
                        "The backend didn't answer within {} seconds.",
                        ANSWER_WITHIN.as_secs()
                    ),
                })
            });
        if let Some(earlier) = earlier {
            earlier.await;
        }
        cx.update(|cx| answered(answer, machine, from, at, jump, cx))
    });
    cx.default_global::<Asking>().last = Some(task);
}

fn answered(
    answer: Outcome<FileOwnership>,
    machine: Option<String>,
    from: Option<(SessionKey, ExpectedRowSource)>,
    at: Selection,
    jump: Jump,
    cx: &mut App,
) {
    let selected = SelectedSession::global(cx).read(cx);
    // The person has moved on.
    if selected.key() != at.0.as_ref() {
        return;
    }
    if selected.source() != at.1.as_ref() {
        return show_notice(didnt_open("The session's source changed."), cx);
    }
    let owner = match answer {
        Outcome::Ok(owner) => owner,
        Outcome::Failed(failure) => return show_notice(refusal_words(failure), cx),
        Outcome::Aborted => return show_notice(didnt_open("The connection dropped."), cx),
    };
    let Some(file) = owner.file else {
        return show_notice(didnt_open("The backend didn't say which file."), cx);
    };
    let intent = match from.filter(|_| !owner.remote_session_id.is_empty()) {
        Some((key, source)) => OpenFile {
            session_id: key.id,
            path: file,
            expected_source: Some(source),
            line: jump.0,
            column: jump.1,
            pinned: true,
            outside: None,
        },
        None => {
            // An outside answer's `file` is a bare file name and its `checkoutRoot` the real absolute parent folder,
            // which is "/" for a file at the top, so the join never doubles the slash.
            let real = format!("{}/{file}", owner.checkout_root.trim_end_matches('/'));
            OpenFile {
                session_id: String::new(),
                path: real.clone(),
                expected_source: None,
                line: jump.0,
                column: jump.1,
                pinned: true,
                outside: Some(OutsideFile {
                    machine,
                    path: real,
                }),
            }
        }
    };
    cx.dispatch_action(&intent);
}

/// The words for a path target the backend refused.
fn refusal_words(failure: CallFailure) -> String {
    let ErrorCode::Refused { code } = &failure.code else {
        return didnt_open(&failure.message);
    };
    match code.as_str() {
        "not_a_path" => "That file link isn't a file path.".into(),
        "not_full_path" => "That file link isn't a full path.".into(),
        "not_regular" => "That isn't a regular file, so it can't open.".into(),
        // The backend's own words name the machine.
        "remote_older" => failure.message,
        _ => didnt_open(&failure.message),
    }
}

fn not_connected(machine: &str) -> String {
    format!("That file is on {machine}, which this app isn't connected to.")
}

fn didnt_open(reason: &str) -> String {
    format!("That file didn't open: {reason}")
}
