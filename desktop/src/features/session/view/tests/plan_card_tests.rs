use super::current_step;
use crate::features::session::rows::plan::{PlanItem, PlanStatus};

fn plan(statuses: &[PlanStatus]) -> Vec<PlanItem> {
    statuses
        .iter()
        .map(|status| PlanItem {
            text: "step".into(),
            status: *status,
        })
        .collect()
}

// Pins: g0-session#18
#[test]
fn the_plan_marks_the_step_in_progress_else_the_next_pending_else_the_last() {
    use PlanStatus::*;
    assert_eq!(current_step(&plan(&[Completed, InProgress, Pending])), 2);
    assert_eq!(current_step(&plan(&[Completed, Pending, Pending])), 2);
    assert_eq!(current_step(&plan(&[Completed, Completed])), 2);
    assert_eq!(current_step(&plan(&[])), 0);
}

mod turn_totals {
    use crate::features::session::model::item::{Item, ItemKind};
    use crate::features::session::rows::tool::{ToolPayload, ToolState};
    use crate::features::session::view::plan_card::{FileChanges, turn_file_changes};

    fn item(key: &str, kind: ItemKind) -> Item {
        Item {
            key: key.into(),
            run_id: None,
            kind,
            at: 0,
        }
    }
    fn user(key: &str) -> Item {
        item(
            key,
            ItemKind::User {
                text: "go".into(),
                at: 0,
                from: None,
                from_machine: None,
                sender_label: None,
            },
        )
    }
    fn tool(key: &str, state: ToolState, path: Option<&str>, added: Option<i64>, removed: Option<i64>) -> Item {
        item(
            key,
            ItemKind::Tool {
                item_id: key.into(),
                tool: ToolPayload {
                    name: "edit".into(),
                    state,
                    summary: None,
                    path: path.map(Into::into),
                    added,
                    removed,
                    has_detail: false,
                    kind: None,
                },
            },
        )
    }

    #[test]
    fn only_the_newest_turns_finished_changes_count_and_a_file_counts_once() {
        let items = [
            tool("old", ToolState::Completed, Some("old.rs"), Some(50), Some(50)),
            user("u"),
            tool("a", ToolState::Completed, Some("a.rs"), Some(3), Some(1)),
            tool("b", ToolState::Completed, Some("a.rs"), Some(2), None),
            tool("running", ToolState::Started, Some("c.rs"), Some(9), Some(9)),
            tool("read", ToolState::Completed, Some("d.rs"), None, None),
        ];
        let changes = turn_file_changes(&items);
        assert_eq!(changes, Some(FileChanges { files: 1, added: 5, removed: 1 }));
    }

    #[test]
    fn a_turn_that_changed_nothing_has_no_totals() {
        assert_eq!(turn_file_changes(&[user("u")]), None);
    }
}
