use super::DiscardRequest;
use crate::features::source_control::git_status::ChangedFile;
fn file(path: &str, index: &str, worktree: &str) -> ChangedFile {
    ChangedFile {
        path: path.into(),
        original_path: None,
        index: index.into(),
        worktree: worktree.into(),
    }
}
// Pins: g9#3
#[test]
fn discard_everything_leaves_new_files_unless_ticked() {
    let request = DiscardRequest::Everything {
        fingerprint: "seen".into(),
        listed: vec![file("a", ".", "M"), file("new", "?", "?")],
        omitted: 0,
    };
    let kept = request.args("s", false);
    assert_eq!(kept["includeUntracked"], false);
    assert_eq!(kept["expectedFingerprint"], "seen");
    assert_eq!(request.question(false).confirm, "Discard everything");
    assert_eq!(request.args("s", true)["includeUntracked"], true);
    assert_eq!(request.question(true).confirm, "Discard and delete");
    assert_eq!(request.question(false).title, "Discard every change?");
}
#[test]
fn discard_of_a_staged_new_file_warns_it_will_be_deleted() {
    let question = DiscardRequest::Files(vec![file("new", "A", ".")]).question(false);
    assert_eq!(question.title, "Discard changes to this file?");
    assert_eq!(question.confirm, "Delete the file");
    assert_eq!(question.lines, ["1 file git has never tracked will be deleted from disk."]);
}
#[test]
fn several_files_say_how_many_of_each_and_keep_their_own_words() {
    let question = DiscardRequest::Files(vec![
        file("a", ".", "M"),
        file("b", "M", "."),
        file("new", "?", "?"),
    ])
    .question(false);
    assert_eq!(question.title, "Discard changes to 3 files?");
    assert_eq!(question.confirm, "Discard changes");
    assert_eq!(
        question.lines,
        [
            "1 file git has never tracked will be deleted from disk.",
            "2 files goes back to how the last commit has it, staged or not."
        ]
    );
}
#[test]
fn a_rename_restores_both_paths_and_new_files_go_in_their_own_list() {
    let mut renamed = file("new-name", "R", ".");
    renamed.original_path = Some("old-name".into());
    let args = DiscardRequest::Files(vec![renamed, file("n", "?", "?")]).args("s", false);
    assert_eq!(args["tracked"], serde_json::json!(["new-name", "old-name"]));
    assert_eq!(args["untracked"], serde_json::json!(["n"]));
    assert_eq!(args["expected"].as_array().unwrap().len(), 2);
}
