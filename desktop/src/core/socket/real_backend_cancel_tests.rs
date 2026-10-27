//! A call the window drops stops its work on the real backend, down to the agent: a config change waiting on an
//! agent that never answers is cancelled, not left to the backend's 30 s limit.

use std::fs;
use std::path::Path;
use std::process::Command;
use std::sync::mpsc::Receiver;

use serde_json::{Value, json};

use super::connection_state::ConnectionState;
use super::event::Event;
use super::outcome::Outcome;
use super::real_backend::RealBackend;
use super::row_update::RowUpdate;
use super::test_client::{DEADLINE, TestClient as Client, outcome, updates};

/// An ACP agent that opens sessions and never answers a prompt or a config change. It says "prompted" when a prompt
/// comes and "setting" when a config change does, as message text, so the test waits on rows, not on time.
const STUB: &str = r#"
id() { printf '%s' "$1" | sed -n 's/^{"jsonrpc":"2.0","id":\([0-9]*\),.*/\1/p'; }
answer() { printf '{"jsonrpc":"2.0","id":%s,"result":%s}\n' "$1" "$2"; }
say() { printf '{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"stub","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"%s"}}}}\n' "$1"; }
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialize"'*) answer "$(id "$line")" '{"protocolVersion":1,"agentCapabilities":{},"authMethods":[]}' ;;
    *'"method":"session/new"'*) answer "$(id "$line")" '{"sessionId":"stub"}' ;;
    *'"method":"session/prompt"'*) say prompted ;;
    *'"method":"session/set_config_option"'*) say setting ;;
  esac
done
"#;

/// A backend whose one agent is the stub, and a git repo with one commit for its sessions to clone.
pub(crate) fn backend_with_stub() -> Option<(RealBackend, String)> {
    let mut repo = None;
    let backend = RealBackend::start_with_agents(|folder| {
        let stub = folder.join("stub.sh");
        fs::write(&stub, STUB).expect("write the stub agent");
        repo = Some(git_repo(&folder.join("repo")));
        json!([{ "id": "stub", "name": "Stub", "command": "sh", "args": [stub] }]).to_string()
    })?;
    Some((backend, repo.expect("the repo")))
}

fn git_repo(folder: &Path) -> String {
    for args in [
        &["init", "--quiet"][..],
        &[
            "-c",
            "user.name=test",
            "-c",
            "user.email=test@example.com",
            "commit",
            "--quiet",
            "--allow-empty",
            "--message",
            "start",
        ][..],
    ] {
        fs::create_dir_all(folder).expect("make the repo's folder");
        let status = Command::new("git")
            .args(args)
            .current_dir(folder)
            .status()
            .expect("run git");
        assert!(status.success(), "git {args:?} failed");
    }
    folder.to_string_lossy().into_owned()
}

/// Waits for a row whose payload holds `text`.
fn row_saying(rows: &Receiver<RowUpdate>, text: &str) {
    loop {
        match rows.recv_timeout(DEADLINE) {
            Ok(RowUpdate::Row(row)) if row.payload.to_string().contains(text) => return,
            Ok(_) => {}
            Err(_) => panic!("no row saying {text:?} within the deadline"),
        }
    }
}

fn answer(outcome: Outcome<Value>) -> Value {
    match outcome {
        Outcome::Ok(value) => value,
        other => panic!("expected a result, got {other:?}"),
    }
}

// Pins: g0-socket#4
#[test]
fn dropping_a_config_change_the_agent_never_answers_cancels_it_on_the_backend() {
    let Some((backend, repo)) = backend_with_stub() else {
        return;
    };
    let mut client = Client::start_with(backend.endpoint());
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert!(matches!(client.state(), ConnectionState::Connected { .. }));

    let (_, created) = client.call_with(
        "sessions",
        "create",
        json!({ "agent": "stub", "repoUrl": repo }),
        None,
    );
    let created = answer(outcome(created));
    let session = created["sessionId"].as_str().expect("a session id");
    let rows = updates(client.rows_of(session, None));
    let (_, sent) = client.call_with(
        "send",
        "send",
        json!({ "session": session, "text": "hi" }),
        None,
    );
    answer(outcome(sent));
    // The agent's session is open: the prompt went to it.
    row_saying(&rows, "prompted");

    let (id, set) = client.call_with(
        "config",
        "set",
        json!({ "session": session, "configId": "model", "value": "other" }),
        None,
    );
    row_saying(&rows, "setting");
    // What a dropped `PendingCall` sends.
    client.events.send(Event::Cancel { id }).unwrap();

    assert!(matches!(outcome(set), Outcome::Aborted));
}
