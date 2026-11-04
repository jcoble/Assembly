//! Fixtures follow what `DisplayQueries.cs` sends.
use serde_json::{Value, json};

use super::*;

const MIDNIGHT: i64 = 1000;

fn item(machine: &str, kind: &str, id: &str, mut value: Value) -> EngineItem {
    value["entityKind"] = json!(kind);
    value["machine"] = json!(machine);
    value["id"] = json!(id);
    value["version"] = json!(1);
    EngineItem {
        key: EngineKey {
            machine: machine.into(),
            kind: kind.into(),
            id: id.into(),
        },
        version: 1,
        value,
    }
}

fn need(machine: &str, id: &str, state: &str, created: i64) -> EngineItem {
    item(
        machine,
        "needs_me",
        id,
        json!({"sessionId":"s","kind":"question","state":state,"createdMs":created,"answeredMs":null,"updatedMs":created}),
    )
}

fn session(machine: &str, id: &str, state: &str) -> EngineItem {
    item(
        machine,
        "session",
        id,
        json!({"provider":"claude","folder":"/w","state":state,"activityState":"idle","epoch":1,"createdMs":1,"updatedMs":MIDNIGHT + 5}),
    )
}

fn run(machine: &str, id: &str, state: &str, ended: Option<i64>) -> EngineItem {
    item(
        machine,
        "run",
        id,
        json!({"state":state,"activeStepIds":[],"paused":false,"reference":false,"startedMs":1,"endedMs":ended}),
    )
}

fn data(needs: &[EngineItem], sessions: &[EngineItem], runs: &[EngineItem]) -> BoardData {
    BoardData::of(needs, sessions, runs, &[])
}

fn ids(keys: Vec<EngineKey>) -> Vec<String> {
    keys.into_iter().map(|key| key.id).collect()
}

#[test]
fn only_questions_addressed_to_the_owner_are_listed_oldest_first() {
    let mut to_parent = need("m", "to-parent", "open", 1);
    to_parent.value["addresseeSessionId"] = json!("parent");
    let data = data(
        &[
            need("m", "newer", "open", 20),
            need("m", "older", "open", 10),
            to_parent,
        ],
        &[],
        &[],
    );
    assert_eq!(ids(open_owner_questions(&data)), ["older", "newer"]);
    assert_eq!(badge_count(&data), 2);
}

#[test]
fn answered_today_starts_at_midnight_and_lists_the_newest_first() {
    let answered = |id: &str, ms: i64| {
        let mut row = need("m", id, "answered", 1);
        row.value["answeredMs"] = json!(ms);
        row
    };
    let mut cancelled = need("m", "cancelled", "cancelled", 1);
    cancelled.value["updatedMs"] = json!(MIDNIGHT + 1);
    let data = data(
        &[
            answered("yesterday", MIDNIGHT - 1),
            answered("first", MIDNIGHT + 2),
            answered("last", MIDNIGHT + 9),
            cancelled,
        ],
        &[],
        &[],
    );
    assert_eq!(
        ids(answered_today(&data, MIDNIGHT)),
        ["last", "first", "cancelled"]
    );
}

#[test]
fn the_badge_reads_nothing_a_count_or_99_plus() {
    assert_eq!(badge_label(0), None);
    assert_eq!(badge_label(5).as_deref(), Some("5"));
    assert_eq!(badge_label(99).as_deref(), Some("99"));
    assert_eq!(badge_label(100).as_deref(), Some("99+"));
}

#[test]
fn live_leaves_out_children_idle_sessions_and_ended_ones() {
    let mut child = session("m", "child", "running");
    child.value["parentId"] = json!("top");
    child.value["parentMachine"] = json!("m");
    let mut idle_with_run = session("m", "idle-with-run", "idle");
    idle_with_run.value["runId"] = json!("r");
    let mut idle_with_done_run = session("m", "idle-done-run", "idle");
    idle_with_done_run.value["runId"] = json!("done");
    let data = data(
        &[],
        &[
            session("m", "top", "running"),
            session("m", "starting", "starting"),
            session("m", "idle", "idle"),
            session("m", "gone", "ended"),
            child,
            idle_with_run,
            idle_with_done_run,
        ],
        &[
            run("m", "r", "running", None),
            run("m", "done", "succeeded", Some(MIDNIGHT)),
        ],
    );
    let mut live = ids(live_top_level(&data));
    live.sort();
    assert_eq!(live, ["idle-with-run", "starting", "top"]);
    assert_eq!(ids(done_today(&data, MIDNIGHT)), ["gone"]);
}

