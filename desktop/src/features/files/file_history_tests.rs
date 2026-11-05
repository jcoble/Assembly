use super::{Body, FileHistoryState, HistoryFile};
use crate::features::source_control::git_history::{GitCommit, GitHistory};

fn page(first: usize, count: usize, next: Option<&str>) -> GitHistory {
    GitHistory {
        commits: (first..first + count)
            .map(|i| GitCommit {
                sha: format!("sha{i}"),
                subject: format!("commit {i}"),
                ..Default::default()
            })
            .collect(),
        next_cursor: next.map(String::from),
    }
}

fn file(path: &str) -> Option<HistoryFile> {
    Some(HistoryFile {
        session: "s".into(),
        path: path.into(),
        source: None,
    })
}

#[test]
fn history_pages_append_unique_commits_and_hidden_history_starts_no_query() {
    let mut state = FileHistoryState::default();
    state.select(file("a"));
    assert!(state.request(false).is_none());
    state.show(true);
    let first = state.request(false).unwrap();
    state.loaded(&first, Ok(page(0, 24, Some("next"))));
    assert!(state.near_end(10).is_none());
    let next = state.near_end(22).unwrap();
    assert_eq!(next.cursor.as_deref(), Some("next"));
    state.loaded(&next, Ok(page(23, 2, None)));
    assert_eq!(state.page.commits.len(), 25);
    assert!(state.near_end(24).is_none());
    state.show(false);
    assert!(state.request(true).is_none());
}

#[test]
fn changing_selected_path_drops_old_history_and_refuses_its_late_page() {
    let mut state = FileHistoryState::default();
    state.select(file("a"));
    state.show(true);
    let old = state.request(false).unwrap();
    state.select(file("b"));
    state.loaded(&old, Ok(page(0, 24, None)));
    assert!(state.page.commits.is_empty());
    let next = state.request(false).unwrap();
    assert_eq!(next.file, file("b").unwrap());
}

#[test]
fn each_state_has_its_line() {
    let mut state = FileHistoryState::default();
    state.show(true);
    assert_eq!(state.body(), Body::Pick);
    state.select(file("a"));
    let first = state.request(false).unwrap();
    assert_eq!(state.body(), Body::Reading);
    state.loaded(&first, Ok(page(0, 0, None)));
    assert_eq!(state.body(), Body::Empty);
    state.refresh();
    let again = state.request(false).unwrap();
    state.loaded(
        &again,
        Err("fatal: not a git repository (or any of the parent directories): .git".into()),
    );
    assert_eq!(state.body(), Body::Unavailable);
    state.refresh();
    let failed = state.request(false).unwrap();
    state.loaded(&failed, Err("disk on fire".into()));
    assert_eq!(state.body(), Body::Failed("disk on fire".into()));
}
