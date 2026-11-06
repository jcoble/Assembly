//! Feature 28 spec sections 3 and 6: the one resolver for file links. A relative path needs a session; an absolute or
//! `~` path goes to the backend as a path target, with the selected session's own id on its machine as the inside
//! hint, and opens as a session file or as an outside file by machine and real path.

use gpui::TestAppContext;
use serde_json::json;

use super::deep_links_file_tests::{HOME, Harness, ROOT, harness};
use super::file_link;
use crate::core::engine::SessionKey;
use crate::core::intents::open_file::{OpenFile, OutsideFile};
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::machines::Machines;

fn session(id: &str, route: Option<&str>) -> (SessionKey, ExpectedRowSource) {
    let key = SessionKey {
        machine: "owner".into(),
        id: id.into(),
    };
    let source = ExpectedRowSource {
        machine: "owner".into(),
        generation: u64::from(route.is_some()),
        route_machine: route.map(String::from),
    };
    (key, source)
}

/// What a notice calls this machine: "this Mac" on a Mac, "this computer" anywhere else.
pub(super) fn this_computer() -> &'static str {
    if cfg!(target_os = "macos") {
        "this Mac"
    } else {
        "this computer"
    }
}

fn outside(machine: Option<&str>, path: &str) -> OpenFile {
    OpenFile {
        session_id: String::new(),
        path: path.into(),
        expected_source: None,
        line: None,
        column: None,
        pinned: true,
        outside: Some(OutsideFile {
            machine: machine.map(String::from),
            path: path.into(),
        }),
        whole: None,
    }
}

impl Harness<'_> {
    /// Hands a path to the resolver as a chat link or the editor does, outside any window update.
    async fn open(
        &mut self,
        path: &str,
        machine: Option<&str>,
        from: Option<(SessionKey, ExpectedRowSource)>,
    ) {
        self.open_at(path, machine, from, (None, None)).await;
    }

    /// As `open`, at a line and column, as a changes or pull request view sends.
    async fn open_at(
        &mut self,
        path: &str,
        machine: Option<&str>,
        from: Option<(SessionKey, ExpectedRowSource)>,
        jump: file_link::Jump,
    ) {
        let (path, machine) = (path.to_string(), machine.map(String::from));
        self.cx
            .update(|_, cx| cx.defer(move |cx| file_link::open(path, machine, from, jump, cx)));
        self.settle().await;
    }

    fn asked(&self) -> Vec<serde_json::Value> {
        self.owner.lock().unwrap().asked.clone()
    }
}

#[gpui::test]
async fn a_relative_path_with_a_session_opens_as_today(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    let (key, source) = session("s1", None);
    h.open("src/a.txt", None, Some((key, source.clone()))).await;
    let expected = OpenFile {
        session_id: "s1".into(),
        path: "src/a.txt".into(),
        expected_source: Some(source),
        line: None,
        column: None,
        pinned: true,
        outside: None,
        whole: None,
    };
    assert!(h.opened() == [expected]);
    assert!(h.asked().is_empty(), "a session file needs no question");
    assert_eq!(h.shown(), Vec::<String>::new());
}

#[gpui::test]
async fn a_relative_path_with_no_session_says_select_a_session(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.open("src/a.txt", None, None).await;
    assert_eq!(h.shown(), ["Select a session to open this file."]);
    assert!(h.opened().is_empty());
    assert!(h.asked().is_empty());
}

#[gpui::test]
async fn an_absolute_path_inside_the_session_opens_as_a_session_file(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    let (key, source) = session("s1", None);
    h.open(
        &format!("{ROOT}/src/a.txt"),
        None,
        Some((key, source.clone())),
    )
    .await;
    let expected = OpenFile {
        session_id: "s1".into(),
        path: "src/a.txt".into(),
        expected_source: Some(source),
        line: None,
        column: None,
        pinned: true,
        outside: None,
        whole: None,
    };
    assert!(h.opened() == [expected]);
    assert_eq!(h.shown(), Vec::<String>::new());
}

#[gpui::test]
async fn an_absolute_path_outside_opens_as_an_outside_file(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.open("/etc/hosts", None, Some(session("s1", None))).await;
    assert!(h.opened() == [outside(None, "/etc/hosts")]);
    assert_eq!(h.shown(), Vec::<String>::new());
}

