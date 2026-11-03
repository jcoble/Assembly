use super::HistoryFilter;
use crate::features::source_control::git_history::GitCommit;
// Pins: g9#16
#[test]
fn filters_apply_together_to_loaded_metadata() {
    let commit = GitCommit {
        sha: "abcdef123456".into(),
        subject: "Keep the editor fast".into(),
        author: "Jesse".into(),
        refs: vec!["HEAD -> main".into(), "origin/main".into()],
        ..Default::default()
    };
    let mut filter = HistoryFilter {
        branch: Some("main".into()),
        author: Some("Jesse".into()),
        text: "EDITOR".into(),
    };
    assert!(filter.matches(&commit));
    filter.text = "abcdef".into();
    assert!(filter.matches(&commit));
    filter.text = "123456".into();
    assert!(!filter.matches(&commit));
    filter.text.clear();
    filter.author = Some("jesse".into());
    assert!(!filter.matches(&commit));
    filter.author = None;
    filter.branch = Some("feature".into());
    assert!(!filter.matches(&commit));
    assert_eq!(filter.summary(2, 24), "2 of 24 loaded commits match");
}
