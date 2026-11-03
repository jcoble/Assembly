use super::{lanes, EdgeKind};
use crate::features::source_control::git_history::GitCommit;
fn c(sha: &str, parents: &[&str]) -> GitCommit { GitCommit { sha: sha.into(), parents: parents.iter().map(|p| (*p).into()).collect(), ..Default::default() } }
// Pins: g9#15
#[test]
fn a_linear_history_ends_only_at_its_actual_root() {
    let rows = lanes(&[c("c", &["b"]), c("b", &["a"]), c("a", &[])]);
    assert_eq!(rows.iter().map(|row| row.lane).collect::<Vec<_>>(), [0,0,0]);
    assert_eq!(rows[0].edges, [(EdgeKind::Parent,0,0)]);
    assert_eq!(rows[1].edges, [(EdgeKind::Child,0,0),(EdgeKind::Parent,0,0)]);
    assert!(rows[2].root); assert!(!rows[1].root);
    assert_eq!(lanes(&[c("last", &["unloaded"])])[0].edges, [(EdgeKind::Parent,0,0)]);
}
// Pins: g9#15
#[test]
fn merges_join_existing_parent_lanes_and_reuse_freed_columns() {
    let rows = lanes(&[c("m", &["b","c"]),c("b", &["a"]),c("c", &["a"]),c("a", &["z"]),c("tip", &["z"]),c("z", &[])]);
    assert!(rows[0].merge);
    assert_eq!(rows[0].edges, [(EdgeKind::Parent,0,0),(EdgeKind::Parent,0,1)]);
    assert_eq!(rows[2].edges, [(EdgeKind::Passing,0,0),(EdgeKind::Child,1,1),(EdgeKind::Parent,1,0)]);
    assert_eq!(rows[4].lane,1); assert_eq!(rows[5].lane,0);
}
// Pins: g9#15
#[test]
fn octopus_parents_are_distinct_and_blank_repeated_commits_are_ignored() {
    let rows = lanes(&[c("m", &["a","b","c","a",""]),c(" ", &[]),c("m", &[]),c("a", &[]),c("b", &[]),c("c", &[])]);
    assert_eq!(rows.len(),4);
    assert_eq!(rows[0].edges, [(EdgeKind::Parent,0,0),(EdgeKind::Parent,0,1),(EdgeKind::Parent,0,2)]);
    assert!(rows[1..].iter().all(|row| row.root));
}
