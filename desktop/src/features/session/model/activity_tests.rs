//! The activity card's steps and footer.

use serde_json::json;

use super::activity::{Step, footer, steps};
use super::item::{Item, ItemKind};

fn item(key: &str, at: i64, kind: ItemKind) -> Item {
    Item {
        key: key.into(),
        run_id: Some("r1".into()),
        kind,
        at,
    }
}

fn tool(key: &str, at: i64, state: &str) -> Item {
    let tool = json!({ "name": "Bash", "state": state, "hasDetail": false });
    item(
        key,
        at,
        ItemKind::Tool {
            item_id: key.into(),
            tool: serde_json::from_value(tool).unwrap(),
        },
    )
}

// Pins: g0-session#12
#[test]
fn each_step_runs_from_the_item_before_it_and_the_running_one_to_now() {
    let thought = item(
        "th",
        1_500,
        ItemKind::Thinking {
            text: "hm".into(),
            done: true,
        },
    );
    let other_run = Item {
        run_id: Some("r0".into()),
        ..tool("old", 9_000, "completed")
    };
    let items = vec![
        tool("t1", 1_200, "completed"),
        thought,
        other_run,
        tool("t2", 4_000, "failed"),
        tool("t3", 4_100, "started"),
    ];
    assert_eq!(
        steps(&items, "r1", 1_000, 6_000),
        [
            Step {
                at: 0,
                took_ms: 200
            },
            Step {
                at: 3,
                took_ms: 2_500
            },
            Step {
                at: 4,
                took_ms: 2_000
            },
        ]
    );
}

// Pins: g0-session#12
#[test]
fn the_footer_counts_the_steps_and_the_turns_time() {
    assert_eq!(footer(0, 3_400), "0:03");
    assert_eq!(footer(1, 41_000), "1 step · 0:41");
    assert_eq!(footer(3, 125_000), "3 steps · 2:05");
    assert_eq!(footer(2, 3_725_000), "2 steps · 1:02:05");
}