#[test]
fn a_failed_closed_run_stays_failed_and_done() {
    let mut closed = run("m", "closed", "failed", Some(MIDNIGHT + 1));
    closed.value["closed"] = json!(true);
    let data = data(
        &[],
        &[],
        &[
            closed,
            run("m", "live", "running", None),
            run("m", "old", "succeeded", Some(MIDNIGHT - 1)),
        ],
    );
    assert_eq!(ids(live_runs(&data)), ["live"]);
    assert_eq!(ids(done_runs_today(&data, MIDNIGHT)), ["closed"]);
}

#[test]
fn the_same_id_on_two_machines_stays_two_rows_and_groups_local_first() {
    let data = data(
        &[
            need("remote", "same", "open", 1),
            need("local", "same", "open", 2),
        ],
        &[],
        &[],
    );
    let keys = open_owner_questions(&data);
    assert_eq!(keys.len(), 2);
    assert_ne!(keys[0], keys[1]);
    let groups = by_machine(keys, Some("local"));
    assert_eq!(
        groups
            .iter()
            .map(|group| group.0.as_str())
            .collect::<Vec<_>>(),
        ["local", "remote"]
    );
}

#[test]
fn a_child_question_shows_on_its_parent_not_on_a_stranger() {
    let mut child = session("m", "child", "running");
    child.value["parentId"] = json!("top");
    child.value["parentMachine"] = json!("m");
    let mut asked = need("m", "from-child", "open", 1);
    asked.value["sessionId"] = json!("child");
    asked.value["addresseeSessionId"] = json!("top");
    asked.value["addresseeMachine"] = json!("m");
    let data = data(
        &[asked],
        &[
            session("m", "top", "running"),
            session("m", "other", "running"),
            child,
        ],
        &[],
    );
    let of = |id: &str| {
        ids(session_questions(
            &data,
            &SessionKey {
                machine: "m".into(),
                id: id.into(),
            },
            MIDNIGHT,
        ))
    };
    assert_eq!(of("top"), ["from-child"]);
    assert_eq!(of("other"), Vec::<String>::new());
    assert!(open_owner_questions(&data).is_empty());
}

#[test]
fn a_pr_belongs_to_its_session_by_run_or_owner_on_the_same_machine() {
    let pr = |machine: &str, id: &str, run: Option<&str>, owner: Option<&str>| {
        item(
            machine,
            "pr",
            id,
            json!({"runId":run,"ownerSessionId":owner,"repo":"o/r","number":1,"state":"open"}),
        )
    };
    let mut top = session("m", "top", "running");
    top.value["runId"] = json!("r");
    let data = BoardData::of(
        &[],
        &[top],
        &[],
        &[
            pr("m", "by-run", Some("r"), None),
            pr("m", "by-owner", None, Some("top")),
            pr("m", "other", Some("x"), None),
            pr("elsewhere", "same-run", Some("r"), None),
        ],
    );
    let session = data
        .session(&SessionKey {
            machine: "m".into(),
            id: "top".into(),
        })
        .unwrap();
    let mut found = data
        .prs_for(session)
        .iter()
        .map(|pr| pr.key.id.clone())
        .collect::<Vec<_>>();
    found.sort();
    assert_eq!(found, ["by-owner", "by-run"]);
}

#[test]
fn counts_split_running_from_waiting_and_count_today_only() {
    let mut paused = run("m", "paused", "running", None);
    paused.value["paused"] = json!(true);
    let data = data(
        &[],
        &[],
        &[
            run("m", "a", "running", None),
            paused,
            run("m", "w", "waiting", None),
            run("m", "d", "failed", Some(MIDNIGHT + 1)),
            run("m", "o", "failed", Some(MIDNIGHT - 1)),
        ],
    );
    assert_eq!(summary_counts(&data, MIDNIGHT), (1, 2, 1));
}
