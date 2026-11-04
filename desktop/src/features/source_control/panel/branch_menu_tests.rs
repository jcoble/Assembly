use super::{BranchPicked, branch_detail, branch_name_problem, choice_for, filter_branches};
use crate::features::source_control::git_branches::GitBranch;
fn branch(name: &str) -> GitBranch {
    GitBranch {
        name: name.into(),
        ..GitBranch::default()
    }
}
// Pins: g9#6
#[test]
fn a_held_branch_moves_to_its_folder_and_the_current_branch_is_not_a_choice() {
    assert_eq!(
        choice_for(&branch("free")),
        Some(BranchPicked::Switch("free".into()))
    );
    assert_eq!(
        choice_for(&GitBranch {
            worktree_folder: Some("/work/held".into()),
            ..branch("held")
        }),
        Some(BranchPicked::MoveToFolder("/work/held".into()))
    );
    assert_eq!(
        choice_for(&GitBranch {
            current: true,
            ..branch("main")
        }),
        None
    );
}
#[test]
fn a_branch_name_git_would_refuse_says_why() {
    let branches = [branch("main")];
    let problem = |name: &str| branch_name_problem(name, &branches);
    assert_eq!(problem("   "), None);
    assert_eq!(problem("feature/ok"), None);
    assert_eq!(problem("-x").as_deref(), Some("A branch name cannot start with a dash."));
    assert_eq!(problem("a b").as_deref(), Some("Branch names cannot contain spaces."));
    assert_eq!(problem("a~b").as_deref(), Some("Branch names cannot contain ~"));
    assert_eq!(problem("a..b").as_deref(), Some("Branch names cannot contain two dots in a row."));
    assert_eq!(problem("a/").as_deref(), Some("Branch names cannot start or end with a slash."));
    assert_eq!(problem("a.lock").as_deref(), Some("Branch names cannot end with .lock"));
    assert_eq!(problem("main").as_deref(), Some("main already exists."));
    assert_eq!(problem(&"x".repeat(201)).as_deref(), Some("That branch name is too long."));
}
#[test]
fn the_filter_is_case_insensitive_and_the_detail_says_what_the_branch_tracks() {
    let branches = [branch("Feature/Git"), branch("main")];
    assert_eq!(filter_branches(&branches, "git").len(), 1);
    assert_eq!(filter_branches(&branches, "").len(), 2);
    assert_eq!(
        branch_detail(&GitBranch {
            upstream: Some("origin/main".into()),
            subject: "Fix it".into(),
            ..branch("main")
        }),
        "tracks origin/main · Fix it"
    );
    assert_eq!(branch_detail(&branch("new")), "no remote branch");
}
