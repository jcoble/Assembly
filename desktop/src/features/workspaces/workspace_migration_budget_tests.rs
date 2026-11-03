#[gpui::test]
async fn the_servers_canonical_batch_size_refusal_preserves_old_metadata_and_never_falls_back_to_single_saves(
    cx: &mut TestAppContext,
) {
    let (_backend, stored, _dock, coordinator, cx) =
        legacy(cx, ReceiptKind::CanonicalBudgetRefused).await;
    let (sent, mut refused) = unbounded();
    let _terminal = cx.update(|_, cx| {
        cx.subscribe(
            &coordinator,
            move |_, event: &super::super::WorkspaceRefused, _| {
                sent.unbounded_send(event.clone()).unwrap();
            },
        )
    });
    cx.update(|window, cx| coordinator.update(cx, |view, cx| {
        view.request(
            WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
                machine: "actual-owner".into(),
                id: "history".into(),
            }),
            Box::new(|_, _, _| panic!("server budget refusal must preserve the current workspace")),
            window,
            cx,
        );
    }));
    assert!(
        refused
            .recv()
            .await
            .unwrap()
            .reason
            .contains("larger than 3 MiB")
    );
    cx.update(|_,cx| {
        let files=CenterPanes::file_views(cx);assert_eq!(files.len(),1);
        assert_eq!(serde_json::to_value(BasePanel::dump(files[0].read(cx),cx).info).unwrap()["panel"]["session"],"old-alias");
    });
    let stored = stored.lock().unwrap();
    assert!(stored.snapshot.is_none());
    assert_eq!(
        stored
            .calls
            .iter()
            .filter(|frame| frame["method"] == "save_many")
            .count(),
        1
    );
    assert!(!stored.calls.iter().any(|frame| frame["method"] == "save"));
}
