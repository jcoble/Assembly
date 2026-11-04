use super::{HistoryFilter, author_options, branch_options};
use crate::features::source_control::git_history::GitCommit;

fn commit(sha: &str, parents: &[&str], refs: &[&str], subject: &str, author: &str) -> GitCommit {
    GitCommit {
        sha: sha.into(),
        subject: subject.into(),
        author: author.into(),
        refs: refs.iter().map(|name| (*name).into()).collect(),
        parents: parents.iter().map(|sha| (*sha).into()).collect(),
        ..Default::default()
    }
}
fn history() -> Vec<GitCommit> {
    vec![
        commit("c3", &["c1"], &["feature"], "Other line", "Sam"),
        commit(
            "c2",
            &["c1"],
            &["HEAD -> main", "origin/main", "tag: v1"],
            "Keep the editor fast",
            "Jesse",
        ),
        commit("c1", &["c0"], &[], "First", "Jesse"),
        commit("c0", &[], &[], "Root", "Sam"),
    ]
}
// Pins: g9#16
#[test]
fn filters_apply_together_to_loaded_metadata() {
    let commits = history();
    let shown = |filter: HistoryFilter| filter.visible(&commits);
    let by = |field: &str| HistoryFilter {
        text: field.into(),
        ..Default::default()
    };
    assert_eq!(shown(by("EDITOR")), [1]);
    assert_eq!(shown(by("c2")), [1]);
    assert_eq!(shown(by("sam")), [0, 3]);
    assert_eq!(shown(by("origin/main")), [1]);
    assert_eq!(
        shown(HistoryFilter {
            author: Some("Jesse".into()),
            text: "first".into(),
            ..Default::default()
        }),
        [2]
    );
    assert_eq!(
        shown(HistoryFilter {
            author: Some("jesse".into()),
            ..Default::default()
        }),
        Vec::<usize>::new()
    );
}
// Pins: g9#16
#[test]
fn a_branch_keeps_its_tip_and_what_that_tip_descends_from_among_the_loaded_commits() {
    let commits = history();
    let branch = |name: &str| HistoryFilter {
        branch: Some(name.into()),
        ..Default::default()
    };
    assert_eq!(branch("main").visible(&commits), [1, 2, 3]);
    assert_eq!(branch("origin/main").visible(&commits), [1, 2, 3]);
    assert_eq!(branch("feature").visible(&commits), [0, 2, 3]);
    assert_eq!(branch("gone").visible(&commits), Vec::<usize>::new());
    assert!(branch("main").active() && !branch("main").hides_uncommitted());
    assert!(
        HistoryFilter {
            text: " x ".into(),
            ..Default::default()
        }
        .hides_uncommitted()
    );
    assert!(
        !HistoryFilter {
            text: "  ".into(),
            ..Default::default()
        }
        .active()
    );
}
#[test]
fn the_pickers_offer_branches_without_tags_and_each_author_once() {
    let commits = history();
    assert_eq!(branch_options(&commits), ["feature", "main", "origin/main"]);
    assert_eq!(author_options(&commits), ["Jesse", "Sam"]);
}
