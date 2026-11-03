#[gpui::test]
async fn newer_outgoing_edits_are_saved_before_the_target_commit(cx: &mut TestAppContext) {
    let (seen, first_save) = futures_channel::oneshot::channel();
    let seen = Arc::new(Mutex::new(Some(seen)));
    let saves = Arc::new(Mutex::new(Vec::new()));
    let written = saves.clone();
    let held = Arc::new(Mutex::new(None));
    let pending = held.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        if frame["method"] == "save" {
            let mut saves = written.lock().unwrap();
            assert_eq!(
                frame["args"]["expectedRevision"].as_u64(),
                Some(saves.len() as u64)
            );
            saves.push(frame["args"]["snapshot"].clone());
            if saves.len() == 1 {
                *pending.lock().unwrap() = Some(frame["id"].clone());
                if let Some(seen) = seen.lock().unwrap().take() {
                    let _ = seen.send(());
                }
                return Vec::new();
            }
            return vec![Reply::Frame(
                json!({"t":"result","id":frame["id"],"result":{"revision":saves.len()}}),
            )];
        }
        let mut replies = Vec::new();
        if let Some(id) = pending.lock().unwrap().take() {
            replies.push(Reply::Frame(
                json!({"t":"result","id":id,"result":{"revision":1}}),
            ));
        }
        let saved = written.lock().unwrap();
        let (revision, snapshot) = if frame["args"]["checkoutRoot"] == "/a" {
            (saved.len(), saved.last().cloned())
        } else {
            (0, None)
        };
        replies.push(Reply::Frame(json!({"t":"result","id":frame["id"],"result":{"revision":revision,"snapshot":snapshot}})));
        replies
    });
    cx.executor().allow_parking();
    let live = Rc::new(RefCell::new(json!({"files":[]})));
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start_bulk(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        WorkspacePanes::register(Box::new(CurrentFiles(live.clone())), cx).unwrap();
        WorkspaceCoordinator::init(cx)
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| {
        cx.observe(&coordinator, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    coordinator.update_in(cx, |view, window, cx| {
        view.request(
            WorkspaceTarget::Checkout(key("/a")),
            Box::new(|_, _, _| Ok(())),
            window,
            cx,
        );
    });
    until("A ready", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/a")))
    })
    .await;
    *live.borrow_mut() = json!({"files":[],"activePath":"first edit"});
    let commits = Rc::new(RefCell::new(Vec::new()));
    let committed = commits.clone();
    let captured = saves.clone();
    coordinator.update_in(cx, |view, window, cx| {
        view.request(
            WorkspaceTarget::Checkout(key("/b")),
            Box::new(move |key, _, _| {
                committed.borrow_mut().push(key.clone());
                assert_eq!(
                    captured.lock().unwrap().len(),
                    2,
                    "newer edits must have a second confirmed save before selection"
                );
                Ok(())
            }),
            window,
            cx,
        );
    });
    first_save.await.unwrap();
    *live.borrow_mut() = json!({"files":[],"activePath":"newer edit"});
    // An actual independent read releases the held first receipt; no timer or polling controls the race.
    let inspection = cx.update(|_, cx| {
        SocketClient::try_bulk(cx).unwrap().read(cx).call::<Read>(
            "workspaces",
            "read",
            json!({"machine":"actual-source","sessionId":"s","checkoutRoot":"/a"}),
            None,
        )
    });
    assert!(matches!(inspection.await, Outcome::Ok(_)));
    until(
        "B committed after both saves",
        &mut changes,
        &changed,
        || coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/b"))),
    )
    .await;
    let saves = saves.lock().unwrap();
    assert_eq!(saves.len(), 2);
    assert_eq!(saves[0]["activePath"], "first edit");
    assert_eq!(saves[1]["activePath"], "newer edit");
    assert_eq!(
        *commits.borrow(),
        vec![WorkspaceTarget::Checkout(key("/b"))]
    );
}
