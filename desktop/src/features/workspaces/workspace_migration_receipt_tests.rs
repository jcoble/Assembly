//! Workspace migration tests for batch receipts: matching every identity, revision and position, the center order, and
//! reconciling an unknown receipt by the exact saved bytes.
use super::*;

#[test]
fn batch_receipts_must_match_every_actual_identity_revision_and_input_position() {
    let key = |session: &str| WorkspaceKey {
        machine: "actual-owner".into(),
        session_id: session.into(),
        checkout_root: "/checkout".into(),
    };
    let groups = vec![
        legacy_migration::Group {
            key: key("a"),
            snapshot: json!({"version":1}),
        },
        legacy_migration::Group {
            key: key("b"),
            snapshot: json!({"version":1}),
        },
    ];
    let receipt = |session: &str, revision| SavedEntry {
        machine: "actual-owner".into(),
        session_id: session.into(),
        checkout_root: "/checkout".into(),
        revision,
    };
    assert!(confirmed(
        &SavedMany {
            saved: vec![receipt("a", 6), receipt("b", 1)]
        },
        &groups,
        &[5, 0]
    ));
    assert!(!confirmed(
        &SavedMany {
            saved: vec![receipt("b", 1), receipt("a", 6)]
        },
        &groups,
        &[5, 0]
    ));
    assert!(!confirmed(
        &SavedMany {
            saved: vec![receipt("a", 5), receipt("b", 1)]
        },
        &groups,
        &[5, 0]
    ));
    assert!(!confirmed(
        &SavedMany {
            saved: vec![receipt("a", 6)]
        },
        &groups,
        &[5, 0]
    ));
    let mut foreign = receipt("a", 6);
    foreign.machine = "other-owner".into();
    assert!(!confirmed(
        &SavedMany {
            saved: vec![foreign, receipt("b", 1)]
        },
        &groups,
        &[5, 0]
    ));
}

#[gpui::test]
async fn center_order_uses_confirmed_document_identity_and_the_sole_files_snapshot(
    cx: &mut TestAppContext,
) {
    let (_backend, _stored, _dock, _coordinator, cx) = legacy(cx, ReceiptKind::Confirmed).await;
    cx.update(|window, cx| {
        let files = CenterPanes::file_views(cx);
        assert_eq!(files.len(), 1);
        let owner = files[0].read(cx).legacy_capture(cx).unwrap().owner.unwrap();
        WorkspacePanes::set_key(owner.clone(), cx);
        let snapshot = WorkspacePanes::capture(window, cx).unwrap();
        let tabs =
            crate::features::workspaces::center_tabs_capture::capture(&owner, window, cx).unwrap();
        let document = format!(
            "document:{}",
            serde_json::to_string(&("actual-owner", "/real-checkout", "src/λ.rs")).unwrap()
        );
        assert_eq!(tabs.order, vec![document.clone()]);
        assert_eq!(tabs.active_key, Some(document));
        assert!(
            tabs.panes.is_empty(),
            "file content has exactly one codec location"
        );
        let value = serde_json::to_value(tabs).unwrap();
        assert!(
            crate::features::workspaces::center_tabs::parse(&value, &owner, &snapshot)
                .unwrap()
                .is_some()
        );
        assert_eq!(snapshot["files"][0]["path"], "src/λ.rs");
    });
}

#[gpui::test]
async fn an_unknown_batch_receipt_is_reconciled_by_exact_saved_bytes_without_replaying_the_write(
    cx: &mut TestAppContext,
) {
    let (_backend, stored, _dock, coordinator, cx) =
        legacy(cx, ReceiptKind::CommittedMissing).await;
    let (sent, mut activated) = unbounded();
    let _terminal = cx.update(|_, cx| {
        cx.subscribe(
            &coordinator,
            move |_, event: &super::super::super::WorkspaceActivated, _| {
                sent.unbounded_send(event.clone()).unwrap();
            },
        )
    });
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
                    machine: "actual-owner".into(),
                    id: "history".into(),
                }),
                Box::new(|_, _, cx| {
                    assert_eq!(
                        CenterPanes::file_views(cx).len(),
                        1,
                        "old metadata remains until reconciliation"
                    );
                    Ok(())
                }),
                window,
                cx,
            );
        })
    });
    activated.recv().await.unwrap();
    cx.update(|_, cx| assert!(CenterPanes::file_views(cx).is_empty()));
    let stored = stored.lock().unwrap();
    assert_eq!(
        stored
            .calls
            .iter()
            .filter(|frame| frame["feature"] == "workspaces" && frame["method"] == "save_many")
            .count(),
        1
    );
    assert_eq!(
        stored
            .calls
            .iter()
            .filter(|frame| frame["feature"] == "workspaces" && frame["method"] == "read")
            .count(),
        2
    );
    assert_eq!(
        stored.snapshot.as_ref().unwrap()["files"][0]["path"],
        "src/λ.rs"
    );
}
