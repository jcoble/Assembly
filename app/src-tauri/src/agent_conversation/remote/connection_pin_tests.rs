//! Regression tests that pin how a desktop connects to, loses, and catches up with a
//! remote machine (TSK-1167). Each test names the behavior and the commit it protects.

use super::super::connection_tests::{history_event, session};
use super::super::*;
use tokio_tungstenite::tungstenite::Error as SocketError;

type ServerSocket = tokio_tungstenite::WebSocketStream<tokio::net::TcpStream>;

fn store() -> Arc<SessionStore> {
    Arc::new(SessionStore::open_in_memory().unwrap())
}

fn manager_publishing_to(published: mpsc::UnboundedSender<AgentConversationEvent>) -> RemoteConnectionManager {
    RemoteConnectionManager::from_environment(
        Arc::new(move |event| { let _ = published.send(event); }), Arc::new(|_| {}), store()).unwrap()
}

fn quiet_manager() -> RemoteConnectionManager {
    RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap()
}

fn machine(id: &str, name: &str, ssh_target: &str) -> RemoteAssemblyProfile {
    RemoteAssemblyProfile { id: id.into(), name: name.into(), ssh_target: ssh_target.into(),
        source_root: String::new(), default_cwd: String::new() }
}

fn event_for(owned_id: &str, sequence: i64) -> AgentConversationEvent {
    AgentConversationEvent { owned_id: owned_id.into(), ..history_event(sequence) }
}

fn snapshot(events: Vec<AgentConversationEvent>, last_sequence: i64) -> AgentConversationSnapshot {
    AgentConversationSnapshot {
        connection: AgentConversationConnection {
            owned_id: "large-history".into(), provider: AgentConversationProvider::Codex, generation: 1,
            native_session_id: None, state: super::super::super::protocol::ConversationConnectionState::Disconnected,
            config: Default::default(),
        },
        suspended: true, last_sequence, events, has_earlier_transcript: false,
        pending_events: Vec::new(), active_turn_id: None,
    }
}

async fn send(socket: &mut ServerSocket, frame: ServerFrame) {
    socket.send(TungsteniteMessage::Text(encode_server_frame(&frame).unwrap().into())).await.unwrap();
}

async fn receive(socket: &mut ServerSocket) -> ClientFrame {
    let message = tokio::time::timeout(Duration::from_secs(5), socket.next()).await
        .expect("client frame").unwrap().unwrap();
    serde_json::from_str(message.to_text().unwrap()).unwrap()
}

/// Accepts one client, announces this build's protocol, and reads its Resume and readiness request.
async fn accept_handshake(listener: &tokio::net::TcpListener) -> (ServerSocket, BTreeMap<String, i64>) {
    let (stream, _) = listener.accept().await.unwrap();
    let mut socket = handshake::test_backend(stream, PROTOCOL_VERSION).await;
    let ClientFrame::Resume { cursors } = receive(&mut socket).await else { panic!("expected Resume first"); };
    assert!(matches!(receive(&mut socket).await,
        ClientFrame::Request { id: READINESS_REQUEST_ID, command: RemoteCommand::ListSessions }));
    (socket, cursors)
}

fn readiness(sessions: Vec<AgentConversationSessionRecord>) -> ServerFrame {
    ServerFrame::Response { id: READINESS_REQUEST_ID, response: RemoteResponse::Sessions(sessions) }
}

