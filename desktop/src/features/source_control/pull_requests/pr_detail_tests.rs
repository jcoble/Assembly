use super::{PrDetail, PrOwner};
use crate::features::source_control::pull_requests::review_draft::{
    DraftComment, ReviewDraft, ReviewIdentity,
};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
#[gpui::test]
async fn detail_is_a_bulk_read_and_a_different_repository_cannot_replace_the_requested_target(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let sent = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return vec![];
        }
        observed.lock().unwrap().push(frame.clone());
        let _ = sent.unbounded_send(());
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"repository":"other/repository","localRoot":"/fixture","number":7,"title":"Wrong repository","body":"","bodyBlocks":[],"url":"https://github.com/other/repository/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[],"moreFiles":false,"timeline":[]}}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start_bulk(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let detail = cx.new(|cx| {
        PrDetail::new(
            PrOwner::Session {
                id: "actual-session".into(),
                source: None,
            },
            Some("expected/repository".into()),
            7,
            false,
            cx,
        )
    });
    cx.run_until_parked();
    assert!(seen.lock().unwrap().is_empty());
    let sent = changed.clone();
    let _observe = cx.update(|cx| {
        cx.observe(&detail, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    detail.update(cx, |detail, cx| detail.set_active(true, cx));
    until("repository refusal", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| detail.read(cx).failure.is_some())
    })
    .await;
    cx.update(|cx| assert!(detail.read(cx).detail.is_none()));
    let seen = seen.lock().unwrap();
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0]["feature"], "git");
    assert_eq!(seen[0]["method"], "pr");
    assert_eq!(seen[0]["args"]["session"], "actual-session");
}

const OLD_HEAD: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const NEW_HEAD: &str = "cccccccccccccccccccccccccccccccccccccccc";

/// A page that has read this head, holding a draft begun on the old one.
fn page_with_draft(
    cx: &mut TestAppContext,
    head: &str,
    state: &str,
    comments: usize,
) -> gpui::Entity<PrDetail> {
    let shown: crate::features::source_control::pull_requests::pull_request::PullRequestDetail =
        serde_json::from_value(json!({"repository":"o/r","localRoot":"/r","number":7,"title":"PR","body":"","bodyBlocks":[],"url":"https://github.com/o/r/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":head,"baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":state,"isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[],"moreFiles":false,"timeline":[]})).unwrap();
    cx.new(|cx| {
        let mut page = PrDetail::new(
            PrOwner::Session { id: "session".into(), source: None },
            Some("o/r".into()),
            7,
            false,
            cx,
        );
        let mut draft = ReviewDraft::new(ReviewIdentity {
            repository: "o/r".into(),
            number: 7,
            head: OLD_HEAD.into(),
            base: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb".into(),
        });
        draft.body = "words the person typed".into();
        for index in 0..comments {
            draft.comments.push(DraftComment {
                id: index.to_string(),
                target: serde_json::from_value(json!({
                    "path":"a.rs","line":1,"side":"RIGHT","body":"on the old head",
                }))
                .unwrap(),
            });
        }
        page.review = Some(cx.new(|_| draft));
        page.detail = Some(shown);
        page.page_read(false, cx);
        page
    })
}

#[gpui::test]
fn a_moved_head_clears_a_draft_tied_to_it_and_says_so(cx: &mut TestAppContext) {
    let page = page_with_draft(cx, NEW_HEAD, "OPEN", 1);
    cx.update(|cx| {
        let page = page.read(cx);
        let draft = page.review.as_ref().unwrap().read(cx);
        assert!(draft.comments.is_empty());
        assert!(draft.body.is_empty());
        assert_eq!(draft.identity.head, NEW_HEAD);
        assert!(page.failure.as_deref().unwrap().starts_with("The PR changed."));
    });
}

#[gpui::test]
fn a_moved_head_keeps_words_only_typed_and_they_go_with_the_new_head(cx: &mut TestAppContext) {
    let page = page_with_draft(cx, NEW_HEAD, "OPEN", 0);
    cx.update(|cx| {
        let page = page.read(cx);
        let draft = page.review.as_ref().unwrap().read(cx);
        assert_eq!(draft.body, "words the person typed");
        assert_eq!(draft.identity.head, NEW_HEAD);
        assert!(page.failure.is_none());
    });
}

#[gpui::test]
fn line_comments_do_not_outlive_an_open_pull_request(cx: &mut TestAppContext) {
    let page = page_with_draft(cx, OLD_HEAD, "MERGED", 1);
    cx.update(|cx| {
        let draft = page.read(cx).review.as_ref().unwrap().read(cx);
        assert!(draft.comments.is_empty());
        assert_eq!(draft.body, "words the person typed");
    });
}
