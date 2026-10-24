//! A pushed event that skips past the Mac's saved history fills the hole with one quiet replay.
use super::connection_tests::{begin_test_download, download_manager, history_event, history_snapshot,
    large_history_event, sequences, store};
use super::*;

type Socket = tokio_tungstenite::WebSocketStream<tokio::net::TcpStream>;

async fn send(socket: &mut Socket, frame: ServerFrame) {
    socket.send(TungsteniteMessage::Text(encode_server_frame(&frame).unwrap().into())).await.unwrap();
}

/// The next client frame, or None once the client hangs up.
async fn receive(socket: &mut Socket) -> Option<ClientFrame> {
    match tokio::time::timeout(Duration::from_secs(5), socket.next()).await.expect("the client sent no frame") {
        Some(Ok(TungsteniteMessage::Text(text))) => Some(serde_json::from_str(&text).unwrap()),
        _ => None,
    }
}

async fn next_server_frame<S>(socket: &mut S) -> ServerFrame
where S: StreamExt<Item = Result<TungsteniteMessage, tokio_tungstenite::tungstenite::Error>> + Unpin {
    let message = tokio::time::timeout(Duration::from_secs(5), socket.next()).await.unwrap().unwrap().unwrap();
    parse_server_frame(message).unwrap().unwrap()
}

/// The next frame about a session. `MessagesChanged` is skipped: it comes from a process-wide
/// signal that any test running at the same time may raise, and it names no session.
async fn next_session_frame<S>(socket: &mut S) -> ServerFrame
where S: StreamExt<Item = Result<TungsteniteMessage, tokio_tungstenite::tungstenite::Error>> + Unpin {
    loop {
        let frame = next_server_frame(socket).await;
        if !matches!(frame, ServerFrame::MessagesChanged) { return frame; }
    }
}

fn event(sequence: i64) -> ServerFrame { ServerFrame::Event { event: history_event(sequence) } }

fn replay_complete(head: i64) -> ServerFrame {
    ServerFrame::ReplayComplete { owned_id: "large-history".into(), head, snapshot: history_snapshot(vec![], head) }
}

/// Runs `client_loop` with rows 1..=3 saved against a scripted server, then a sentinel request.
/// Returns the published and saved sequences and every client frame sent after the script.
async fn run<F: std::future::Future<Output = Socket> + Send + 'static>(script: impl FnOnce(Socket) -> F + Send + 'static)
    -> (Vec<i64>, Vec<i64>, Vec<ClientFrame>) {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let published = Arc::new(Mutex::new(Vec::new()));
    let captured = published.clone();
    let manager = RemoteConnectionManager::from_environment(
        Arc::new(move |event| captured.lock().unwrap().push(event)), Arc::new(|_| {}), store()).unwrap();
    manager.history.lock().unwrap().snapshot("box", 0, None,
        &mut history_snapshot((1..=3).map(history_event).collect(), 3)).unwrap();
    let (scripted, script_done) = oneshot::channel();
    let server = tokio::spawn(async move {
        let mut socket = handshake::test_backend(listener.accept().await.unwrap().0, PROTOCOL_VERSION).await;
        assert!(matches!(receive(&mut socket).await, Some(ClientFrame::Resume { cursors })
            if cursors == BTreeMap::from([("large-history".into(), 3)])));
        assert!(matches!(receive(&mut socket).await, Some(ClientFrame::Request { id: READINESS_REQUEST_ID, .. })));
        send(&mut socket, ServerFrame::Response { id: READINESS_REQUEST_ID, response: RemoteResponse::Sessions(vec![]) }).await;
        let mut socket = script(socket).await;
        scripted.send(()).unwrap();
        // Read until the client hangs up, so a late extra Resume is seen too.
        let mut after = Vec::new();
        while let Some(frame) = receive(&mut socket).await {
            if matches!(frame, ClientFrame::Request { id: 900, .. }) {
                send(&mut socket, ServerFrame::Response { id: 900, response: RemoteResponse::Sessions(vec![]) }).await;
            }
            after.push(frame);
        }
        after
    });
    let (requests, mut receiver) = mpsc::channel(2);
    let sink = manager.history_event_sink("box", true);
    let client = manager.clone();
    let actor = tokio::spawn(async move {
        client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver, sink, None, Some(1),
            Arc::new(AtomicBool::new(false)), &mut None, Arc::new(|| {}), None, (client, "box".into())).await;
    });
    script_done.await.unwrap();
    let (reply, answer) = oneshot::channel();
    requests.send(ClientRequest::Execute { id: 900, command: RemoteCommand::ListSessions, reply, progress: None }).await.unwrap();
    assert!(matches!(answer.await.unwrap(), Ok(RemoteResponse::Sessions(_))));
    drop(requests);
    actor.await.unwrap();
    let after = server.await.unwrap();
    let saved = manager.store.list_events(&RemoteHistory::key("box", "large-history"), 0, 100).unwrap()
        .iter().map(|row| row.seq).collect();
    let published = sequences(&published.lock().unwrap());
    (published, saved, after)
}

