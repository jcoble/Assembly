//! Regression tests for the Terminal tab's shells: what happens when a shell exits, is
//! closed, or the app quits, and what a re-attaching view gets replayed. Each test waits
//! on the shell's own termination frame (through the output tap a remote server uses),
//! never on a sleep. They live here because `terminal/mod.rs` is at its size baseline.
#![cfg(not(windows))]

use super::super::{
    close_terminal_session, list_terminal_sessions, read_terminal_session_scrollback,
    start_terminal_session, start_terminal_session_typed, TerminalKind, TerminalOutputFrame,
    TerminalRegistry, TerminalSessionInfo, TerminalStartRequest, ToolTerminalIdentity,
};
use crate::projection_streams::ProjectionStreams;
use std::time::Duration;
use tokio::sync::broadcast;

fn tapped() -> (ProjectionStreams, broadcast::Receiver<TerminalOutputFrame>) {
    let (tap, frames) = broadcast::channel(1024);
    (ProjectionStreams::with_terminal_tap(tap), frames)
}

fn request(owned_id: Option<&str>, command: Option<&str>) -> TerminalStartRequest {
    TerminalStartRequest {
        cwd: std::env::temp_dir().display().to_string(),
        shell: Some("/bin/sh".to_string()),
        cols: Some(80),
        rows: Some(20),
        owned_id: owned_id.map(str::to_string),
        command: command.map(str::to_string),
    }
}

/// Every frame for `session_id` up to and including its termination frame.
async fn frames_until_terminated(
    frames: &mut broadcast::Receiver<TerminalOutputFrame>,
    session_id: &str,
) -> Vec<TerminalOutputFrame> {
    let mut seen = Vec::new();
    loop {
        let frame = tokio::time::timeout(Duration::from_secs(10), frames.recv())
            .await
            .unwrap_or_else(|_| panic!("{session_id} never sent its termination frame"))
            .expect("the output tap stays open");
        if frame.output.session_id != session_id {
            continue;
        }
        let terminated = frame.output.terminated;
        seen.push(frame);
        if terminated {
            return seen;
        }
    }
}

fn output_text(frames: &[TerminalOutputFrame]) -> String {
    frames.iter().map(|frame| frame.output.data.as_str()).collect()
}

fn listed(registry: &TerminalRegistry, session_id: &str) -> Option<TerminalSessionInfo> {
    list_terminal_sessions(registry)
        .unwrap()
        .into_iter()
        .find(|info| info.session_id == session_id)
}

// Pins f732d1931 (exited shells stay as tombstones), 968813f78 (the exit frame comes after
// the last output) and 1e444eabc (a one-command session reports the command's exit code).
#[tokio::test]
async fn an_exited_shell_stays_listed_with_its_exit_code_and_readable_final_output() {
    let registry = TerminalRegistry::default();
    let (streams, mut frames) = tapped();
    let session = start_terminal_session(
        &registry,
        streams,
        request(None, Some("printf 'final-output'; exit 3")),
    )
    .unwrap();

    let seen = frames_until_terminated(&mut frames, &session.session_id).await;
    let (terminated, output) = seen.split_last().unwrap();
    assert!(
        output_text(output).contains("final-output"),
        "the last output arrives before the exit frame: {:?}",
        output_text(output)
    );
    assert_eq!(terminated.output.exit_code, Some(3));
    let last_data = output.last().expect("the command printed").sequence;
    assert!(terminated.sequence > last_data, "the exit frame is ordered after the output");

    let tombstone = listed(&registry, &session.session_id).expect("an exited shell stays listed");
    assert!(tombstone.exited);
    assert_eq!(tombstone.exit_code, Some(3));
    let replay = read_terminal_session_scrollback(&registry, &session.session_id, None)
        .unwrap()
        .expect("an exited shell's output stays readable");
    assert!(replay.data.contains("final-output"));
    assert_eq!(
        replay.sequence, last_data,
        "the replay watermark is the last output frame, so a view drops exactly the frames it already has"
    );

    assert!(close_terminal_session(&registry, &session.session_id).unwrap());
    assert!(listed(&registry, &session.session_id).is_none(), "only a close removes it");
}

// Pins the kill-before-unregister order of `kill_and_remove` (e7a8479e4): closing a running
// shell ends its process, it does not just forget the id.
#[tokio::test]
async fn closing_a_running_shell_ends_its_process() {
    let registry = TerminalRegistry::default();
    let (streams, mut frames) = tapped();
    let session = start_terminal_session(&registry, streams, request(None, None)).unwrap();

    assert!(close_terminal_session(&registry, &session.session_id).unwrap());
    assert!(listed(&registry, &session.session_id).is_none());
    let seen = frames_until_terminated(&mut frames, &session.session_id).await;
    assert!(seen.last().unwrap().output.terminated, "the shell process exited");
}

