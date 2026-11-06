use super::{ReviewDraft, ReviewIdentity};
use gpui_kit::component::diff::{DiffLinePosition, DiffSide};
fn identity(head: &str) -> ReviewIdentity {
    ReviewIdentity {
        repository: "o/r".into(),
        number: 7,
        head: head.into(),
        base: "base".into(),
    }
}
#[test]
fn a_clicked_code_line_keeps_its_exact_file_side_and_source_line() {
    let mut draft = ReviewDraft::new(identity("head"));
    let id = draft
        .start_comment(&DiffLinePosition::new(
            "renamed ü.rs",
            DiffSide::Original,
            12,
        ))
        .unwrap();
    let comment = &draft.comments[0];
    assert_eq!(comment.id, id);
    assert_eq!(comment.target.path, "renamed ü.rs");
    assert_eq!(comment.target.side, "LEFT");
    assert_eq!(comment.target.line, 12);
    draft.update_comment(&id, "Exact comment".into());
    draft.body = "Overall".into();
    assert!(draft.can_post(&identity("head")));
}
#[test]
fn a_moved_head_refuses_posting_without_dropping_or_retargeting_the_draft() {
    let mut draft = ReviewDraft::new(identity("old-head"));
    draft.body = "Review".into();
    let id = draft
        .start_comment(&DiffLinePosition::new("a.rs", DiffSide::Modified, 1))
        .unwrap();
    draft.update_comment(&id, "Keep text".into());
    assert!(!draft.can_post(&identity("new-head")));
    assert_eq!(draft.comments[0].id, id);
    assert_eq!(draft.comments[0].target.body, "Keep text");
    assert_eq!(draft.identity.head, "old-head");
}
#[test]
fn empty_line_text_blocks_posting_and_another_file_does_not_retarget_existing_comments() {
    let mut draft = ReviewDraft::new(identity("head"));
    draft.body = "Review".into();
    let first = draft
        .start_comment(&DiffLinePosition::new("a.rs", DiffSide::Modified, 2))
        .unwrap();
    assert!(!draft.can_post(&identity("head")));
    draft.update_comment(&first, "First line".into());
    assert!(draft.can_post(&identity("head")));
    let second = draft
        .start_comment(&DiffLinePosition::new("other.rs", DiffSide::Original, 8))
        .unwrap();
    assert_eq!(draft.comments[0].id, first);
    assert_eq!(draft.comments[0].target.path, "a.rs");
    assert_eq!(draft.comments[0].target.body, "First line");
    assert!(!draft.can_post(&identity("head")));
    draft.update_comment(&second, "Second line".into());
    assert!(draft.can_post(&identity("head")));
}
