use serde_json::json;

use super::task_export::*;
use super::tasks_state::*;

fn task_ref(source: &str, page: &str) -> NotionTaskRef {
    NotionTaskRef {
        data_source_id: source.into(),
        page_id: page.into(),
    }
}

fn detail_fixture(source: NotionTaskRef, revision: &str) -> TaskDetail {
    TaskDetail {
        source,
        revision: revision.into(),
        properties: json!({}),
        blocks: vec![],
        comments: vec![],
        comments_unavailable: false,
    }
}

fn row(number: i64) -> NotionTaskRow {
    NotionTaskRow {
        source_id: "s".into(),
        page_id: format!("page-{number}"),
        title: format!("Title {number}"),
        key_number: number,
        task_key: format!("TSK-{number}"),
        project_id: "p".into(),
        project_title: "Assembly".into(),
        status: "Doing".into(),
        priority: "High".into(),
        assignee: String::new(),
        due: None,
        url: String::new(),
        fetched_ms: 0,
    }
}

fn page(from: i64, count: i64, more: bool) -> NotionTaskPage {
    NotionTaskPage {
        rows: (from..from + count).map(row).collect(),
        has_more: more,
        statuses: vec!["Doing".into(), "Done".into()],
        priorities: vec!["High".into()],
        projects: vec![NotionProjectOption {
            id: "p".into(),
            title: "Assembly".into(),
        }],
        fetched_ms: 0,
        truncated: false,
        generation: 1,
        revision: 1,
    }
}

fn settings(generation: i64, source: &str, token: bool) -> NotionSettingsView {
    NotionSettingsView {
        data_source_id: source.into(),
        has_token: token,
        generation,
        state: "connected".into(),
        error: None,
    }
}

fn failure(code: &str) -> NotionError {
    NotionError {
        code: code.into(),
        message: code.into(),
    }
}

#[test]
fn source_change_rejects_old_detail() {
    let mut state = TasksState::default();
    let old = state.select(task_ref("source-a", "page-a"));
    state.select(task_ref("source-b", "page-b"));
    state.detail_result(old, detail_fixture(task_ref("source-a", "page-a"), "old"));
    assert!(state.detail().is_none());
}

#[test]
fn an_older_read_of_the_same_task_is_dropped() {
    let mut state = TasksState::default();
    let source = task_ref("s", "a");
    let first = state.select(source.clone());
    let (second, _) = state.reread().unwrap();
    assert!(!state.detail_result(first, detail_fixture(source.clone(), "old")));
    assert!(state.detail_result(second, detail_fixture(source, "new")));
    assert_eq!(state.detail().unwrap().revision, "new");
}

#[test]
fn going_back_drops_the_answer_on_its_way() {
    let mut state = TasksState::default();
    let source = task_ref("s", "a");
    let token = state.select(source.clone());
    state.back();
    assert!(!state.detail_result(token, detail_fixture(source, "late")));
    assert!(state.selected().is_none() && state.detail().is_none());
}

#[test]
fn rows_page_in_twenty_fives() {
    let mut state = TasksState::default();
    let (token, query) = state.first_page();
    assert_eq!((query.offset, query.limit), (0, 25));
    assert_eq!(
        state.page_result(token, Ok(page(1, 25, true))),
        PageOutcome::Replaced
    );
    let (token, query) = state.next_page().unwrap();
    assert_eq!((query.offset, query.limit), (25, 25));
    assert!(state.next_page().is_none(), "a read is already running");
    assert_eq!(
        state.page_result(token, Ok(page(26, 5, false))),
        PageOutcome::Appended { from: 25, count: 5 }
    );
    assert_eq!(state.rows.len(), 30);
    assert!(state.next_page().is_none(), "no more to read");
}

#[test]
fn a_new_filter_drops_the_page_on_its_way() {
    let mut state = TasksState::default();
    let (old, _) = state.first_page();
    state.query.statuses = vec!["Done".into()];
    let (new, _) = state.first_page();
    assert_eq!(
        state.page_result(old, Ok(page(1, 25, true))),
        PageOutcome::Stale
    );
    assert!(state.rows.is_empty());
    assert_eq!(
        state.page_result(new, Ok(page(50, 2, false))),
        PageOutcome::Replaced
    );
    assert_eq!(state.rows[0].key_number, 50);
}

#[test]
fn a_reset_drops_the_next_page_on_its_way() {
    let mut state = TasksState::default();
    let (token, _) = state.first_page();
    state.page_result(token, Ok(page(1, 25, true)));
    let (more, _) = state.next_page().unwrap();
    let (reset, _) = state.first_page();
    assert_eq!(
        state.page_result(more, Ok(page(26, 25, true))),
        PageOutcome::Stale
    );
    assert_eq!(state.rows.len(), 25);
    assert_eq!(
        state.page_result(reset, Ok(page(1, 3, false))),
        PageOutcome::Replaced
    );
    assert_eq!(state.rows.len(), 3);
}