#[gpui::test]
async fn a_link_with_no_session_selected_opens_outside(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.link("/work/a/x.cs", None).await;
    assert!(h.opened() == [outside(None, "/work/a/x.cs")]);
    assert_eq!(h.shown(), Vec::<String>::new());
    let asked = h.asked();
    assert_eq!(asked.len(), 1, "no session, so no inside hint to ask for");
    assert_eq!(
        asked[0],
        json!({"target": {"path": "/work/a/x.cs", "machine": null, "insideOf": null}})
    );
}

#[gpui::test]
async fn a_link_on_another_machine_sends_that_machine_and_no_inside_hint_from_a_session_elsewhere(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", None);
    h.link("/work/a/x.cs", Some("Box")).await;
    h.select("s2", Some("Box"));
    h.link("/srv/y.cs", None).await;
    assert!(
        h.opened()
            == [
                outside(Some("Box"), "/work/a/x.cs"),
                outside(None, "/srv/y.cs")
            ]
    );
    let asked = h.asked();
    assert_eq!(asked.len(), 2, "neither session is on the link's machine");
    assert_eq!(
        asked[0]["target"],
        json!({"path": "/work/a/x.cs", "machine": "Box", "insideOf": null})
    );
    assert_eq!(
        asked[1]["target"],
        json!({"path": "/srv/y.cs", "machine": null, "insideOf": null})
    );
    assert_eq!(h.shown(), Vec::<String>::new());
}

#[gpui::test]
async fn a_tilde_link_goes_to_the_backend_unexpanded(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.link("~/notes.txt", None).await;
    assert_eq!(h.asked()[0]["target"]["path"], "~/notes.txt");
    assert!(h.opened() == [outside(None, &format!("{HOME}/notes.txt"))]);
}

#[gpui::test]
async fn each_refusal_code_shows_its_words(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    let older = "Box's Assembly is older and can't open files outside a session yet.";
    for (code, message) in [
        ("not_a_path", "nul"),
        ("not_full_path", "dots"),
        ("not_regular", "fifo"),
        ("remote_older", older),
        ("failed", "that file isn't there"),
        ("failed", "it moved."),
    ] {
        h.owner.lock().unwrap().refuse = Some((code, message));
        h.link("/work/a/x.cs", None).await;
    }
    assert_eq!(
        h.shown(),
        [
            "That file link isn't a file path.",
            "That file link isn't a full path.",
            "That isn't a regular file, so it can't open.",
            older,
            "That file didn't open: That file isn't there.",
            "That file didn't open: It moved.",
        ]
    );
    assert!(h.opened().is_empty());
}

#[gpui::test]
async fn the_path_call_carries_a_target_and_no_session_with_the_session_s_own_id_as_the_hint(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.select("s1", Some("Box"));
    h.link("/work/a/x.cs", Some("Box")).await;
    let asked = h.asked();
    assert_eq!(asked.len(), 2);
    assert_eq!(asked[0]["session"], "s1", "the session's folder first");
    assert!(asked[1].get("session").is_none(), "{:?}", asked[1]);
    assert_eq!(
        asked[1]["target"],
        json!({"path": "/work/a/x.cs", "machine": "Box", "insideOf": "r"})
    );
}

// Pinned by review: a failed session question is no inside hint, so the file opens outside, which is locked: the
// safe default when the session can't say.
#[gpui::test]
async fn a_session_whose_folder_question_fails_still_opens_the_file_outside(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.owner.lock().unwrap().refuse_session = true;
    h.select("s1", None);
    h.link("/work/a/x.cs", None).await;
    assert!(h.opened() == [outside(None, "/work/a/x.cs")]);
    assert_eq!(h.asked()[1]["target"]["insideOf"], serde_json::Value::Null);
    assert_eq!(h.shown(), Vec::<String>::new());
}

#[gpui::test]
async fn a_link_to_a_machine_missing_from_the_loaded_list_fails_at_once(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.cx.update(|_, cx| {
        Machines::start_holding(
            vec![(
                "Box".into(),
                json!({"name": "Box", "kind": "remote", "reachable": true, "checked": true}),
            )],
            cx,
        )
    });
    h.link("/srv/y.cs", Some("Nowhere")).await;
    assert_eq!(
        h.shown(),
        ["That file is on Nowhere, which this app isn't connected to."]
    );
    assert!(h.opened().is_empty());
    assert!(
        h.asked().is_empty(),
        "nothing is asked of a missing machine"
    );
    h.link("/srv/y.cs", Some("Box")).await;
    assert!(h.opened() == [outside(Some("Box"), "/srv/y.cs")]);
}

