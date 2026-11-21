use super::{chat_row, row_label};
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::rows::tool::{ToolPayload, ToolState};
use gpui_kit::accesskit::Node;
use gpui_kit::{Element, Role};

fn tool(summary: &str) -> Item {
    Item {
        key: "tool:t1".into(),
        run_id: Some("r1".into()),
        kind: ItemKind::Tool {
            item_id: "t1".into(),
            tool: ToolPayload {
                name: "Bash".into(),
                state: ToolState::Started,
                summary: Some(summary.into()),
                path: None,
                added: None,
                removed: None,
                has_detail: false,
                kind: Some("execute".into()),
            },
        },
        at: 0,
    }
}

fn node(element: &impl Element) -> Node {
    let mut node = Node::new(element.a11y_role().expect("the element has a role"));
    element.write_a11y_info(&mut node);
    node
}

#[test]
fn a_chat_row_is_a_list_item_found_by_its_key() {
    let item = tool("python3 -c \"import time; time.sleep(20)\"");
    let row = node(&chat_row(&item.key, row_label(&item)));
    assert_eq!(row.role(), Role::ListItem);
    assert_eq!(row.author_id(), Some("rows.tool.t1"));
    assert!(
        row.label()
            .is_some_and(|label| label.starts_with("tool: python3 -c"))
    );
    let long = row_label(&tool(&"x".repeat(200)));
    assert_eq!(long.chars().count(), "tool: ".len() + 80 + 1);
}
