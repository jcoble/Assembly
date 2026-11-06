use super::FileHistoryState;
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

#[test]
fn history_pages_append_unique_commits_and_hidden_history_starts_no_query() {
    let mut state = FileHistoryState::new("s".into(), "a".into());
    assert!(state.request(false).is_none());
    state.shown = true;
    let first = state.request(false).unwrap();
    state.loaded(&first, Ok(page(0, 24, Some("next"))));
    let next = state.request(true).unwrap();
    assert_eq!(next.cursor.as_deref(), Some("next"));
    state.loaded(&next, Ok(page(23, 2, None)));
    assert_eq!(state.page.commits.len(), 25);
    state.hide();
    assert!(state.request(true).is_none());
}

#[test]
fn changing_selected_path_drops_old_history_and_refuses_its_late_page() {
    let mut state = FileHistoryState::new("s".into(), "a".into());
    state.shown = true;
    let old = state.request(false).unwrap();
    state.select("s".into(), "b".into());
    state.loaded(&old, Ok(page(0, 24, None)));
    assert!(state.page.commits.is_empty());
    let next = state.request(false).unwrap();
    assert_eq!(
        (&next.session, &next.path),
        (&"s".to_owned(), &"b".to_owned())
    );
}