// Quitting the app must not leave shells running: `shutdown` ends user and agent terminals.
#[tokio::test]
async fn shutdown_ends_every_shell_it_holds() {
    let registry = TerminalRegistry::default();
    let (streams, mut frames) = tapped();
    let user = start_terminal_session(&registry, streams.clone(), request(Some("owner-quit"), None))
        .unwrap();
    let tool = start_terminal_session_typed(
        &registry,
        request(Some("owner-quit"), None),
        TerminalKind::AgentTool,
        Some(ToolTerminalIdentity {
            owned_id: "owner-quit".into(),
            turn_id: "turn".into(),
            tool_call_id: "tool".into(),
            terminal_id: "agent-tool-quit".into(),
        }),
        streams,
    )
    .unwrap();

    registry.shutdown();
    assert!(list_terminal_sessions(&registry).unwrap().is_empty());
    let mut ended = Vec::new();
    while ended.len() < 2 {
        let frame = tokio::time::timeout(Duration::from_secs(10), frames.recv())
            .await
            .expect("both shells end after shutdown")
            .unwrap();
        if frame.output.terminated {
            ended.push(frame.output.session_id);
        }
    }
    ended.sort();
    let mut expected = vec![user.session_id, tool.session_id];
    expected.sort();
    assert_eq!(ended, expected);
}

// Pins 0bf6abb82's per-owner reuse at its edge: a session whose shell has exited gets a
// fresh shell instead of being handed the dead one back.
#[tokio::test]
async fn reopening_an_owner_whose_shell_exited_starts_a_new_shell() {
    let registry = TerminalRegistry::default();
    let (streams, mut frames) = tapped();
    let first =
        start_terminal_session(&registry, streams.clone(), request(Some("owner-exit"), Some("exit 0")))
            .unwrap();
    frames_until_terminated(&mut frames, &first.session_id).await;

    let second =
        start_terminal_session(&registry, streams, request(Some("owner-exit"), None)).unwrap();
    assert_ne!(second.session_id, first.session_id, "the exited shell is not reused");
    assert!(!second.exited);
    assert!(listed(&registry, &first.session_id).is_some_and(|info| info.exited));

    close_terminal_session(&registry, &second.session_id).unwrap();
    close_terminal_session(&registry, &first.session_id).unwrap();
}

// Pins 6fb8ee5d3 (the shell knows its Assembly session) and the colour environment agent
// CLIs read to decide whether to print colour.
#[tokio::test]
async fn the_shell_gets_its_session_id_and_a_colour_terminal_environment() {
    let registry = TerminalRegistry::default();
    let (streams, mut frames) = tapped();
    let session = start_terminal_session(
        &registry,
        streams,
        request(
            Some("  owner-env  "),
            Some(r#"printf '[%s|%s|%s|%s]' "$COMMANDBAR_SESSION_ID" "$TERM" "$TERM_PROGRAM" "$COLORTERM""#),
        ),
    )
    .unwrap();
    assert_eq!(session.owned_id.as_deref(), Some("owner-env"));

    let output = output_text(&frames_until_terminated(&mut frames, &session.session_id).await);
    assert!(
        output.contains("[owner-env|xterm-256color|Assembly|truecolor]"),
        "{output:?}"
    );
    close_terminal_session(&registry, &session.session_id).unwrap();
}

// Pins 1ff90bab4: a bounded replay (the phone asks for at most 1 MB) is the newest output,
// cut on a character boundary rather than through a multibyte character.
#[tokio::test]
async fn a_bounded_replay_returns_the_newest_output_on_a_character_boundary() {
    let registry = TerminalRegistry::default();
    let (streams, mut frames) = tapped();
    let session =
        start_terminal_session(&registry, streams, request(None, Some("printf 'head-世界'")))
            .unwrap();
    frames_until_terminated(&mut frames, &session.session_id).await;

    let read = |max_bytes| {
        read_terminal_session_scrollback(&registry, &session.session_id, max_bytes)
            .unwrap()
            .unwrap()
            .data
    };
    // The last four bytes start inside 世, so the replay starts at the next character.
    assert_eq!(read(Some(4)), "界");
    assert_eq!(read(Some(6)), "世界");
    assert!(read(Some(1 << 20)).ends_with("head-世界"), "a limit above the size returns it all");
    close_terminal_session(&registry, &session.session_id).unwrap();
}