fn only_the_sentinel(after: &[ClientFrame]) -> bool {
    matches!(after, [ClientFrame::Request { id: 900, .. }])
}

#[test]
fn skipped_reports_only_sessions_past_their_saved_maximum() {
    let history = RemoteHistory::new(store());
    history.snapshot("box", 0, None,
        &mut history_snapshot((1..=3).map(history_event).collect(), 3)).unwrap();
    assert!(history.skipped("box", &[history_event(4)]).unwrap().is_empty());
    assert_eq!(history.skipped("box", &[history_event(6), history_event(7)]).unwrap(),
        BTreeMap::from([("large-history".to_string(), 3)]));
    let unsaved = AgentConversationEvent { owned_id: "other".into(), ..history_event(9) };
    assert!(history.skipped("box", &[unsaved]).unwrap().is_empty());
}

#[test]
fn a_contiguous_first_event_does_not_hide_a_later_skip_in_the_batch() {
    let history = RemoteHistory::new(store());
    history.snapshot("box", 0, None,
        &mut history_snapshot((1..=3).map(history_event).collect(), 3)).unwrap();
    assert_eq!(history.skipped("box", &[history_event(4), history_event(6)]).unwrap(),
        BTreeMap::from([("large-history".to_string(), 3)]));
}

#[tokio::test]
async fn a_skipped_sequence_asks_for_the_skipped_range_once() {
    let (published, saved, after) = run(|mut socket| async move {
        send(&mut socket, ServerFrame::ReplayStart { owned_id: "large-history".into() }).await;
        send(&mut socket, replay_complete(3)).await;
        send(&mut socket, event(6)).await;
        assert!(matches!(receive(&mut socket).await, Some(ClientFrame::Resume { cursors })
            if cursors == BTreeMap::from([("large-history".into(), 3)])), "a skip must ask for a replay");
        // Pushed before the server read the Resume: the replay sends it again in order.
        send(&mut socket, event(7)).await;
        send(&mut socket, ServerFrame::ReplayStart { owned_id: "large-history".into() }).await;
        for sequence in 4..=7 { send(&mut socket, event(sequence)).await; }
        send(&mut socket, replay_complete(7)).await;
        socket
    }).await;
    // The reconnect replay publishes the saved newest (3); the fill publishes only its newest.
    assert_eq!(published, [3, 7]);
    assert_eq!(saved, (1..=7).collect::<Vec<_>>());
    assert!(only_the_sentinel(&after), "no second Resume: {after:?}");
}

#[tokio::test]
async fn contiguous_live_events_ask_for_nothing() {
    let (published, saved, after) = run(|mut socket| async move {
        send(&mut socket, ServerFrame::ReplayStart { owned_id: "large-history".into() }).await;
        send(&mut socket, replay_complete(3)).await;
        send(&mut socket, event(4)).await;
        send(&mut socket, event(5)).await;
        socket
    }).await;
    assert_eq!(published, [3, 4, 5]);
    assert_eq!(saved, (1..=5).collect::<Vec<_>>());
    assert!(only_the_sentinel(&after), "{after:?}");
}

#[tokio::test]
async fn replay_takes_its_events_in_order_and_asks_for_no_gaps() {
    let (published, saved, after) = run(|mut socket| async move {
        // A live event that lands before its session's replay starts.
        send(&mut socket, event(9)).await;
        send(&mut socket, ServerFrame::ReplayStart { owned_id: "large-history".into() }).await;
        // 5, 7 and 8 were pruned on the server.
        for sequence in [4, 6, 9] { send(&mut socket, event(sequence)).await; }
        send(&mut socket, replay_complete(9)).await;
        socket
    }).await;
    assert_eq!(published, [9]);
    assert_eq!(saved, [1, 2, 3, 4, 6, 9]);
    assert!(only_the_sentinel(&after), "{after:?}");
}

