//! Regression tests for what a phone can still reach once it is removed, and which shells it
//! can reach at all. They reuse the phone test server in `phone/mod.rs`, which is at its size
//! baseline.

use super::{client, operation, paired, phone_server, post_json, terminal_session, terminal_socket, LiveClient};
use crate::terminal::{self, TerminalStartRequest};
use futures_util::StreamExt;
use serde_json::{json, Value};
use std::time::Duration;
use tokio_tungstenite::tungstenite::{client::IntoClientRequest, Message};

/// Reads past any text frames until the server closes the socket; fails if it stays open.
async fn closed_by_server(socket: &mut LiveClient, what: &str) {
    loop {
        match tokio::time::timeout(Duration::from_secs(10), socket.next()).await {
            Ok(Some(Ok(Message::Text(_)))) => continue,
            Ok(Some(Ok(Message::Close(_)))) | Ok(None) | Ok(Some(Err(_))) => return,
            Ok(Some(Ok(_))) => continue,
            Err(_) => panic!("the {what} socket stayed open after the phone was removed"),
        }
    }
}

// Pins 17cf73f53 (live socket) and dc1f76f3d (terminal stream): removing a phone on the desktop
// cuts the sockets it already has open, not only its next request.
#[tokio::test]
async fn removing_a_phone_closes_its_open_live_and_terminal_sockets() {
    let (state, base) = phone_server().await;
    let token = paired(&state, &base).await;
    let root = terminal_session(&state, "lane");
    let (status, body) = post_json(&base, "/sessions/lane/terminal", &token, json!({"cols": 80, "rows": 24})).await;
    assert_eq!(status, reqwest::StatusCode::OK, "{body}");
    let terminal_id = body["terminalId"].as_str().unwrap().to_string();

    let mut live = format!("{}/phone/v1/live", base.replacen("http", "ws", 1)).into_client_request().unwrap();
    live.headers_mut().insert("authorization", format!("Bearer {token}").parse().unwrap());
    let (mut live, _) = tokio_tungstenite::connect_async(live).await.unwrap();
    let mut shell = terminal_socket(&base, &token, &terminal_id).await;
    let first = tokio::time::timeout(Duration::from_secs(10), shell.next()).await.unwrap().unwrap().unwrap();
    assert!(first.to_text().unwrap().contains("\"scrollback\""), "the stream opened: {first:?}");

    let devices = operation(&state, "phoneDevices", &json!({})).unwrap().unwrap();
    let device_id: Value = devices["devices"][0]["id"].clone();
    operation(&state, "phoneRevoke", &json!({"deviceId": device_id})).unwrap().unwrap();

    closed_by_server(&mut live, "live").await;
    closed_by_server(&mut shell, "terminal").await;

    terminal::close_terminal_session(&state.terminals.registry, &terminal_id).unwrap();
    std::fs::remove_dir_all(root).unwrap();
}

// Pins dc1f76f3d's reach: the phone can stream and close only a shell that belongs to a session in
// this machine's directory. A shell with no session, or one for a session the phone cannot see,
// is "not found" and keeps running.
#[tokio::test]
async fn the_phone_cannot_reach_shells_outside_this_machines_sessions() {
    let (state, base) = phone_server().await;
    let token = paired(&state, &base).await;
    let start = |owned_id: Option<&str>| {
        terminal::start_terminal_session(
            &state.terminals.registry,
            crate::projection_streams::ProjectionStreams::with_terminal_tap(state.terminals.output.clone()),
            TerminalStartRequest {
                cwd: std::env::temp_dir().display().to_string(),
                shell: Some("/bin/sh".to_string()),
                cols: Some(80),
                rows: Some(24),
                owned_id: owned_id.map(str::to_string),
                command: None,
            },
        )
        .unwrap()
        .session_id
    };
    for terminal_id in [start(None), start(Some("not-on-this-machine"))] {
        let close = client(reqwest::Method::DELETE, &base, &format!("/terminals/{terminal_id}"), &token).send().await.unwrap();
        assert_eq!(close.status(), reqwest::StatusCode::NOT_FOUND);
        let stream = client(reqwest::Method::GET, &base, &format!("/terminals/{terminal_id}/stream"), &token).send().await.unwrap();
        assert_eq!(stream.status(), reqwest::StatusCode::NOT_FOUND);
        let still_running = terminal::list_terminal_sessions(&state.terminals.registry).unwrap()
            .into_iter().any(|info| info.session_id == terminal_id && !info.exited);
        assert!(still_running, "a refused close leaves the shell alone");
        terminal::close_terminal_session(&state.terminals.registry, &terminal_id).unwrap();
    }
}
