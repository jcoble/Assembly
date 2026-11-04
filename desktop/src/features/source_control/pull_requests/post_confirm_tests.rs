use super::super::review_draft::{ReviewDraft, ReviewIdentity};
use super::PostRequest;
fn identity() -> ReviewIdentity {
    ReviewIdentity {
        repository: "o/r".into(),
        number: 7,
        head: "a".repeat(40),
        base: "b".repeat(40),
    }
}
#[test]
fn changed_head_refuses_confirmation_and_the_exact_review_targets_remain_in_the_preview() {
    let mut draft = ReviewDraft::new(identity());
    draft.body = "  Exact review  ".into();
    let id = draft
        .start_comment(&gpui_kit::component::diff::DiffLinePosition::new(
            "ü.rs",
            gpui_kit::component::diff::DiffSide::Original,
            9,
        ))
        .unwrap();
    draft.update_comment(&id, " Exact line ".into());
    let request = PostRequest::review(&draft, &identity()).unwrap();
    assert_eq!(request.body(), "Exact review");
    assert_eq!(request.comments()[0].path, "ü.rs");
    assert_eq!(request.comments()[0].side, "LEFT");
    assert_eq!(request.comments()[0].line, 9);
    assert_eq!(request.comments()[0].body, "Exact line");
    let mut moved = identity();
    moved.head = "c".repeat(40);
    assert!(request.args(&moved).is_err());
    assert_eq!(draft.comments[0].target.body, " Exact line ");
}
#[test]
fn each_confirmation_names_only_what_it_is_tied_to() {
    let mut draft = ReviewDraft::new(identity());
    draft.body = "Review".into();
    let review = PostRequest::review(&draft, &identity()).unwrap();
    assert_eq!(review.fact().as_deref(), Some("head aaaaaaaaaa"));
    let reply = PostRequest::reply(identity(), 41, "Reply".into()).unwrap();
    assert_eq!(reply.fact().as_deref(), Some("comment 41"));
    let comment = PostRequest::comment(identity(), "Comment".into()).unwrap();
    assert_eq!(comment.fact(), None);
}