/// A real backend socket on a loopback port, as the remote machine serves it.
async fn start_backend(token: &str) -> (std::net::SocketAddr, tokio::task::JoinHandle<std::io::Result<()>>, PathBuf) {
    let directory = std::env::temp_dir().join(format!("assembly-pin-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir(&directory).unwrap();
    let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap();
    let state = server_state(manager, directory.clone(), Arc::from(token)).unwrap();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let server = tokio::spawn(axum::serve(listener,
        Router::new().route("/assembly", get(upgrade_remote_socket)).with_state(state)).into_future());
    (address, server, directory)
}

#[test]
fn a_machine_cannot_take_the_desktops_name_or_an_unsafe_id() {
    // Pins a443b8042: messages call this desktop "mac", so a machine with that id would take
    // its messages. Ids also become keys and paths, so only letters, digits, '_' and '-' pass.
    assert!(validate_profile(&machine("workbox-test_2", "Workbox Test", "workbox-test")).is_ok());
    assert!(validate_profile(&machine(LOCAL_MACHINE, "Mac", "box")).unwrap_err().contains("reserved"));
    for id in ["", " ", "box one", "../box", "box;true", "box/x"] {
        assert!(validate_profile(&machine(id, "Box", "box")).is_err(), "id {id:?} must be refused");
    }
    assert!(validate_profile(&machine("box", "  ", "box")).is_err(), "a blank name must be refused");
    assert!(validate_profile(&machine("box", "Box", "-oProxyCommand=true")).is_err(), "an SSH option is not a target");
}

#[tokio::test]
async fn the_backend_refuses_a_socket_without_its_token_and_greets_with_its_protocol() {
    // Pins the backend socket's token check (588413980, now the challenge proof of 9173f3552)
    // and its first frame, the protocol version the desktop compares against (b72f1946d).
    let (address, server, directory) = start_backend("test-token").await;
    let error = tokio_tungstenite::connect_async(format!("ws://{address}/assembly")).await.expect_err("no challenge");
    assert!(matches!(&error, SocketError::Http(response) if response.status() == 426), "{error}");
    let (request, _) = handshake::upgrade_request(&format!("ws://{address}/assembly")).unwrap();
    let (mut socket, _) = tokio_tungstenite::connect_async(request).await.unwrap();
    let first = parse_server_frame(socket.next().await.unwrap().unwrap()).unwrap();
    assert!(matches!(first, Some(ServerFrame::Ready { protocol_version: PROTOCOL_VERSION, .. })));
    send_client_frame(&mut socket, &ClientFrame::Authenticate { proof: "forged".into() }).await.unwrap();
    let answer = tokio::time::timeout(Duration::from_secs(5), socket.next()).await.expect("the backend must hang up");
    assert!(!matches!(answer, Some(Ok(TungsteniteMessage::Text(_)))), "a forged proof must be refused: {answer:?}");
    drop(handshake::test_client(&format!("ws://{address}/assembly")).await);
    server.abort();
    let _ = server.await;
    std::fs::remove_dir_all(directory).unwrap();
}

#[tokio::test]
async fn connecting_with_a_stale_token_fails_at_once_with_the_authentication_message() {
    // Pins 00215d7a7: Connect answers a rejected token straight away instead of retrying
    // until the 30 second connection timeout. Since 9173f3552 the token never crosses the
    // tunnel, so the refusal is the desktop rejecting the backend's proof, not an HTTP 401.
    let (address, server, directory) = start_backend("current-token").await;
    let (_requests, mut receiver) = mpsc::channel(1);
    let (reply, answer) = oneshot::channel();
    let ready = Arc::new(AtomicBool::new(false));
    tokio::time::timeout(Duration::from_secs(5), client_loop(format!("ws://{address}/assembly"), "old-token".into(),
        &mut receiver, Arc::new(|_| Ok(())), None, Some(4), ready.clone(), &mut Some(reply), Arc::new(|| {}), None,
        (quiet_manager(), "box".into()))).await.expect("a refused first connection must not retry");
    let error = answer.await.unwrap().unwrap_err();
    assert_eq!(error, "Backend did not prove it knows the token");
    assert!(!ready.load(Ordering::Acquire));
    server.abort();
    let _ = server.await;
    std::fs::remove_dir_all(directory).unwrap();
}

#[tokio::test]
async fn a_backend_on_another_protocol_fails_queued_requests_with_the_update_message() {
    // Pins b72f1946d: when the backend reports another protocol version,
    // a request waiting for the socket hears which side needs an update instead of hanging.
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let server = tokio::spawn(async move {
        let (stream, _) = listener.accept().await.unwrap();
        let mut socket = handshake::test_backend(stream, PROTOCOL_VERSION - 1).await;
        let _ = socket.next().await;
    });
    let (requests, mut receiver) = mpsc::channel(1);
    let (reply, answer) = oneshot::channel();
    requests.send(ClientRequest::Execute { id: 1, command: RemoteCommand::Config { owned_id: "queued".into() },
        reply, progress: None }).await.unwrap();
    tokio::time::timeout(Duration::from_secs(5), client_loop(format!("ws://{address}"), "test-token".into(),
        &mut receiver, Arc::new(|_| Ok(())), None, Some(1), Arc::new(AtomicBool::new(false)), &mut None,
        Arc::new(|| {}), None, (quiet_manager(), "box".into()))).await.expect("a protocol mismatch ends the client");
    let error = answer.await.unwrap().unwrap_err();
    assert!(error.contains("Remote backend needs an update"), "{error}");
    server.await.unwrap();
}

#[tokio::test]
async fn a_socket_that_drops_after_readiness_reports_the_machine_not_ready_while_it_reconnects() {
    // Pins d0061b736: the rail leaves Connected as soon as the socket drops, while the
    // client is still trying again, not only once the client gives up.
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let server = tokio::spawn(async move {
        let (mut socket, _) = accept_handshake(&listener).await;
        send(&mut socket, readiness(vec![])).await;
        socket.close(None).await.unwrap();
        // The reconnect reaches the port but is never answered, so the client stays retrying.
        listener.accept().await.unwrap().0
    });
    let ready = Arc::new(AtomicBool::new(false));
    let (reports, mut reported) = mpsc::unbounded_channel();
    let observed = ready.clone();
    let status: Arc<dyn Fn() + Send + Sync> = Arc::new(move || { let _ = reports.send(observed.load(Ordering::Acquire)); });
    let (requests, mut receiver) = mpsc::channel(1);
    let actor_ready = ready.clone();
    let actor = tokio::spawn(async move {
        client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver, Arc::new(|_| Ok(())), None, Some(1),
            actor_ready, &mut None, status, None, (quiet_manager(), "box".into())).await;
    });
    assert_eq!(tokio::time::timeout(Duration::from_secs(5), reported.recv()).await.unwrap(), Some(true));
    let parked = server.await.unwrap();
    assert_eq!(reported.try_recv(), Ok(false), "the drop must be reported before the next attempt");
    assert!(!actor.is_finished(), "the client is still reconnecting");
    drop(requests);
    actor.abort();
    let _ = actor.await;
    drop(parked);
}

#[test]
fn a_machine_whose_client_ended_is_not_reported_connected_after_a_reload() {
    // Pins 00215d7a7: a reloaded window reads readyProfileIds to paint each machine. A client
    // whose actor is gone is not ready, even if its ready flag was never cleared.
    let manager = quiet_manager();
    manager.register_profile(machine("box", "Box", "box")).unwrap();
    let (sender, receiver) = mpsc::channel(1);
    {
        let mut state = manager.client.lock().unwrap();
        let client = state.clients.get_mut("box").unwrap();
        client.requests = Some(sender);
        client.ready.store(true, Ordering::Release);
    }
    assert_eq!(manager.environment().ready_profile_ids, vec!["box"]);
    assert_eq!(manager.connection_state("box"), RemoteConnectionState::Connected);
    drop(receiver);
    assert!(manager.environment().ready_profile_ids.is_empty());
    assert_eq!(manager.connection_state("box"), RemoteConnectionState::Disconnected);
    assert_eq!(manager.environment().profiles, vec![machine("box", "Box", "box")], "the saved machine stays");
}

#[test]
fn events_already_applied_are_not_delivered_again() {
    // Pins ab538d9bf: a replay or reconnect resends rows at or below the applied cursor;
    // each row reaches the chat once, and a failed delivery leaves the cursor unmoved.
    let cursors = Mutex::new(BTreeMap::from([("large-history".to_string(), 5)]));
    let delivered = Arc::new(Mutex::new(Vec::new()));
    let captured = delivered.clone();
    let sink: RemoteEventSink = Arc::new(move |events| {
        captured.lock().unwrap().extend(events.into_iter().map(|event| (event.owned_id, event.sequence)));
        Ok(())
    });
    let batch = vec![event_for("large-history", 4), event_for("large-history", 5), event_for("large-history", 6),
        event_for("large-history", 6), event_for("other", 1), event_for("large-history", 7)];
    apply_event_batch(batch, &sink, &cursors).unwrap();
    assert_eq!(*delivered.lock().unwrap(), vec![("large-history".to_string(), 6), ("other".to_string(), 1),
        ("large-history".to_string(), 7)]);
    assert_eq!(*cursors.lock().unwrap(), BTreeMap::from([("large-history".to_string(), 7), ("other".to_string(), 1)]));
    let failing: RemoteEventSink = Arc::new(|_| Err("history cache closed".into()));
    assert!(apply_event_batch(vec![event_for("large-history", 8)], &failing, &cursors).is_err());
    assert_eq!(cursors.lock().unwrap()["large-history"], 7);
}

#[tokio::test]
async fn a_quiet_replay_of_one_session_does_not_hold_back_live_events_of_another() {
    // Pins e6ae503bd: between ReplayStart and ReplayComplete a session's events are saved
    // without being shown; other sessions on the same socket keep publishing live.
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let (live_seen, live_reached) = oneshot::channel::<()>();
    let server = tokio::spawn(async move {
        let (mut socket, _) = accept_handshake(&listener).await;
        send(&mut socket, readiness(vec![])).await;
        send(&mut socket, ServerFrame::ReplayStart { owned_id: "large-history".into() }).await;
        send(&mut socket, ServerFrame::Event { event: event_for("large-history", 1) }).await;
        send(&mut socket, ServerFrame::Event { event: event_for("other", 1) }).await;
        live_reached.await.unwrap();
        send(&mut socket, ServerFrame::ReplayComplete { owned_id: "large-history".into(), head: 1,
            snapshot: snapshot(vec![], 1) }).await;
        let _ = socket.next().await;
    });
    let (published, mut shown) = mpsc::unbounded_channel();
    let manager = manager_publishing_to(published);
    let sink = manager.history_event_sink("box", true);
    let (requests, mut receiver) = mpsc::channel(1);
    let actor = tokio::spawn(async move {
        client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver, sink, None, Some(1),
            Arc::new(AtomicBool::new(false)), &mut None, Arc::new(|| {}), None, (manager, "box".into())).await;
    });
    let first = tokio::time::timeout(Duration::from_secs(5), shown.recv()).await.unwrap().unwrap();
    assert_eq!((first.owned_id.as_str(), first.sequence), ("other", 1));
    assert!(shown.try_recv().is_err(), "the replaying session shows nothing before completion");
    live_seen.send(()).unwrap();
    let newest = tokio::time::timeout(Duration::from_secs(5), shown.recv()).await.unwrap().unwrap();
    assert_eq!((newest.owned_id.as_str(), newest.sequence), ("large-history", 1));
    drop(requests);
    actor.abort();
    let _ = actor.await;
    server.abort();
    let _ = server.await;
}

