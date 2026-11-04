//! The history table: what it says, what its rows do when pressed, and that opening a commit keeps the place in the list.
use super::super::{CommitFiles, CommitTimeline};
use super::{
    Uncommitted, committed_on, drawn_lanes, empty_line, graph_width, table_footer, uncommitted_of,
    unreadable_path,
};
use crate::core::intents::open_commit_diff::OpenCommitDiff;
use crate::features::source_control::git_history::{GitCommit, GitCommitFile, GitHistory};
use crate::features::source_control::git_status::GitStatus;
use crate::features::source_control::graph_lanes::lanes;
use crate::features::source_control::history_filter::HistoryFilter;
use crate::features::source_control::source_control_pane::ShowSourceControl;
use gpui::TestAppContext;
use gpui_kit::{
    Entity, Modifiers, MouseButton, MouseDownEvent, MouseUpEvent, Pixels, Point, ScrollDelta,
    ScrollEvent, VisualTestContext, point, px, size,
};
use serde_json::json;
use std::cell::RefCell;
use std::rc::Rc;

fn commit(at: usize, author: &str) -> GitCommit {
    GitCommit {
        short_sha: format!("{at:07}"),
        sha: format!("{at:040}"),
        subject: format!("Change {at}"),
        author: author.into(),
        committed_at: "2026-08-17T10:04:00+00:00".into(),
        parents: vec![format!("{:040}", at + 1)],
        ..GitCommit::default()
    }
}
fn status(files: serde_json::Value, total: usize) -> GitStatus {
    serde_json::from_value(json!({
        "branch":"main","head":"abc","ahead":0,"behind":0,"hasUpstream":true,"files":files,"totalFiles":total,"fingerprint":"f"
    }))
    .unwrap()
}
fn table(
    cx: &mut TestAppContext,
    commits: usize,
) -> (Entity<CommitTimeline>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let (timeline, visual) = cx.add_window_view(move |_, cx| {
        let mut timeline = CommitTimeline::new("s1".into(), None, None, false, cx);
        timeline.history = GitHistory {
            commits: (0..commits)
                .map(|at| commit(at, if at == 0 { "Amy" } else { "Bo" }))
                .collect(),
            next_cursor: None,
        };
        timeline.sync_rows();
        timeline
    });
    visual.simulate_resize(size(px(1000.), px(400.)));
    visual.update(|window, _| window.activate_window());
    settle(visual);
    (timeline, visual)
}
fn settle(visual: &mut VisualTestContext) {
    visual.run_until_parked();
    visual.update(|window, cx| window.draw(cx).clear(cx));
    visual.run_until_parked();
    visual.update(|window, cx| window.draw(cx).clear(cx));
}
fn center(visual: &mut VisualTestContext, selector: &'static str) -> Point<Pixels> {
    visual.debug_bounds(selector).expect(selector).center()
}
fn press(visual: &mut VisualTestContext, at: Point<Pixels>, button: MouseButton) {
    let modifiers = Modifiers::default();
    visual.simulate_mouse_move(at, None, modifiers);
    visual.simulate_event(MouseDownEvent {
        position: at,
        button,
        modifiers,
        click_count: 1,
        first_mouse: false,
    });
    visual.simulate_event(MouseUpEvent {
        position: at,
        button,
        modifiers,
        click_count: 1,
    });
    settle(visual);
}
fn leak(text: String) -> &'static str {
    Box::leak(text.into_boxed_str())
}
fn row(at: usize) -> &'static str {
    leak(format!("history-row:{at:040}"))
}
fn file_row(at: usize, path: &str) -> &'static str {
    leak(format!("history-file:{at:040}:{path}"))
}
fn open_with_file(
    timeline: &Entity<CommitTimeline>,
    visual: &mut VisualTestContext,
    at: usize,
    path: &str,
) {
    let at_row = center(visual, row(at));
    press(visual, at_row, MouseButton::Left);
    let sha = format!("{at:040}");
    let file = GitCommitFile {
        path: path.into(),
        original_path: None,
        letter: 'M',
    };
    visual.update(|_, cx| {
        timeline.update(cx, |timeline, cx| {
            timeline.files.insert(sha, CommitFiles::Read(vec![file]));
            cx.notify();
        })
    });
    settle(visual);
}
// Pins: g9#16
#[test]
fn the_footer_says_what_the_filters_left_and_what_is_read() {
    let none = HistoryFilter::default();
    let branch = HistoryFilter {
        branch: Some("main".into()),
        ..HistoryFilter::default()
    };
    let author = HistoryFilter {
        author: Some("Amy".into()),
        ..HistoryFilter::default()
    };
    assert_eq!(
        table_footer(&author, 0, 24, false, false, 1),
        "No commit in the history that is loaded matches the current filters."
    );
    assert_eq!(
        table_footer(&branch, 3, 24, false, false, 1),
        "Showing 3 commits from main among 24 loaded."
    );
    assert_eq!(
        table_footer(&author, 3, 24, false, false, 1),
        "3 of 24 loaded commits match the current filters."
    );
    assert_eq!(
        table_footer(&none, 24, 24, true, false, 1),
        "Reading older commits…"
    );
    assert_eq!(
        table_footer(&none, 48, 48, false, true, 2),
        "Showing all 48 commits loaded across pages."
    );
}
#[test]
fn uncommitted_work_is_worded_by_group_and_a_clean_tree_has_no_row() {
    assert_eq!(uncommitted_of(&status(json!([]), 0)), None);
    let file = |path: &str, index: &str, worktree: &str| json!({"path":path,"originalPath":null,"index":index,"worktree":worktree});
    let one = status(json!([file("a", "M", ".")]), 1);
    assert_eq!(
        uncommitted_of(&one),
        Some(Uncommitted {
            count_label: "1 file".into(),
            detail: "Staged 1".into()
        })
    );
    let capped = status(
        json!([
            file("a", "M", "."),
            file("b", ".", "M"),
            file("c", "?", "?")
        ]),
        1500,
    );
    assert_eq!(
        uncommitted_of(&capped),
        Some(Uncommitted {
            count_label: "1500 files".into(),
            detail: "Staged 1 · Changed 1 · New files 1".into()
        })
    );
}
#[test]
fn the_graph_column_never_falls_below_its_heading_and_grows_by_lane() {
    assert_eq!(graph_width(0), 52.);
    assert_eq!(graph_width(1), 52.);
    assert_eq!(graph_width(6), 80.);
    let mut tips: Vec<GitCommit> = (0..12)
        .map(|at| {
            let mut tip = commit(at, "Amy");
            tip.parents = vec![format!("base{at}")];
            tip
        })
        .collect();
    tips.push(commit(99, "Amy"));
    assert_eq!(
        drawn_lanes(&lanes(&tips)),
        8,
        "past eight lanes the rest fold into the last"
    );
}
#[test]
fn a_readable_time_gets_one_shape_and_an_unreadable_one_stays_as_it_came() {
    assert!(committed_on("2026-08-17T10:04:00+00:00").contains("Aug 2026"));
    assert_eq!(committed_on("yesterday-ish"), "yesterday-ish");
}
#[test]
fn a_name_git_could_not_print_is_recognized() {
    assert!(unreadable_path("\"caf\\303\\251.txt\""));
    assert!(unreadable_path("caf\\303\\251.txt"));
    assert!(!unreadable_path("src/plain name.rs"));
}
// Pins: g9#15
#[test]
fn no_commits_yet() {
    assert_eq!(
        empty_line(None, false),
        (
            "No commits yet. The first commit you make will appear here.".to_string(),
            false
        )
    );
    assert_eq!(
        empty_line(None, true),
        ("Reading the commit history…".to_string(), false)
    );
    assert_eq!(
        empty_line(
            Some("fatal: not a git repository (or any of the parent directories)"),
            false
        ),
        (
            "This folder's Git history is unavailable.".to_string(),
            false
        )
    );
    assert_eq!(
        empty_line(Some("fatal: bad object"), false),
        ("fatal: bad object".to_string(), true)
    );
}
// Pins: g9#17
#[gpui::test]
fn expanding_a_commit_lists_its_files(cx: &mut TestAppContext) {
    let (timeline, visual) = table(cx, 3);
    assert!(visual.debug_bounds(file_row(1, "src/a.rs")).is_none());
    open_with_file(&timeline, visual, 1, "src/a.rs");
    assert!(visual.debug_bounds(file_row(1, "src/a.rs")).is_some());
    let at_row = center(visual, row(1));
    press(visual, at_row, MouseButton::Left);
    assert!(
        visual.debug_bounds(file_row(1, "src/a.rs")).is_none(),
        "pressing the row again closes it"
    );
}
#[gpui::test]
fn picking_a_file_dispatches_open_commit_diff(cx: &mut TestAppContext) {
    let (timeline, visual) = table(cx, 3);
    let opened = Rc::new(RefCell::new(Vec::<OpenCommitDiff>::new()));
    let heard = opened.clone();
    visual.update(|_, cx| {
        cx.on_action(move |intent: &OpenCommitDiff, _| heard.borrow_mut().push(intent.clone()));
    });
    open_with_file(&timeline, visual, 1, "src/a.rs");
    let at_row = center(visual, file_row(1, "src/a.rs"));
    press(visual, at_row, MouseButton::Left);
    let opened = opened.borrow();
    assert_eq!(opened.len(), 1);
    assert_eq!(opened[0].sha, format!("{:040}", 1));
    assert_eq!(opened[0].path, "src/a.rs");
    assert_eq!(opened[0].session_id, "s1");
    visual.update(|_, cx| {
        assert_eq!(
            timeline.read(cx).chosen,
            Some((format!("{:040}", 1), "src/a.rs".to_string()))
        )
    });
}
// Pins: g9#17
#[gpui::test]
fn opening_a_commit_keeps_the_place_in_the_list(cx: &mut TestAppContext) {
    let (timeline, visual) = table(cx, 80);
    let over = center(visual, row(2));
    visual.simulate_event(ScrollEvent {
        position: over,
        delta: ScrollDelta::Pixels(point(px(0.), px(-480.))),
        ..ScrollEvent::default()
    });
    settle(visual);
    let top = visual.update(|_, cx| timeline.read(cx).list.logical_scroll_top().item_ix);
    assert!(top > 0, "the wheel moved the list");
    timeline.update(visual, |timeline, cx| {
        let sha = timeline.history.commits[top + 2].sha.clone();
        timeline.toggle_commit(sha, cx);
    });
    settle(visual);
    let after = visual.update(|_, cx| timeline.read(cx).list.logical_scroll_top().item_ix);
    assert_eq!(after, top);
}
#[gpui::test]
fn an_author_or_text_filter_hides_the_uncommitted_row_and_a_branch_does_not(
    cx: &mut TestAppContext,
) {
    let (timeline, visual) = table(cx, 2);
    assert!(
        visual.debug_bounds("history-uncommitted").is_none(),
        "nothing changed, no row"
    );
    let work = Uncommitted {
        count_label: "1 file".into(),
        detail: "Changed 1".into(),
    };
    timeline.update(visual, |timeline, cx| {
        timeline.set_uncommitted(Some(work), cx)
    });
    settle(visual);
    assert!(visual.debug_bounds("history-uncommitted").is_some());
    let filter = HistoryFilter {
        branch: Some("main".into()),
        ..HistoryFilter::default()
    };
    timeline.update(visual, |timeline, cx| timeline.set_filter(filter, cx));
    settle(visual);
    assert!(visual.debug_bounds("history-uncommitted").is_some());
    let filter = HistoryFilter {
        author: Some("Amy".into()),
        ..HistoryFilter::default()
    };
    timeline.update(visual, |timeline, cx| timeline.set_filter(filter, cx));
    settle(visual);
    assert!(visual.debug_bounds("history-uncommitted").is_none());
    assert!(visual.debug_bounds(row(0)).is_some());
    assert!(
        visual.debug_bounds(row(1)).is_none(),
        "another author's commit is gone too"
    );
}
#[gpui::test]
fn the_uncommitted_row_asks_for_source_control(cx: &mut TestAppContext) {
    let (timeline, visual) = table(cx, 1);
    let asked = Rc::new(RefCell::new(0));
    let heard = asked.clone();
    visual.update(|_, cx| {
        cx.on_action(move |_: &ShowSourceControl, _| *heard.borrow_mut() += 1);
    });
    let work = Uncommitted {
        count_label: "1 file".into(),
        detail: "Changed 1".into(),
    };
    timeline.update(visual, |timeline, cx| {
        timeline.set_uncommitted(Some(work), cx)
    });
    settle(visual);
    let at_row = center(visual, "history-uncommitted");
    press(visual, at_row, MouseButton::Left);
    assert_eq!(*asked.borrow(), 1);
}
// Pins: g9#17
#[gpui::test]
fn a_commits_context_menu_copies_sha_subject_and_path(cx: &mut TestAppContext) {
    let (timeline, visual) = table(cx, 2);
    let pick = |visual: &mut VisualTestContext, at: Point<Pixels>, keys: &str| {
        press(visual, at, MouseButton::Right);
        visual.simulate_keystrokes(keys);
        settle(visual);
        visual.update(|_, cx| cx.read_from_clipboard().and_then(|item| item.text()))
    };
    let at = center(visual, row(1));
    assert_eq!(
        pick(visual, at, "down down enter"),
        Some(format!("{:040}", 1))
    );
    assert_eq!(
        pick(visual, at, "down down down enter"),
        Some("Change 1".to_string())
    );
    open_with_file(&timeline, visual, 1, "src/a.rs");
    let at = center(visual, file_row(1, "src/a.rs"));
    assert_eq!(
        pick(visual, at, "down down down enter"),
        Some("src/a.rs".to_string())
    );
}
