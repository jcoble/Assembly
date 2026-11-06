//! Controller note 2, ruling 1: a link ending in a line suffix (`a.rs:12`, `a.rs:12:4`) names a line, unless a file
//! whose real name ends in that suffix exists: that's checked first, on the file's machine, before the suffix is read.

use gpui::TestAppContext;
use serde_json::json;

use super::deep_links_file_tests::{Harness, ROOT, harness};
use super::file_link_whole;
use crate::core::intents::open_file::OpenFile;
use crate::core::socket::row_source::ExpectedRowSource;

fn source(route: Option<&str>) -> ExpectedRowSource {
    ExpectedRowSource {
        machine: "owner".into(),
        generation: u64::from(route.is_some()),
        route_machine: route.map(String::from),
    }
}

/// The intent a chat link gives: its path and line read from the link, with the link as written.
fn link(session: &str, whole: &str, route: Option<&str>) -> OpenFile {
    OpenFile::link(session, whole, Some(source(route))).expect("a file link")
}

fn opening(
    session: &str,
    path: &str,
    jump: (Option<u32>, Option<u32>),
    route: Option<&str>,
) -> OpenFile {
    OpenFile {
        session_id: session.into(),
        path: path.into(),
        expected_source: (!session.is_empty()).then(|| source(route)),
        line: jump.0,
        column: jump.1,
        pinned: true,
        outside: None,
        whole: None,
    }
}

impl Harness<'_> {
    async fn follow(&mut self, intent: OpenFile) {
        self.cx
            .update(|_, cx| cx.defer(move |cx| file_link_whole::open_whole_first(intent, cx)));
        self.settle().await;
    }

    fn gone(&mut self, path: &str) {
        self.owner.lock().unwrap().missing.push(path.into());
    }
}

#[gpui::test]
async fn a_relative_link_with_a_line_and_column_opens_there_when_no_file_has_its_whole_name(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.gone(&format!("{ROOT}/src/a.rs:12:4"));
    h.follow(link("s1", "src/a.rs:12:4", None)).await;
    assert!(h.opened() == [opening("s1", "src/a.rs", (Some(12), Some(4)), None)]);
    let asked = h.owner.lock().unwrap().asked.clone();
    assert_eq!(asked[0]["session"], "s1");
    assert_eq!(
        asked[1]["target"],
        json!({"path": format!("{ROOT}/src/a.rs:12:4"), "machine": null, "insideOf": "r"})
    );
    assert_eq!(h.shown(), Vec::<String>::new());
}

#[gpui::test]
async fn a_relative_file_whose_real_name_ends_in_a_line_suffix_opens_by_that_name(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.follow(link("s1", "src/a.rs:12", None)).await;
    assert!(h.opened() == [opening("s1", "src/a.rs:12", (None, None), None)]);
}

#[gpui::test]
async fn an_absolute_link_checks_its_whole_name_on_the_link_s_machine_first(
    cx: &mut TestAppContext,
) {
    let mut h = harness(cx);
    h.connect();
    h.gone("/etc/x.conf:7");
    h.follow(link("s1", "/etc/x.conf:7", Some("Box"))).await;
    h.follow(link("s1", "/etc/y.conf:7", Some("Box"))).await;
    let opened = h.opened();
    assert!(opened[0] == opening("s1", "/etc/x.conf", (Some(7), None), Some("Box")));
    assert!(opened[1] == opening("s1", "/etc/y.conf:7", (None, None), Some("Box")));
    let asked = h.owner.lock().unwrap().asked.clone();
    let targets: Vec<_> = asked.iter().filter_map(|args| args.get("target")).collect();
    assert_eq!(targets[0]["machine"], "Box");
    assert_eq!(targets[0]["path"], "/etc/x.conf:7");
}

#[gpui::test]
async fn a_link_with_no_line_suffix_opens_without_asking(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    h.follow(link("s1", "src/a.rs", None)).await;
    assert!(h.opened() == [opening("s1", "src/a.rs", (None, None), None)]);
    assert!(h.owner.lock().unwrap().asked.is_empty());
}

// With no session, a relative link has nowhere to look, so its suffix is read as a line and the normal open says why.
#[gpui::test]
async fn a_relative_link_with_no_session_reads_its_suffix_without_asking(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.connect();
    let mut intent = link("s1", "src/a.rs:12", None);
    intent.session_id = String::new();
    intent.expected_source = None;
    h.follow(intent).await;
    assert!(h.opened() == [opening("", "src/a.rs", (Some(12), None), None)]);
    assert!(h.owner.lock().unwrap().asked.is_empty());
}