#[tokio::test]
async fn readiness_replaces_the_machines_rows_and_restarts_unfinished_downloads_below_the_saved_rows() {
    // Pins 34cad92d3 and dd2ad373a: on every (re)connect the session list answer replaces that
    // machine's cached rows, and a history download that did not finish starts again below
    // the oldest row already saved, resuming after the newest one.
    let manager = quiet_manager();
    manager.replace_cached_profile_sessions("box", &[session("gone", "box", 1)]).unwrap();
    manager.replace_cached_profile_sessions("other-box", &[session("elsewhere", "other-box", 2)]).unwrap();
    {
        let history = manager.history.lock().unwrap();
        history.snapshot("box", 0, None, &mut snapshot((5..=7).map(history_event).collect(), 7)).unwrap();
        history.prepare_download("box", "large-history").unwrap();
    }
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let server = tokio::spawn(async move {
        let (mut socket, cursors) = accept_handshake(&listener).await;
        assert_eq!(cursors.get("large-history"), Some(&7));
        send(&mut socket, readiness(vec![session("listed", "box", 9)])).await;
        let ClientFrame::Request { command: RemoteCommand::DownloadHistory { owned_id, before_sequence }, .. } =
            receive(&mut socket).await else { panic!("expected the unfinished download to restart"); };
        assert_eq!((owned_id.as_str(), before_sequence), ("large-history", Some(5)));
        socket
    });
    let (requests, mut receiver) = mpsc::channel(1);
    let (reply, answer) = oneshot::channel();
    let client = manager.clone();
    let actor = tokio::spawn(async move {
        client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver, Arc::new(|_| Ok(())), None, Some(1),
            Arc::new(AtomicBool::new(false)), &mut Some(reply), Arc::new(|| {}), None, (client, "box".into())).await;
    });
    let listed = answer.await.unwrap().unwrap();
    assert_eq!(listed.iter().map(|row| row.owned_id.as_str()).collect::<Vec<_>>(), vec!["listed"]);
    let socket = server.await.unwrap();
    let mut cached: Vec<_> = manager.cached_sessions().into_iter().map(|row| (row.owned_id, row.remote_profile_id)).collect();
    cached.sort();
    assert_eq!(cached, vec![("elsewhere".into(), Some("other-box".into())), ("listed".into(), Some("box".into()))]);
    assert!(manager.owns("listed") && !manager.owns("gone") && manager.owns("elsewhere"));
    drop(requests);
    actor.abort();
    let _ = actor.await;
    drop(socket);
}

