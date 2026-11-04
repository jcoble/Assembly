use super::task_export::*;

#[test]
fn task_export_roundtrip_preserves_exact_snapshot_and_provenance() {
    let raw = serde_json::json!({
        "source": {"provider":"notion","dataSourceId":"11111111-1111-1111-1111-111111111111","pageId":"33333333-3333-3333-3333-333333333333"},
        "revision":"r1", "task":{"key":"TSK-1234","title":"Full title","body":"first\n  last 🐙","url":"https://www.notion.so/33333333333333333333333333333333"}
    });
    let export: NotionTaskExport = serde_json::from_value(raw.clone()).unwrap();
    assert_eq!(serde_json::to_value(&export).unwrap(), raw);
    let frozen = export.clone();
    let mut changed = export;
    changed.task.body = "different".into();
    assert_eq!(frozen.task.body, "first\n  last 🐙");
    assert_eq!(
        serde_json::to_value(frozen.task)
            .unwrap()
            .as_object()
            .unwrap()
            .len(),
        4
    );
}

#[test]
fn task_export_receipt_keeps_uncertain_outcome() {
    let receipt: NotionEditReceipt = serde_json::from_value(serde_json::json!({"pageId":"p","revision":"r","state":"uncertain","error":"notion_transport"})).unwrap();
    assert_eq!(receipt.state, "uncertain");
}

#[gpui::test]
async fn task_export_client_reports_typed_disconnection_without_a_backend(
    cx: &mut gpui::TestAppContext,
) {
    use super::notion_client::NotionClient;
    let client = NotionClient;
    let settings = cx.update(|cx| client.settings(cx)).await.unwrap_err();
    assert_eq!(settings.code, "disconnected");
    let source = NotionTaskRef {
        data_source_id: "11111111-1111-1111-1111-111111111111".into(),
        page_id: "33333333-3333-3333-3333-333333333333".into(),
    };
    let export = cx
        .update(|cx| client.export_task(source.clone(), cx))
        .await
        .unwrap_err();
    assert_eq!(export.code, "disconnected");
    let edit = cx
        .update(|cx| client.property(source, "Status".into(), "Done".into(), "r1".into(), cx))
        .await
        .unwrap_err();
    assert_eq!(edit.code, "disconnected");
    let list = cx
        .update(|cx| client.list(NotionQuery::default(), cx))
        .await
        .unwrap_err();
    assert_eq!(list.code, "disconnected");
}
