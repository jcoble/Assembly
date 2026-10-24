//! Source Control behaviour that works today and must keep working (TSK-1167).
//! Each test runs real `git` in a throwaway repository under the system temp
//! folder and goes through the same functions the Tauri commands call.

use std::path::{Path, PathBuf};
use std::process::Command;

use tauri::async_runtime::block_on;

use crate::git::pull_requests::{format_gh_spawn_error, format_process_failure, summarize_checks};
use crate::git::workspace::{
    amend_git_commit, create_git_branch, discard_git_paths, list_git_branches, pop_git_stash,
    stage_git_paths_sync, stash_git_changes, switch_git_branch,
};
use crate::{project_git_status_sync, read_git_commit_history_page_sync, run_git_text};

struct Repo(PathBuf);

impl Drop for Repo {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

impl Repo {
    /// An empty repository on `main` with a local identity, so commits work on
    /// any machine regardless of its global git config.
    fn new(name: &str) -> Self {
        let root = std::env::temp_dir().join(format!(
            "assembly-git-regression-{name}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&root).unwrap();
        let repo = Repo(root);
        repo.git(&["init", "-q", "-b", "main"]);
        repo.git(&["config", "user.name", "Regression Test"]);
        repo.git(&["config", "user.email", "test@example.invalid"]);
        repo.git(&["config", "commit.gpgsign", "false"]);
        repo
    }

    fn root(&self) -> String {
        self.0.display().to_string()
    }

    fn write(&self, path: &str, text: &str) {
        let path = self.0.join(path);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, text).unwrap();
    }

    fn read(&self, path: &str) -> Option<String> {
        std::fs::read_to_string(self.0.join(path)).ok()
    }

    fn git(&self, args: &[&str]) -> String {
        let output = Command::new("git").arg("-C").arg(&self.0).args(args).output().unwrap();
        assert!(
            output.status.success(),
            "git {args:?} failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        String::from_utf8_lossy(&output.stdout).trim().to_string()
    }

    fn commit_file(&self, path: &str, text: &str, subject: &str) {
        self.write(path, text);
        self.git(&["add", path]);
        self.git(&["commit", "-q", "-m", subject]);
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

// Fix 2d646c7e0 (Git history reads bounded cursor pages): the graph reads 24
// commits at a time and the cursor brings exactly the next ones, no repeats.
#[test]
fn history_pages_through_a_long_history_with_its_cursor() {
    let repo = Repo::new("history-pages");
    repo.commit_file("a.txt", "0\n", "commit 0");
    for index in 1..26 {
        repo.git(&["commit", "-q", "--allow-empty", "-m", &format!("commit {index}")]);
    }

    let first = read_git_commit_history_page_sync(repo.path().into(), None, None).unwrap();
    assert_eq!(first.commits.len(), 24);
    assert_eq!(first.commits[0].subject, "commit 25");
    assert_eq!(first.next_cursor.as_deref(), Some("24"));
    assert!(!first.complete);

    let second =
        read_git_commit_history_page_sync(repo.path().into(), first.next_cursor.clone(), None)
            .unwrap();
    assert_eq!(
        second.commits.iter().map(|commit| commit.subject.as_str()).collect::<Vec<_>>(),
        vec!["commit 1", "commit 0"],
        "the second page holds only the two commits the first page left out"
    );
    assert_eq!(second.next_cursor, None);
    assert!(second.complete);

    let blank = read_git_commit_history_page_sync(repo.path().into(), Some("  ".into()), None)
        .unwrap();
    assert_eq!(blank.commits[0].subject, "commit 25", "a blank cursor is the first page");
    let refused =
        read_git_commit_history_page_sync(repo.path().into(), Some("abc".into()), None).unwrap_err();
    assert_eq!(refused, "Git history cursor was not recognized");
}

// A new repository (no commits yet) is an ordinary folder to open: its history
// is an empty finished page and its status has a branch but no line counts.
#[test]
fn a_repository_with_no_commits_has_empty_history_and_no_line_counts() {
    let repo = Repo::new("unborn");
    repo.write("new.txt", "hello\n");

    let history = read_git_commit_history_page_sync(repo.path().into(), None, None).unwrap();
    assert!(history.commits.is_empty());
    assert!(history.complete);
    assert_eq!(history.next_cursor, None);

    let status = project_git_status_sync(repo.path().into()).unwrap();
    assert_eq!(status.branch.as_deref(), Some("main"));
    assert_eq!(status.files.len(), 1);
    assert_eq!(status.files[0].badge, "?");
    assert_eq!((status.additions, status.deletions), (None, None));
}

// File history (Files pane, "Show history") keeps the commits from before a
// file was renamed: the read passes --follow (45dcb14bb).
#[test]
fn file_history_follows_a_file_across_a_rename() {
    let repo = Repo::new("follow");
    repo.commit_file("old.txt", "one\ntwo\nthree\nfour\n", "add old");
    repo.git(&["mv", "old.txt", "new.txt"]);
    repo.git(&["commit", "-q", "-m", "rename to new"]);
    repo.commit_file("new.txt", "one\ntwo\nthree\nfour\nfive\n", "grow new");

    let history =
        read_git_commit_history_page_sync(repo.path().into(), None, Some("new.txt".into()))
            .unwrap();
    assert_eq!(
        history.commits.iter().map(|commit| commit.subject.as_str()).collect::<Vec<_>>(),
        vec!["grow new", "rename to new", "add old"]
    );
}

// Amend with an empty box folds the staged change into the last commit and
// keeps its message; with text it replaces the message; with no commit at all
// it says so in a sentence (cef560734).
#[test]
fn amend_folds_staged_changes_into_the_last_commit() {
    let repo = Repo::new("amend");
    repo.commit_file("a.txt", "one\n", "first");
    repo.commit_file("a.txt", "two\n", "second");
    repo.write("a.txt", "three\n");
    repo.git(&["add", "a.txt"]);

    let kept = block_on(amend_git_commit(repo.root(), None)).unwrap();
    assert_eq!(kept.message, "Amended the last commit");
    assert!(kept.status.files.is_empty(), "the staged change is now in the commit");
    assert_eq!(repo.git(&["rev-list", "--count", "HEAD"]), "2", "no new commit was added");
    assert_eq!(repo.git(&["log", "-1", "--format=%s"]), "second");
    assert_eq!(repo.git(&["show", "HEAD:a.txt"]), "three");

    block_on(amend_git_commit(repo.root(), Some("second, reworded".into()))).unwrap();
    assert_eq!(repo.git(&["log", "-1", "--format=%s"]), "second, reworded");
    assert_eq!(repo.git(&["rev-list", "--count", "HEAD"]), "2");

    let empty = Repo::new("amend-empty");
    let refused = block_on(amend_git_commit(empty.root(), None)).unwrap_err();
    assert_eq!(refused, "There is no commit here yet, so there is nothing to amend");
}

// Branch create, create-and-switch, and switch each leave the repository on
// the branch the person picked, and the list marks it and names the remotes.
#[test]
fn branches_create_switch_and_list_the_checked_out_one() {
    let repo = Repo::new("branches");
    repo.commit_file("a.txt", "one\n", "first");
    repo.git(&["remote", "add", "origin", "https://example.invalid/repo.git"]);

    let created = block_on(create_git_branch(repo.root(), "spike".into(), false)).unwrap();
    assert_eq!(created.message, "Created spike");
    assert_eq!(created.status.branch.as_deref(), Some("main"), "creating alone does not switch");

    let switched = block_on(switch_git_branch(repo.root(), "spike".into())).unwrap();
    assert_eq!(switched.message, "Switched to spike");
    assert_eq!(switched.status.branch.as_deref(), Some("spike"));

    let both = block_on(create_git_branch(repo.root(), "tsk-1-next".into(), true)).unwrap();
    assert_eq!(both.message, "Created tsk-1-next and switched to it");
    assert_eq!(both.status.branch.as_deref(), Some("tsk-1-next"));

    let list = block_on(list_git_branches(repo.root())).unwrap();
    assert_eq!(list.current, "tsk-1-next");
    let mut names = list.branches.iter().map(|branch| branch.name.as_str()).collect::<Vec<_>>();
    names.sort();
    assert_eq!(names, vec!["main", "spike", "tsk-1-next"]);
    assert_eq!(list.remotes, vec!["origin".to_string()]);

    let refused = block_on(switch_git_branch(repo.root(), "--orphan".into())).unwrap_err();
    assert_eq!(refused, "A branch name cannot start with a dash");
    assert_eq!(repo.git(&["branch", "--show-current"]), "tsk-1-next");
}

// Stash puts the working copy aside (new files too, when asked) and pop brings
// it all back; stashing a clean copy says so instead of failing (cef560734).
#[test]
fn stash_and_pop_round_trip_the_working_copy() {
    let repo = Repo::new("stash");
    repo.commit_file("a.txt", "one\n", "first");
    repo.write("a.txt", "changed\n");
    repo.write("new.txt", "fresh\n");

    let stashed = block_on(stash_git_changes(repo.root(), true, Some("before".into()))).unwrap();
    assert_eq!(stashed.message, "Stashed the working copy");
    assert!(stashed.status.files.is_empty());
    assert_eq!(repo.read("new.txt"), None);

    let popped = block_on(pop_git_stash(repo.root(), None)).unwrap();
    assert_eq!(popped.message, "Restored the stashed changes");
    assert_eq!(repo.read("a.txt").as_deref(), Some("changed\n"));
    assert_eq!(repo.read("new.txt").as_deref(), Some("fresh\n"));

    repo.git(&["checkout", "--", "a.txt"]);
    std::fs::remove_file(repo.path().join("new.txt")).unwrap();
    let clean = block_on(stash_git_changes(repo.root(), false, None)).unwrap();
    assert_eq!(clean.message, "Nothing to stash — the working copy is clean");
}

// Discard (cef560734) puts a staged-and-edited tracked file back to HEAD in
// the index and on disk, deletes a new file it was asked about, and touches
// nothing else: not other changed files, not ignored files.
#[test]
fn discard_restores_tracked_files_and_deletes_only_the_named_new_ones() {
    let repo = Repo::new("discard");
    repo.commit_file(".gitignore", "*.log\n", "ignore logs");
    repo.commit_file("a.txt", "one\n", "first");
    repo.commit_file("keep.txt", "keep\n", "second");
    repo.write("a.txt", "staged\n");
    repo.git(&["add", "a.txt"]);
    repo.write("a.txt", "staged and edited\n");
    repo.write("new.txt", "fresh\n");
    repo.write("keep.txt", "my work\n");
    repo.write("debug.log", "ignored\n");

    let result = block_on(discard_git_paths(
        repo.root(),
        vec!["a.txt".into(), "new.txt".into()],
    ))
    .unwrap();
    assert_eq!(result.message, "Discarded changes in 2 files");
    assert_eq!(repo.read("a.txt").as_deref(), Some("one\n"));
    assert_eq!(repo.read("new.txt"), None);
    assert_eq!(repo.read("keep.txt").as_deref(), Some("my work\n"), "an unnamed change survives");
    assert_eq!(repo.read("debug.log").as_deref(), Some("ignored\n"), "ignored files survive");
    let left = result.status.files.iter().map(|file| file.relative_path.as_str()).collect::<Vec<_>>();
    assert_eq!(left, vec!["keep.txt"]);
}

// git quotes a path that has a space in it; the status strips the quotes so
// the row can be staged by the very path it shows (1ef43684c).
#[test]
fn a_file_with_spaces_in_its_name_is_listed_plainly_and_can_be_staged() {
    let repo = Repo::new("spaces");
    repo.commit_file("README.md", "readme\n", "first");
    repo.write("my notes.txt", "hello\n");

    let status = project_git_status_sync(repo.path().into()).unwrap();
    assert_eq!(status.files[0].relative_path, "my notes.txt");

    let staged =
        stage_git_paths_sync(repo.path().into(), vec![status.files[0].relative_path.clone()])
            .unwrap();
    assert_eq!(staged.status.files[0].index_status, "added");
    assert_eq!(run_git_text(repo.path(), &["diff", "--cached", "--name-only"]).unwrap().trim(), "my notes.txt");
}

// A staged rename shows as the new path with an R badge, so clicking it opens
// a file that exists.
#[test]
fn a_staged_rename_is_listed_under_its_new_path() {
    let repo = Repo::new("rename");
    repo.commit_file("old.txt", "one\ntwo\nthree\n", "first");
    repo.git(&["mv", "old.txt", "new.txt"]);

    let status = project_git_status_sync(repo.path().into()).unwrap();
    assert_eq!(status.files.len(), 1);
    assert_eq!(status.files[0].relative_path, "new.txt");
    assert_eq!(status.files[0].badge, "R");
    assert_eq!(status.files[0].index_status, "renamed");
}

// Errors from gh and git reach the panel as one readable line: stderr first,
// then stdout, then the bare command; a missing gh says how to fix it (7a07d8e14).
#[test]
fn process_failures_read_as_one_plain_sentence() {
    assert_eq!(
        format_process_failure("gh pr list", b"  no git remotes found \n", b"ignored"),
        "gh pr list failed: no git remotes found"
    );
    assert_eq!(
        format_process_failure("gh pr list", b"   ", b"HTTP 401\n"),
        "gh pr list failed: HTTP 401"
    );
    assert_eq!(format_process_failure("gh pr list", b"", b""), "gh pr list failed");
    assert_eq!(
        format_gh_spawn_error(std::io::Error::from(std::io::ErrorKind::NotFound)),
        "GitHub CLI (`gh`) is not installed or not on PATH"
    );
    assert!(format_gh_spawn_error(std::io::Error::from(std::io::ErrorKind::PermissionDenied))
        .starts_with("Could not start gh: "));
}

// The pull request list's check badge: any failed, cancelled or timed-out run
// makes the request "failing" even while others are still running.
#[test]
fn a_failed_check_outranks_running_ones_in_the_pull_request_badge() {
    let checks = serde_json::json!([
        {"status": "COMPLETED", "conclusion": "SUCCESS"},
        {"status": "IN_PROGRESS", "conclusion": ""},
        {"status": "COMPLETED", "conclusion": "TIMED_OUT"},
        {"status": "COMPLETED", "conclusion": "CANCELLED"}
    ]);
    assert_eq!(
        summarize_checks(Some(&checks)),
        ("failing".to_string(), "1 passing · 1 pending · 2 failing".to_string())
    );
    let green = serde_json::json!([{"status": "COMPLETED", "conclusion": "SUCCESS"}]);
    assert_eq!(summarize_checks(Some(&green)).0, "passing");
    assert_eq!(summarize_checks(Some(&serde_json::json!([]))).0, "none");
}
