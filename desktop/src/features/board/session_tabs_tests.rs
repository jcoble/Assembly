//! A session's row and what it opens into.
use serde_json::{Value, json};

use super::{default_tab, row_chip, second_line, task_label};
use crate::activity::Tone;
use crate::core::engine::engine_items::SessionItem;
use crate::core::engine::workflow_items::{RunItem, StepItem};
use crate::core::engine::{EngineItem, EngineKey};
use crate::features::board::board_prefs::SessionTab;

fn session(extra: Value) -> SessionItem {
    let mut value = json!({"entityKind":"session","machine":"m","id":"s","version":1,"provider":"claude","folder":"/work/tsk-1615-mute","state":"idle","activityState":"idle","epoch":1});
    value.as_object_mut().unwrap().extend(extra.as_object().unwrap().clone());
    SessionItem::of(&EngineItem { key: EngineKey { machine: "m".into(), kind: "session".into(), id: "s".into() }, version: 1, value }).unwrap()
}

fn run(branch: Option<&str>) -> RunItem {
    RunItem::decode(json!({"entityKind":"run","id":"r","machine":"m","version":1,"state":"running","paused":false,"reference":false,"activeStepIds":[],"branch":branch})).unwrap()
}

fn step(id: &str, state: &str) -> StepItem {
    StepItem::decode(json!({"entityKind":"step","id":id,"machine":"m","version":1,"runId":"r","name":id,"kind":"agent","state":state,"attempt":1,"maxAttempts":1,"required":1,"ruleSkippable":0,
        "labelPresent":"Working","labelPast":"Worked","badges":[],"scopeGranted":false,"closed":false,"needsMeIds":[],"dependencies":[]})).unwrap()
}

#[test]
fn default_tab_rules() {
    assert_eq!(default_tab(2, true), SessionTab::Questions);
    assert_eq!(default_tab(0, true), SessionTab::Workflow);
    assert_eq!(default_tab(0, false), SessionTab::SubAgents);
}

#[test]
fn chip_prefers_question_count() {
    let running = session(json!({"state":"running"}));
    assert_eq!(row_chip(&running, 1), ("1 question".to_owned(), Tone::Warning));
    assert_eq!(row_chip(&running, 2).0, "2 questions");
    assert_eq!(row_chip(&running, 0), ("Working".to_owned(), Tone::Info));
    assert_eq!(row_chip(&session(json!({"state":"lost"})), 0), ("Lost".to_owned(), Tone::Danger));
    assert_eq!(row_chip(&session(json!({"runId":"r"})), 0).0, "Waiting");
    assert_eq!(row_chip(&session(json!({})), 0).0, "Idle");
}

#[test]
fn second_line_with_and_without_run() {
    let (done, working, later) = (step("a", "succeeded"), step("b", "running"), step("c", "pending"));
    let steps = [&done, &working, &later];
    assert_eq!(second_line(&session(json!({})), Some(&run(Some("tsk-1615-mute"))), &steps, "Workbox Test"), "Step 2 of 3 · Workbox Test · tsk-1615-mute");
    assert_eq!(second_line(&session(json!({"branch":"own"})), Some(&run(None)), &steps, "This Mac"), "Step 2 of 3 · This Mac · own");
    assert_eq!(second_line(&session(json!({"model":"opus"})), None, &[], "This Mac"), "claude · opus · tsk-1615-mute");
    assert_eq!(second_line(&session(json!({})), None, &[], "This Mac"), "claude · tsk-1615-mute");
}

#[test]
fn a_task_link_reads_as_its_key_only_when_it_names_one() {
    assert_eq!(task_label("https://x/TSK-1615-mute-thing").as_ref(), "TSK-1615-mute-thing");
    assert_eq!(task_label("https://app.notion.com/p/40b394").as_ref(), "Task");
}

#[test]
fn child_question_shows_for_parent_label() {
    use crate::features::board::question_row::asked_by;
    assert_eq!(asked_by("Worker", "This Mac", Some("Main orchestrator")), "Worker · This Mac · for Main orchestrator");
}
