//! The one resolver for file links, from deep links, chat links and tool rows. A relative path opens through the
//! session it came with. An absolute or `~` path goes to the backend as a path target on the link's machine, which
//! decides whether it's a file at all and whether it lies inside the session's checkout: inside opens as that session's
//! file, anything else by machine and real path. The desktop never expands `~`. A session that came with no source
//! (a read-only transcript, say) keeps its machine, from the sessions list, and its source is asked of that machine.
//!
//! The selection stays as it is and nothing waits: the calls ask, and their answer is the end of the link. Every
//! link's first call goes out when it arrives and each link waits for the one before, so files open in the order they
//! came. A link under another selection than the one before it doesn't wait: the newer selection drops the older
//! links, whose calls are cancelled. A dropped connection answers every call at once; a call the backend never answers
//! while the connection stays up has no event at all, so each link has a deadline, which cancels it.

use std::time::Duration;

use gpui_kit::*;
use serde_json::{Value, json};

use super::deep_links::show_notice;
use crate::core::engine::SessionKey;
use crate::core::intents::open_file::{OpenFile, OutsideFile, names_its_own_place};
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::client::SocketClient;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;
use crate::features::files::entry::FileOwnership;
use crate::features::session::client::source_resolution;

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

/// A line and column to open at.
pub type Jump = (Option<u32>, Option<u32>);

/// A session a link came with, and its source.
type From = (SessionKey, ExpectedRowSource);

const NO_SESSION: &str = "Select a session to open this file.";

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
            return show_notice(NO_SESSION.into(), cx);
        };
        // A relative path is only in this session's checkout when the link is for the session's machine.
        if machine != source.route_machine {
            let on = machine.as_deref().unwrap_or(THIS_COMPUTER);
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
    resolve(path, machine, from.map(Came::Sourced), jump, cx)
}

/// Opens an absolute or `~` `path` from a link that came with `session` and no source. The session's machine is the one
/// the sessions list says. A session the list doesn't hold is placed only where nothing is guessed: the selected one
/// opens with the selection's source, and with no remote machine configured it can only be on this machine, which
/// resolves with no inside hint. Anything else can't be placed.
pub fn open_unsourced(path: String, session: String, jump: Jump, cx: &mut App) {
    let listed = Sessions::try_global(cx).and_then(|sessions| {
        let sessions = sessions.read(cx);
        let item = sessions
            .items()
            .iter()
            .find(|item| item.session_id == session)?;
        Some(item.machine.clone().filter(|machine| !machine.is_empty()))
    });
    if let Some(machine) = listed {
        return resolve(path, machine, Some(Came::Unsourced(session)), jump, cx);
    }
    let selected = SelectedSession::try_global(cx).and_then(|selected| {
        let selected = selected.read(cx);
        let key = selected.key().filter(|key| key.id == session)?;
        Some((key.clone(), selected.source()?.clone()))
    });
    if let Some(from) = selected {
        let machine = from.1.route_machine.clone();
        return resolve(path, machine, Some(Came::Sourced(from)), jump, cx);
    }
    if !Machines::any_remote(cx) {
        return resolve(path, None, None, jump, cx);
    }
    show_notice(didnt_open(NO_MACHINE), cx)
}

const NO_MACHINE: &str = "Assembly can't tell which machine this session is on.";

/// The session a path link came with.
enum Came {
    Sourced(From),
    /// Its id; its source is asked of the link's machine.
    Unsourced(String),
}