#[test]
fn a_pushed_change_rereads_what_is_loaded() {
    let mut state = TasksState::default();
    let (token, _) = state.first_page();
    state.page_result(token, Ok(page(1, 25, true)));
    let (token, _) = state.next_page().unwrap();
    state.page_result(token, Ok(page(26, 25, true)));
    let (_, query) = state.same_window();
    assert_eq!((query.offset, query.limit), (0, 50));
}

#[test]
fn a_filter_the_snapshot_no_longer_has_stops_filtering() {
    let mut state = TasksState::default();
    state.query.statuses = vec!["Gone".into(), "Done".into()];
    let (token, _) = state.first_page();
    assert_eq!(
        state.page_result(token, Ok(page(1, 1, false))),
        PageOutcome::FiltersDropped
    );
    assert_eq!(state.query.statuses, vec!["Done".to_string()]);
    assert!(state.loading == false);
}

#[test]
fn an_empty_snapshot_leaves_the_saved_view_alone() {
    let mut state = TasksState::default();
    state.query.statuses = vec!["Done".into()];
    let (token, _) = state.first_page();
    let mut empty = page(1, 0, false);
    empty.statuses.clear();
    empty.priorities.clear();
    empty.projects.clear();
    assert_eq!(state.page_result(token, Ok(empty)), PageOutcome::Replaced);
    assert_eq!(state.query.statuses, vec!["Done".to_string()]);
}

#[test]
fn an_expired_token_shows_the_error_and_the_connect_card() {
    let mut state = TasksState::default();
    state.set_settings(settings(1, "s", true));
    assert!(!state.show_setup());
    let (token, _) = state.first_page();
    state.page_result(token, Err(failure("notion_http_401")));
    assert!(state.error.as_ref().unwrap().contains("Connect again"));
    assert!(state.show_setup());
    let (token, _) = state.first_page();
    state.page_result(token, Ok(page(1, 1, false)));
    assert!(!state.show_setup() && state.error.is_none());
}

#[test]
fn no_connection_shows_the_connect_card_and_unknown_shows_nothing() {
    let mut state = TasksState::default();
    assert!(!state.show_setup(), "settings haven't been read yet");
    state.set_settings(settings(1, "", false));
    assert!(state.show_setup());
    assert_eq!(state.source(), None);
}

#[test]
fn a_new_connection_drops_everything_from_the_old_one() {
    let mut state = TasksState::default();
    state.set_settings(settings(1, "s", true));
    let (token, _) = state.first_page();
    state.page_result(token, Ok(page(1, 2, false)));
    let late = state.select(task_ref("s", "page-1"));
    let (read, _) = state.first_page();
    assert!(
        !state.set_settings(settings(1, "s", true)),
        "the same source changes nothing"
    );
    assert!(state.set_settings(settings(2, "other", true)));
    assert!(state.rows.is_empty() && state.selected().is_none());
    assert_eq!(
        state.page_result(read, Ok(page(1, 2, false))),
        PageOutcome::Stale
    );
    assert!(!state.detail_result(late, detail_fixture(task_ref("s", "page-1"), "old")));
}

#[test]
fn a_folder_finds_its_project() {
    let projects = ["Assembly", "Rental Command", "EDI Helper"];
    assert_eq!(match_project("/Users/x/dev/Assembly", &projects), Some(0));
    assert_eq!(
        match_project(
            "/Users/x/dev/work/worktrees/Assembly/tsk-1664-components",
            &projects
        ),
        Some(0)
    );
    assert_eq!(match_project("/w/rental-management", &projects), Some(1));
    assert_eq!(match_project("/w/EDIHelper", &projects), Some(2));
    assert_eq!(match_project("/w/unrelated", &projects), None);
    assert_eq!(match_project("/", &projects), None);
}

#[test]
fn the_list_follows_the_session_until_the_person_picks_another() {
    let mut state = TasksState::default();
    let (token, _) = state.first_page();
    state.page_result(token, Ok(page(1, 1, false)));
    assert!(state.follow_root(Some("/w/Assembly")));
    assert_eq!(state.query.project_id.as_deref(), Some("p"));
    state.query.project_id = None;
    assert!(
        !state.follow_root(Some("/w/Assembly")),
        "the same session leaves the pick alone"
    );
    assert!(!state.follow_root(Some("/w/unrelated")));
    assert!(state.no_session_project());
}
