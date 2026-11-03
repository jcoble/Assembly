use super::super::{
    pull_request::PullRequestDetail,
    review_draft::{ReviewDraft, ReviewIdentity},
};
use super::{PrFiles, PrOwner};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    row_source::ExpectedRowSource,
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use gpui_kit::component::diff::{DiffLinePosition, DiffMode, DiffSide};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
fn session() -> PrOwner {
    PrOwner::Session {
        id: "session".into(),
        source: None,
    }
}
#[gpui::test]
async fn every_file_shows_at_once_and_a_left_out_file_reads_at_the_exact_revisions(
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
        assert_eq!(frame["method"], "prFile");
        observed.lock().unwrap().push(frame.clone());
        let _ = sent.unbounded_send(());
        let path = frame["args"]["path"].as_str().unwrap();
        let patch = format!(
            "diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-old\n+new\n"
        );
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"path":path,"patch":patch,"binary":false,"truncated":false}}),
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
    let detail:PullRequestDetail=serde_json::from_value(json!({"repository":"o/r","localRoot":"/fixture","number":7,"title":"PR","body":"","bodyBlocks":[],"url":"https://github.com/o/r/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[{"path":"a.rs","previousPath":null,"status":"modified","additions":1,"deletions":1,"patch":"@@ -1 +1 @@\n-old\n+new","patchOmitted":false},{"path":"b.rs","previousPath":null,"status":"modified","additions":1,"deletions":1,"patch":null,"patchOmitted":true}],"moreFiles":false,"timeline":[]})).unwrap();
    let draft = cx.new(|_| {
        ReviewDraft::new(ReviewIdentity {
            repository: detail.repository.clone(),
            number: 7,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        })
    });
    let files = cx.new(|cx| PrFiles::new(session(), &detail, draft.clone(), cx));
    cx.run_until_parked();
    assert!(seen.lock().unwrap().is_empty());
    let shown = |files: &gpui::Entity<PrFiles>, cx: &mut TestAppContext| {
        cx.update(|cx| {
            let state = files.read(cx).state.read(cx);
            state
                .files()
                .iter()
                .map(|file| file.path().to_string())
                .collect::<Vec<_>>()
        })
    };
    assert_eq!(shown(&files, cx), vec!["a.rs"]);
    cx.update(|cx| assert!(files.read(cx).notes[0].can_read));
    let sent = changed.clone();
    let _observe = cx.update(|cx| {
        cx.observe(&files, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    let id = files.update(cx, |files, cx| {
        files.comment(&DiffLinePosition::new("a.rs", DiffSide::Modified, 1), cx);
        files.draft.read(cx).comments[0].id.clone()
    });
    cx.update(|cx| assert!(files.read(cx).adding.contains(&id)));
    files.update(cx, |files, cx| {
        files.set_mode(DiffMode::Split, cx);
        files.read_full("b.rs".into(), cx);
    });
    until("left-out file", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| files.read(cx).parsed.len() == 2)
    })
    .await;
    assert_eq!(shown(&files, cx), vec!["a.rs", "b.rs"]);
    cx.update(|cx| {
        assert_eq!(draft.read(cx).comments[0].id, id);
        assert_eq!(draft.read(cx).comments[0].target.path, "a.rs");
        assert_eq!(files.read(cx).mode(cx), DiffMode::Split);
    });
    files.update(cx, |files, cx| files.replace_detail(&detail, cx));
    cx.run_until_parked();
    assert_eq!(shown(&files, cx), vec!["a.rs", "b.rs"]);
    let mut changed_head = detail.clone();
    changed_head.head_sha = "cccccccccccccccccccccccccccccccccccccccc".into();
    files.update(cx, |files, cx| files.replace_detail(&changed_head, cx));
    cx.run_until_parked();
    // The old head's whole-file read doesn't carry over; the kept draft stays on its head.
    assert_eq!(shown(&files, cx), vec!["a.rs"]);
    cx.update(|cx| {
        assert_eq!(draft.read(cx).comments[0].id, id);
        assert_eq!(files.read(cx).mode(cx), DiffMode::Split);
        assert_ne!(draft.read(cx).identity.head, files.read(cx).identity.head);
    });
    let seen = seen.lock().unwrap();
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0]["args"]["path"], "b.rs");
    assert_eq!(seen[0]["args"]["baseSha"], detail.base_sha);
    assert_eq!(seen[0]["args"]["headSha"], detail.head_sha);
}
#[gpui::test]
fn a_closed_pull_request_takes_no_new_line_comments(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let detail: PullRequestDetail = serde_json::from_value(json!({"repository":"o/r","localRoot":"/fixture","number":7,"title":"PR","body":"","bodyBlocks":[],"url":"https://github.com/o/r/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"MERGED","isDraft":false,"mergeable":"","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[],"moreFiles":false,"timeline":[]})).unwrap();
    let draft = cx.new(|_| {
        ReviewDraft::new(ReviewIdentity {
            repository: "o/r".into(),
            number: 7,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        })
    });
    let files = cx.new(|cx| PrFiles::new(session(), &detail, draft.clone(), cx));
    files.update(cx, |files, cx| {
        files.comment(&DiffLinePosition::new("a.rs", DiffSide::Modified, 1), cx)
    });
    cx.update(|cx| assert!(draft.read(cx).comments.is_empty()));
}
#[gpui::test]
async fn a_project_file_reads_by_project_with_its_source(cx: &mut TestAppContext) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let sent = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] == "call" {
            observed.lock().unwrap().push(frame.clone());
            let _ = sent.unbounded_send(());
        }
        vec![]
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
    let detail:PullRequestDetail=serde_json::from_value(json!({"repository":"o/r","localRoot":"/fixture","number":7,"title":"PR","body":"","bodyBlocks":[],"url":"https://github.com/o/r/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[{"path":"a.rs","previousPath":null,"status":"modified","additions":1,"deletions":1,"patch":"@@ -1 +1 @@\n-old\n+new","patchOmitted":false},{"path":"b.rs","previousPath":null,"status":"modified","additions":1,"deletions":1,"patch":null,"patchOmitted":true}],"moreFiles":false,"timeline":[]})).unwrap();
    let draft = cx.new(|_| {
        ReviewDraft::new(ReviewIdentity {
            repository: "o/r".into(),
            number: 7,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        })
    });
    let owner = PrOwner::Project {
        id: "project".into(),
        source: ExpectedRowSource {
            machine: "engine".into(),
            generation: 3,
            route_machine: Some("lane".into()),
        },
    };
    let path = detail.files[0].path.clone();
    let files = cx.new(|cx| PrFiles::new(owner, &detail, draft, cx));
    files.update(cx, |files, cx| {
        files.set_active(true, cx);
        files.read_full(path.clone(), cx);
    });
    until("the file read", &mut changes, &changed, || {
        cx.run_until_parked();
        !seen.lock().unwrap().is_empty()
    })
    .await;
    let seen = seen.lock().unwrap();
    assert_eq!(seen[0]["method"], "prFile");
    let args = &seen[0]["args"];
    assert_eq!(args["session"], Value::Null);
    assert_eq!(args["projectId"], "project");
    assert_eq!(args["path"], path.as_str());
    assert_eq!(args["expectedSource"]["routeMachine"], "lane");
}

fn two_line_detail() -> PullRequestDetail {
    serde_json::from_value(json!({"repository":"o/r","localRoot":"/fixture","number":7,"title":"PR","body":"","bodyBlocks":[],"url":"https://github.com/o/r/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[{"path":"a.rs","previousPath":null,"status":"modified","additions":1,"deletions":1,"patch":"@@ -1,3 +1,3 @@\n keep\n-old\n+new\n tail","patchOmitted":false}],"moreFiles":false,"timeline":[]})).unwrap()
}
#[gpui::test]
fn comments_sit_under_their_files_last_line_and_keep_the_line_they_are_about(
    cx: &mut TestAppContext,
) {
    cx.update(gpui_kit::init);
    let detail = two_line_detail();
    let draft = cx.new(|_| {
        ReviewDraft::new(ReviewIdentity {
            repository: "o/r".into(),
            number: 7,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        })
    });
    let files = cx.new(|cx| PrFiles::new(session(), &detail, draft.clone(), cx));
    files.update(cx, |files, cx| {
        files.comment(&DiffLinePosition::new("a.rs", DiffSide::Modified, 2), cx);
        let first = files.draft.read(cx).comments[0].id.clone();
        files.draft.update(cx, |draft, _| draft.update_comment(&first, "  added  ".into()));
        files.add_comment(&first, cx);
        files.comment(&DiffLinePosition::new("a.rs", DiffSide::Original, 2), cx);
    });
    cx.update(|cx| {
        let files = files.read(cx);
        let draft = draft.read(cx);
        assert_eq!(draft.comments[0].target.line, 2);
        assert_eq!(draft.comments[0].target.side, "RIGHT");
        assert_eq!(draft.comments[0].target.body, "added");
        assert_eq!(draft.comments[1].target.side, "LEFT");
        let (annotations, contents) = files.annotations(cx);
        let anchored: Vec<_> = (annotations.iter())
            .map(|annotation| annotation.position().map(|at| (at.side(), at.line())))
            .collect();
        assert_eq!(anchored, [Some((DiffSide::Modified, 3)), Some((DiffSide::Modified, 3))]);
        assert_eq!(contents.len(), 2);
        assert!(!files.adding.contains(&draft.comments[0].id));
        assert!(files.adding.contains(&draft.comments[1].id));
    });
}
#[gpui::test]
fn starting_another_comment_drops_the_one_not_added_to_the_review(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let detail = two_line_detail();
    let draft = cx.new(|_| {
        ReviewDraft::new(ReviewIdentity {
            repository: "o/r".into(),
            number: 7,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        })
    });
    let files = cx.new(|cx| PrFiles::new(session(), &detail, draft.clone(), cx));
    files.update(cx, |files, cx| {
        files.comment(&DiffLinePosition::new("a.rs", DiffSide::Modified, 1), cx);
        files.comment(&DiffLinePosition::new("a.rs", DiffSide::Modified, 3), cx);
    });
    cx.update(|cx| {
        let draft = draft.read(cx);
        assert_eq!(draft.comments.len(), 1);
        assert_eq!(draft.comments[0].target.line, 3);
        assert_eq!(files.read(cx).adding.len(), 1);
    });
}