// Review fix 1: a line and column survive the resolve, for session files and outside files alike.
#[gpui::test]
async fn a_line_and_column_reach_the_opened_file(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    let jump = (Some(42), Some(7));
    let from = session("s1", None);
    h.open_at("src/a.txt", None, Some(from.clone()), jump).await;
    h.open_at(&format!("{ROOT}/src/a.txt"), None, Some(from.clone()), jump)
        .await;
    h.open_at("/etc/hosts", None, Some(from), jump).await;
    let jumps: Vec<_> = h
        .opened()
        .into_iter()
        .map(|open| (open.path, open.line, open.column))
        .collect();
    assert_eq!(
        jumps,
        [
            ("src/a.txt".to_string(), Some(42), Some(7)),
            ("src/a.txt".to_string(), Some(42), Some(7)),
            ("/etc/hosts".to_string(), Some(42), Some(7)),
        ]
    );
}

// Review fix 2: a relative path is only the session's when the link is for the session's machine.
#[gpui::test]
async fn a_relative_path_for_another_machine_says_which(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.open("x.cs", Some("Box"), Some(session("s1", None))).await;
    h.open("x.cs", None, Some(session("s1", Some("Box")))).await;
    h.open("x.cs", Some("Other"), Some(session("s1", Some("Box"))))
        .await;
    assert_eq!(
        h.shown(),
        [
            "That file is on Box.",
            &format!("That file is on {}.", this_computer()),
            "That file is on Other.",
        ]
    );
    assert!(h.opened().is_empty());
    assert!(h.asked().is_empty(), "the machine is checked before asking");
}

// Review fix 4: a file at the top of the machine joins its folder "/" without doubling the slash.
#[gpui::test]
async fn an_outside_file_at_the_root_folder_has_one_slash(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.open("/hosts", None, None).await;
    assert!(h.opened() == [outside(None, "/hosts")]);
}

// Brief item 6: a machine that's listed but disconnected answers `unreachable`, which reads as the not-connected words.
#[gpui::test]
async fn a_listed_machine_that_is_not_connected_says_so(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.owner.lock().unwrap().refuse = Some(("unreachable", "Box: machine socket is disconnected"));
    h.link("/srv/y.cs", Some("Box")).await;
    assert_eq!(
        h.shown(),
        ["That file is on Box, which this app isn't connected to."]
    );
    assert!(h.opened().is_empty());
}

/// A session in the sessions list on `route` (`None` is this machine), as the list holds it.
fn listed(id: &str, route: Option<&str>) -> crate::core::state::session_item::SessionItem {
    serde_json::from_value(json!({
        "sessionId": id, "agent": "claude", "repoUrl": "", "root": null, "checkoutRoot": null,
        "title": null, "machine": route, "parentSessionId": null, "acpSessionId": null,
        "updatedAt": 1, "activeRunId": null,
    }))
    .unwrap()
}

impl Harness<'_> {
    /// Hands a path to the resolver as an open with a session and no source does.
    async fn open_unsourced(&mut self, path: &str, session: &str) {
        let (path, session) = (path.to_string(), session.to_string());
        self.cx.update(|_, cx| {
            cx.defer(move |cx| file_link::open_unsourced(path, session, (None, None), cx))
        });
        self.settle().await;
    }

    fn list(&mut self, items: Vec<crate::core::state::session_item::SessionItem>) {
        self.cx.update(|_, cx| {
            crate::core::state::sessions::Sessions::start_holding(items, false, false, cx)
        });
    }
}

