use super::{GitCommit, GitHistory};
// Pins: g9#15
#[test]
fn pages_append_once_without_duplicate_commit_identity() {
    let mut history = GitHistory {
        commits: vec![GitCommit {
            sha: "a".into(),
            ..Default::default()
        }],
        next_cursor: Some("24".into()),
    };
    history.append(GitHistory {
        commits: vec![
            GitCommit {
                sha: "a".into(),
                ..Default::default()
            },
            GitCommit {
                sha: "b".into(),
                ..Default::default()
            },
        ],
        next_cursor: None,
    });
    assert_eq!(
        history
            .commits
            .iter()
            .map(|c| c.sha.as_str())
            .collect::<Vec<_>>(),
        ["a", "b"]
    );
    assert!(history.next_cursor.is_none());
}