#[tokio::test]
async fn a_completed_session_with_a_hole_opens_and_scrolls_without_asking_the_remote() {
    let manager = download_manager();
    let (id, mut requests) = begin_test_download(&manager, 100, Some(1), Some(3)).await;
    manager.receive_history_chunk("cache-test", id, "large-history", 100,
        encode_history_chunk(&[history_event(3), history_event(2), history_event(1)]).unwrap()).unwrap();
    manager.finish_history_download("cache-test", "large-history", id).await.unwrap();
    manager.history_event_sink("cache-test", true)(vec![history_event(5)]).unwrap();
    let open = tokio::time::timeout(Duration::from_secs(5),
        manager.selection_snapshot("large-history".into(), 71, 1024, None)).await.expect("opening must not wait on the remote");
    assert_eq!(open.unwrap().unwrap().page.watermark, 5);
    let newest = manager.item_page("large-history".into(), i64::MAX, 1024, true, 72).await.unwrap();
    assert!(sequences(&newest.events).contains(&5));
    let oldest = manager.item_page("large-history".into(), 0, 1024, false, 73).await.unwrap();
    assert!(sequences(&oldest.events).contains(&1));
    let events = manager.events_before("large-history".into(), 6, 1024).await.unwrap();
    assert_eq!(sequences(&events.events), [1, 2, 3, 5]);
    assert!(requests.try_recv().is_err());
}

#[tokio::test]
async fn resume_replays_only_the_sessions_it_names() {
    let directory = std::env::temp_dir().join(format!("assembly-named-resume-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir(&directory).unwrap();
    let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap();
    let store = manager.store();
    for owned_id in ["large-history", "other-history"] {
        store.upsert_session(&mcb_core::session_store::SessionRow {
            owned_id: owned_id.into(), native_session_id: None, provider: "codex".into(), model: None, effort: None,
            cwd: String::new(), worktree: None, branch: None, title: None, title_source: None, project: None,
            project_id: None, state: "suspended".into(), suspended: true, created_at_ms: 0, last_activity_at_ms: 0,
            extra_json: super::super::manager::imported_session_extra(AgentConversationProvider::Codex).unwrap(),
        }).unwrap();
        for sequence in 1..=2 {
            let event = AgentConversationEvent { owned_id: owned_id.into(), sequence, ..large_history_event(32) };
            store.append_event(&mcb_core::session_store::EventRow { owned_id: owned_id.into(), seq: sequence,
                turn_id: None, kind: "test".into(), payload_json: serde_json::to_string(&event).unwrap(), created_at_ms: 0 }).unwrap();
        }
    }
    let state = ServerState {
        workflow: crate::workflow::WorkflowEngine::managed(manager.clone(), manager.store_handle()),
        manager, data_dir: directory.clone(), token: Arc::from("test-token"),
        maintenance: Arc::new(tokio::sync::RwLock::new(())), restarting: Arc::new(AtomicBool::new(false)),
        restart_requested: Arc::new(tokio::sync::Notify::new()), restart_flushed: Arc::new(tokio::sync::Notify::new()),
        events: broadcast::channel(16).0, terminals: RemoteTerminals::new(), relay: Arc::default(), pairing: Arc::default(),
    };
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let server = tokio::spawn(axum::serve(listener, Router::new().route("/assembly", get(upgrade_remote_socket)).with_state(state.clone())).into_future());
    let mut socket = handshake::test_client(&format!("ws://{address}/assembly")).await;
    let both = BTreeMap::from([("large-history".into(), 0), ("other-history".into(), 0)]);
    send_client_frame(&mut socket, &ClientFrame::Resume { cursors: both }).await.unwrap();
    let mut completed = 0;
    while completed < 2 { if matches!(next_session_frame(&mut socket).await, ServerFrame::ReplayComplete { .. }) { completed += 1; } }
    send_client_frame(&mut socket, &ClientFrame::Resume { cursors: BTreeMap::from([("large-history".into(), 1)]) }).await.unwrap();
    assert!(matches!(next_session_frame(&mut socket).await, ServerFrame::ReplayStart { owned_id } if owned_id == "large-history"));
    assert!(matches!(next_session_frame(&mut socket).await, ServerFrame::Event { event } if event.owned_id == "large-history" && event.sequence == 2));
    assert!(matches!(next_session_frame(&mut socket).await, ServerFrame::ReplayComplete { owned_id, .. } if owned_id == "large-history"));
    send_client_frame(&mut socket, &ClientFrame::Request { id: 7, command: RemoteCommand::ListSessions }).await.unwrap();
    let frame = next_session_frame(&mut socket).await;
    assert!(matches!(frame, ServerFrame::Response { id: 7, .. }), "an unnamed session replayed: {frame:?}");
    server.abort();
    let _ = server.await;
    drop(state);
    std::fs::remove_dir_all(directory).unwrap();
}