#[tokio::test]
async fn a_cancelled_request_answers_at_once_and_tells_the_machine_to_stop() {
    // Pins 5c648f246: cancelling a remote request answers the caller right away and sends
    // Cancel so the machine stops the work; a late answer for it is dropped and the socket
    // keeps serving the next request.
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let server = tokio::spawn(async move {
        let (mut socket, _) = accept_handshake(&listener).await;
        send(&mut socket, readiness(vec![])).await;
        assert!(matches!(receive(&mut socket).await, ClientFrame::Request { id: 7, command: RemoteCommand::Config { .. } }));
        assert!(matches!(receive(&mut socket).await, ClientFrame::Cancel { id: 7 }));
        send(&mut socket, ServerFrame::Response { id: 7, response: RemoteResponse::Empty }).await;
        assert!(matches!(receive(&mut socket).await, ClientFrame::Request { id: 8, .. }));
        send(&mut socket, ServerFrame::Response { id: 8, response: RemoteResponse::Bool(true) }).await;
        let _ = socket.next().await;
    });
    let (requests, mut receiver) = mpsc::channel(2);
    let (ready_reply, ready_answer) = oneshot::channel();
    let actor = tokio::spawn(async move {
        client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver, Arc::new(|_| Ok(())), None, Some(1),
            Arc::new(AtomicBool::new(false)), &mut Some(ready_reply), Arc::new(|| {}), None, (quiet_manager(), "box".into())).await;
    });
    ready_answer.await.unwrap().unwrap();
    let (reply, answer) = oneshot::channel();
    requests.send(ClientRequest::Execute { id: 7, command: RemoteCommand::Config { owned_id: "slow".into() },
        reply, progress: None }).await.unwrap();
    requests.send(ClientRequest::Cancel { id: 7 }).await.unwrap();
    let error = tokio::time::timeout(Duration::from_secs(5), answer).await.unwrap().unwrap().unwrap_err();
    assert_eq!(error, "Remote Assembly request was canceled");
    let (reply, answer) = oneshot::channel();
    requests.send(ClientRequest::Execute { id: 8, command: RemoteCommand::Config { owned_id: "next".into() },
        reply, progress: None }).await.unwrap();
    let next = tokio::time::timeout(Duration::from_secs(5), answer).await.unwrap().unwrap().unwrap();
    assert!(matches!(next, RemoteResponse::Bool(true)));
    drop(requests);
    actor.abort();
    let _ = actor.await;
    server.abort();
    let _ = server.await;
}