// Brief item 4: an open with a session but no source (a read-only transcript, or a task's session that isn't the
// selected one) keeps its session and its machine. The machine comes from the sessions list and its source from that
// machine, so a remote session's file resolves there, with the session's own id on that machine as the inside hint.
#[gpui::test]
async fn a_remote_session_with_no_source_resolves_its_file_on_that_machine(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.list(vec![listed("t1", Some("Box"))]);
    h.open_unsourced(&format!("{ROOT}/src/a.txt"), "t1").await;
    let source = ExpectedRowSource {
        machine: "box-engine".into(),
        generation: 2,
        route_machine: Some("Box".into()),
    };
    let asked = h.asked();
    assert_eq!(asked.len(), 2, "{asked:?}");
    assert_eq!(asked[0]["session"], "t1");
    assert_eq!(asked[0]["expectedSource"], json!(source));
    assert_eq!(
        asked[1]["target"],
        json!({"path": format!("{ROOT}/src/a.txt"), "machine": "Box", "insideOf": "r"})
    );
    let expected = OpenFile {
        session_id: "t1".into(),
        path: "src/a.txt".into(),
        expected_source: Some(source),
        line: None,
        column: None,
        pinned: true,
        outside: None,
        whole: None,
    };
    assert!(h.opened() == [expected], "{:?}", h.opened().len());
    assert_eq!(h.shown(), Vec::<String>::new());
}

#[gpui::test]
async fn a_local_session_with_no_source_resolves_its_file_here(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.list(vec![listed("t1", None)]);
    h.open_unsourced("/etc/hosts", "t1").await;
    let asked = h.asked();
    assert_eq!(asked[0]["expectedSource"]["machine"], "owner");
    assert_eq!(
        asked[1]["target"],
        json!({"path": "/etc/hosts", "machine": null, "insideOf": "r"})
    );
    assert!(h.opened() == [outside(None, "/etc/hosts")]);
}

/// What the loaded machines list holds: this machine, and `remote` when given.
fn machines(h: &mut Harness<'_>, remote: Option<&str>) {
    let mut items = vec![(
        "local".to_string(),
        json!({"name": null, "kind": "local", "reachable": true, "checked": true}),
    )];
    if let Some(name) = remote {
        items.push((
            name.to_string(),
            json!({"name": name, "kind": "remote", "reachable": true, "checked": true}),
        ));
    }
    h.cx.update(|_, cx| Machines::start_holding(items, cx));
}

const NO_MACHINE: &str =
    "That file didn't open: Assembly can't tell which machine this session is on.";

// Re-review item 4 (a): a session the list doesn't hold that is the selected one opens with the selection's source.
#[gpui::test]
async fn an_unlisted_session_that_is_selected_uses_the_selection_s_source(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    machines(&mut h, Some("Box"));
    h.list(vec![listed("other", Some("Box"))]);
    h.select("t1", Some("Box"));
    h.open_unsourced("/work/a/src/a.txt", "t1").await;
    let asked = h.asked();
    assert_eq!(asked.len(), 2, "{asked:?}");
    assert_eq!(asked[0]["session"], "t1");
    assert_eq!(asked[0]["expectedSource"]["routeMachine"], "Box");
    assert_eq!(
        asked[1]["target"],
        json!({"path": "/work/a/src/a.txt", "machine": "Box", "insideOf": "r"})
    );
    assert_eq!(h.opened().len(), 1);
    assert_eq!(h.opened()[0].path, "src/a.txt");
    assert_eq!(h.shown(), Vec::<String>::new());
}

// Re-review item 4 (b): with no remote machine configured, the session can only be on this machine, so the file
// resolves here with no inside hint, as before.
#[gpui::test]
async fn an_unlisted_session_with_no_remote_machine_resolves_here(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    machines(&mut h, None);
    h.list(vec![listed("other", None)]);
    h.open_unsourced("/etc/hosts", "t1").await;
    let asked = h.asked();
    assert_eq!(asked.len(), 1, "{asked:?}");
    assert_eq!(
        asked[0]["target"],
        json!({"path": "/etc/hosts", "machine": null, "insideOf": null})
    );
    assert!(h.opened() == [outside(None, "/etc/hosts")]);
    assert_eq!(h.shown(), Vec::<String>::new());
}

// Re-review item 4 (c): no guess. An unlisted, unselected session while a remote is configured could be on either.
#[gpui::test]
async fn an_unlisted_session_with_a_remote_machine_says_it_can_t_tell(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    machines(&mut h, Some("Box"));
    h.list(vec![listed("other", Some("Box"))]);
    h.open_unsourced("/etc/hosts", "t1").await;
    assert_eq!(h.shown(), [NO_MACHINE]);
    assert!(h.asked().is_empty());
    assert!(h.opened().is_empty());
}
