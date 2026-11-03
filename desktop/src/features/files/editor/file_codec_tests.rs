use super::{parse, select, retained_clean, document_key, FilesCodec, Candidate, Entry, DocumentStore, WorkspaceKey};
use gpui_kit::{AppContext as _, Context, IntoElement, Render, Window, div};
use std::collections::HashSet;
use serde_json::json;
fn key() -> WorkspaceKey {
    WorkspaceKey {
        machine: "owning-host".into(),
        session_id: "s".into(),
        checkout_root: "/checkout".into(),
    }
}
#[test]
fn files_snapshot_rejects_alias_paths_and_preserves_exact_draft_bytes() {
    for path in ["../outside", "/absolute", "a/../b", "a\0b"] {
        assert!(parse(&json!({"files":[{"path":path}],"activePath":path}), &key()).is_err());
    }
    let value = json!({"files":[{"path":"src/λ.rs","baseRevision":"actual-sha","draft":"λ\r\n🦀\n",
        "viewState":{"selection":[1,99],"scroll":[0.0,-40.0]}}],"activePath":"src/λ.rs"});
    let parsed = parse(&value, &key()).unwrap();
    let drafts = parsed.drafts(&key());
    assert_eq!(drafts[0].text, "λ\r\n🦀\n");
    assert_eq!(drafts[0].base_revision, "actual-sha");
    assert_eq!(drafts[0].key.machine, "owning-host");
    assert_eq!(drafts[0].view.selection, 1..99);
}
#[test]
fn malformed_draft_identity_duplicate_path_and_unknown_active_tab_are_refused() {
    for value in [
        json!({"files":[{"path":"a","draft":"unsaved"}]}),
        json!({"files":[{"path":"a"},{"path":"a"}]}),
        json!({"files":[],"activePath":"missing"}),
        json!({"files":[{"path":"a","viewState":{"selection":[0,1],"scroll":["bad",0]}}]}),
    ] {
        assert!(parse(&value, &key()).is_err());
    }
}
#[test]
fn clean_limit_keeps_the_active_and_most_recent_in_original_tab_order() {
    let entries = (0..15)
        .map(|index| Candidate {
            entry: Entry {
                path: index.to_string(),
                base_revision: None,
                draft: None,
                view_state: None,
            },
            active: index == 0,
            recent: index,
        })
        .collect();
    let snapshot = select(entries, Vec::new());
    assert_eq!(snapshot.files.len(), 12);
    assert_eq!(snapshot.active_path.as_deref(), Some("0"));
    assert_eq!(
        snapshot
            .files
            .iter()
            .map(|entry| entry.path.as_str())
            .collect::<Vec<_>>(),
        vec![
            "0", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14"
        ]
    );
}

#[gpui::test]
fn restore_budget_is_refused_during_read_only_preflight_without_mutating_live_documents(
    cx: &mut gpui::TestAppContext,
) {
    cx.update(|cx| {
        let key=key(); let document=document_key(&key,"live.rs"); let mut docs=DocumentStore::default();
        docs.open(document.clone(),"disk-revision".into(),"disk".into()).unwrap();docs.replace(&document,0,"newer live draft".into()).unwrap();cx.set_global(docs);
        let files=(0..8).map(|index|json!({"path":format!("stored-{index}.rs"),"baseRevision":"actual-base","draft":"stored draft"})).collect::<Vec<_>>();
        assert!(FilesCodec.validate(&json!({"files":files}),&key,&json!({"version":1}),cx).is_err());
        assert_eq!(cx.global::<DocumentStore>().dirty_keys(&key.machine,&key.checkout_root),vec![document.clone()]);
        assert_eq!(cx.global::<DocumentStore>().draft(&document),Some("newer live draft"));
        assert_eq!(cx.global::<DocumentStore>().version(&document),Some(1));
    });
}
#[test]
fn dirty_buffers_are_kept_when_the_clean_tab_limit_is_reached() {
    let candidates = (0..20)
        .map(|index| Candidate {
            entry: Entry {
                path: index.to_string(),
                base_revision: Some("base".into()),
                draft: (index < 3).then(|| "λ\r\n".into()),
                view_state: None,
            },
            active: index == 19,
            recent: index,
        })
        .collect();
    let snapshot = select(
        candidates,
        vec![Entry {
            path: "hidden-dirty".into(),
            base_revision: Some("base".into()),
            draft: Some("hidden text".into()),
            view_state: None,
        }],
    );
    assert_eq!(
        snapshot
            .files
            .iter()
            .filter(|entry| entry.draft.is_none())
            .count(),
        12
    );
    assert_eq!(
        snapshot
            .files
            .iter()
            .filter(|entry| entry.draft.is_some())
            .count(),
        4
    );
    assert_eq!(snapshot.active_path.as_deref(), Some("19"));
}

#[test]
fn clean_retention_keys_match_serialized_file_order_without_copying_draft_text() {
    let candidates: Vec<_> = (0..18)
        .map(|index| Candidate {
            entry: Entry {
                path: format!("{index}.rs"),
                base_revision: Some("base".into()),
                draft: (index < 2).then(|| "actual large draft".into()),
                view_state: None,
            },
            active: index == 2,
            recent: index,
        })
        .collect();
    let retained = retained_clean(candidates.iter().map(|candidate| {
        (
            candidate.entry.path.as_str(),
            candidate.active,
            candidate.recent,
            candidate.entry.draft.is_some(),
        )
    }))
    .into_iter()
    .map(str::to_owned)
    .collect::<HashSet<_>>();
    let snapshot = select(candidates, Vec::new());
    assert_eq!(
        retained,
        snapshot
            .files
            .iter()
            .filter(|entry| entry.draft.is_none())
            .map(|entry| entry.path.clone())
            .collect()
    );
    assert!(retained.contains("2.rs"));
    assert_eq!(retained.len(), 12);
}

struct CaptureHost;
impl Render for CaptureHost {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
#[gpui::test]
fn a_missing_workspace_identity_cannot_be_captured_as_an_empty_snapshot(
    cx: &mut gpui::TestAppContext,
) {
    cx.update(gpui_kit::init);
    let (_, cx) = cx.add_window_view(|_, _| CaptureHost);
    cx.update(|window, cx| {
        assert!(
            FilesCodec
                .capture(window, cx)
                .unwrap_err()
                .contains("identity")
        )
    });
}
