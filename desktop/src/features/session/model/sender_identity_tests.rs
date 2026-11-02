use super::item::ItemKind;
use super::session_view::SessionView;
use super::test_rows::{live, row};
use crate::features::session::rows::row::Row;
use serde_json::json;

#[test]
fn sender_identity_and_captured_label_survive_live_page_and_reopen() {
    let serialized = r#"[{"sessionId":"t","seq":1,"runId":"run","itemId":null,"kind":"user","createdAt":1,"payload":{"text":"body","from":"peer/x","clientId":"c","senderLabel":"Captured title (peer/x)"}}]"#;
    let rows: Vec<Row> = serde_json::from_str(serialized).unwrap();
    let live = live(SessionView::default(), &rows);
    let mut page = SessionView::default();
    page.prepend_page(rows.clone(), false);
    let reopened = super::test_rows::live(
        SessionView::default(),
        &serde_json::from_str::<Vec<Row>>(serialized).unwrap(),
    );
    for view in [&live, &page, &reopened] {
        let ItemKind::User {
            from, sender_label, ..
        } = &view.items[0].kind
        else {
            panic!("user");
        };
        assert_eq!(from.as_deref(), Some("peer/x"));
        assert_eq!(sender_label.as_deref(), Some("Captured title (peer/x)"));
    }
    let old = super::test_rows::live(
        SessionView::default(),
        &[row(
            1,
            "old",
            None,
            "user",
            json!({"text":"old","from":"peer/x","clientId":"c"}),
        )],
    );
    assert!(
        matches!(&old.items[0].kind, ItemKind::User { from: Some(id), sender_label: None, .. } if id == "peer/x")
    );
}