fn resolve(path: String, machine: Option<String>, came: Option<Came>, jump: Jump, cx: &mut App) {
    if let (true, Some(on)) = (Machines::missing(machine.as_deref(), cx), &machine) {
        return show_notice(not_connected(on), cx);
    }
    let Some(socket) = SocketClient::try_global(cx) else {
        return show_notice(didnt_open("The backend isn't running."), cx);
    };
    // The session's own id on its machine says whether the file is inside its checkout; a session elsewhere can't. Its
    // first call goes out now: the session's question, or for a session with no source, its machine's source.
    let (first, unsourced) = match came {
        Some(Came::Sourced(from)) if from.1.route_machine == machine => (Some(from), None),
        Some(Came::Unsourced(id)) => {
            let args = source_resolution::build_args(machine.as_deref()).unwrap_or_default();
            let asking = socket
                .read(cx)
                .call::<Value>("sessions", "resolve_source", args, None);
            (None, Some((id, asking)))
        }
        _ => (None, None),
    };
    let hint = first.as_ref().map(|from| ask_session(&socket, from, cx));
    let selected = SelectedSession::global(cx).read(cx);
    let at: Selection = (selected.key().cloned(), selected.source().cloned());
    let asking = cx.default_global::<Asking>();
    // Dropping the older selection's links cancels their calls; they'd end quietly anyway, as the person moved on.
    let earlier = asking.last.take().filter(|_| asking.key == at.0);
    asking.key = at.0.clone();
    let task = cx.spawn(async move |cx| {
        let (target_machine, app) = (machine.clone(), &*cx);
        let resolve = async move {
            let (mut from, mut hint) = (first, hint);
            if let Some((id, asking)) = unsourced {
                let source = match asking.await {
                    Outcome::Ok(value) => {
                        // This machine's engine, which the answer is checked against, is known once it has answered.
                        let local = app.update(|cx| socket.read(cx).engine_machine());
                        let route = target_machine.as_deref();
                        source_resolution::parse_reply(value, route, local.as_deref()).ok()
                    }
                    // A machine that can't say is no inside hint, so the file opens outside, locked.
                    Outcome::Failed(_) => None,
                    Outcome::Aborted => return (Outcome::Aborted, None),
                };
                from = source.map(|source| {
                    let key = SessionKey {
                        machine: source.machine.clone(),
                        id,
                    };
                    (key, source)
                });
                hint = from
                    .as_ref()
                    .map(|from| app.update(|cx| ask_session(&socket, from, cx)));
            }
            let inside_of = match hint {
                Some(hint) => match hint.await {
                    Outcome::Ok(owner) => Some(owner.remote_session_id).filter(|id| !id.is_empty()),
                    // A session with no folder to compare against can't hold the file, so it opens outside, which
                    // means locked until Edit anyway: the safe default when the session can't say.
                    Outcome::Failed(_) => None,
                    Outcome::Aborted => return (Outcome::Aborted, from),
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
            (call.await, from)
        };
        let (answer, from) = resolve
            .with_timeout(ANSWER_WITHIN, cx.background_executor())
            .await
            .unwrap_or_else(|_| {
                let failure = CallFailure {
                    code: ErrorCode::Failed,
                    message: format!(
                        "The backend didn't answer within {} seconds.",
                        ANSWER_WITHIN.as_secs()
                    ),
                };
                (Outcome::Failed(failure), None)
            });
        if let Some(earlier) = earlier {
            earlier.await;
        }
        cx.update(|cx| answered(answer, machine, from, at, jump, cx))
    });
    cx.default_global::<Asking>().last = Some(task);
}

/// Asks the session's machine for its checkout, whose session id there is the inside hint.
fn ask_session(
    socket: &Entity<SocketClient>,
    (key, source): &From,
    cx: &App,
) -> crate::core::socket::pending_call::PendingCall<FileOwnership> {
    socket.read(cx).call::<FileOwnership>(
        "files",
        "owner",
        json!({ "session": key.id, "expectedSource": source }),
        None,
    )
}

fn answered(
    answer: Outcome<FileOwnership>,
    machine: Option<String>,
    from: Option<From>,
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
        Outcome::Failed(failure) => return show_notice(refusal_words(failure, machine), cx),
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

/// The words for a path target the backend refused. `machine` is the one the link named.
fn refusal_words(failure: CallFailure, machine: Option<String>) -> String {
    let code = match &failure.code {
        ErrorCode::Refused { code } => code.as_str(),
        // A listed machine that isn't connected.
        ErrorCode::Unreachable { machine: named } => {
            let named = Some(named.clone()).filter(|named| !named.is_empty());
            if let Some(on) = named.or(machine) {
                return not_connected(&on);
            }
            return didnt_open(&failure.message);
        }
        _ => return didnt_open(&failure.message),
    };
    match code {
        "not_a_path" => "That file link isn't a file path.".into(),
        "not_full_path" => "That file link isn't a full path.".into(),
        "not_regular" => "That isn't a regular file, so it can't open.".into(),
        // The backend's own words name the machine.
        "remote_older" => failure.message,
        _ => didnt_open(&failure.message),
    }
}

/// What a notice calls the machine the app runs on.
const THIS_COMPUTER: &str = if cfg!(target_os = "macos") {
    "this Mac"
} else {
    "this computer"
};

fn not_connected(machine: &str) -> String {
    format!("That file is on {machine}, which this app isn't connected to.")
}

/// `That file didn't open: <reason>.`, the reason with a capital and one full stop, as spec 1.2 words it.
fn didnt_open(reason: &str) -> String {
    let reason = reason.trim().trim_end_matches('.');
    let mut letters = reason.chars();
    let reason: String = match letters.next() {
        Some(first) => first.to_uppercase().chain(letters).collect(),
        None => String::new(),
    };
    format!("That file didn't open: {reason}.")
}
