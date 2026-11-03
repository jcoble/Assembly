use super::super::{
    pull_request::PullRequestDetail,
    review_draft::{ReviewDraft, ReviewIdentity},
};
use super::PrFiles;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use gpui_kit::component::diff::{DiffLinePosition, DiffMode, DiffSide};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
#[gpui::test]
async fn selected_files_use_exact_immutable_revisions_and_keep_review_targets_and_mode(
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
    let detail:PullRequestDetail=serde_json::from_value(json!({"repository":"o/r","localRoot":"/fixture","number":7,"title":"PR","body":"","bodyBlocks":[],"url":"https://github.com/o/r/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[{"path":"a.rs","previousPath":null,"status":"modified","additions":1,"deletions":1,"patch":null,"patchOmitted":false},{"path":"b.rs","previousPath":null,"status":"modified","additions":1,"deletions":1,"patch":null,"patchOmitted":false}],"moreFiles":false,"timeline":[]})).unwrap();
    let draft = cx.new(|_| {
        ReviewDraft::new(ReviewIdentity {
            repository: detail.repository.clone(),
            number: 7,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        })
    });
    let files = cx.new(|cx| PrFiles::new("session".into(), &detail, draft.clone(), cx));
    cx.run_until_parked();
    assert!(seen.lock().unwrap().is_empty());
    let sent = changed.clone();
    let _observe = cx.update(|cx| {
        cx.observe(&files, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    files.update(cx, |files, cx| files.select("a.rs".into(), cx));
    until("first file", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| files.read(cx).context)
    })
    .await;
    let id = files.update(cx, |files, cx| {
        files.comment(&DiffLinePosition::new("a.rs", DiffSide::Modified, 1), cx);
        files.draft.read(cx).comments[0].id.clone()
    });
    files.update(cx, |files, cx| {
        files.mode = DiffMode::Split;
        files
            .state
            .update(cx, |state, cx| state.set_mode(DiffMode::Split, cx));
        files.select("b.rs".into(), cx);
    });
    until("second file", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| files.read(cx).context && files.read(cx).selected.as_deref() == Some("b.rs"))
    })
    .await;
    cx.update(|cx| {
        assert_eq!(draft.read(cx).comments[0].id, id);
        assert_eq!(draft.read(cx).comments[0].target.path, "a.rs");
        assert_eq!(files.read(cx).state.read(cx).mode(), DiffMode::Split);
    });
    files.update(cx, |files, cx| files.replace_detail(&detail, cx));
    cx.run_until_parked();
    assert_eq!(seen.lock().unwrap().len(), 2);
    let mut changed_head = detail.clone();
    changed_head.head_sha = "cccccccccccccccccccccccccccccccccccccccc".into();
    files.update(cx, |files, cx| files.replace_detail(&changed_head, cx));
    until("new head file", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| files.read(cx).context) && seen.lock().unwrap().len() == 3
    })
    .await;
    cx.update(|cx| {
        assert_eq!(draft.read(cx).comments[0].id, id);
        assert_eq!(files.read(cx).state.read(cx).mode(), DiffMode::Split);
        assert_ne!(draft.read(cx).identity.head, files.read(cx).identity.head);
    });
    let seen = seen.lock().unwrap();
    assert_eq!(seen.len(), 3);
    assert_eq!(seen[1]["args"]["baseSha"], detail.base_sha);
    assert_eq!(seen[1]["args"]["headSha"], detail.head_sha);
    assert_eq!(seen[2]["args"]["headSha"], changed_head.head_sha);
}
