use super::{changed_lines, list_entries, project_label, same_line};
use crate::core::socket::outcome::Outcome;
use crate::features::sessions::list_entry::ListEntry;
use crate::features::sessions::rail_page::{Rail, RailEntry, RailPage, test_entry};
use crate::features::sessions::status::Status;

fn parent(id: &str, repo: &str, group_start: bool) -> RailEntry {
    RailEntry {
        group_start,
        ..test_entry(id, repo)
    }
}

fn page(sessions: Vec<RailEntry>) -> RailPage {
    RailPage {
        totals: Default::default(),
        agents: vec![],
        sessions,
        next: None,
        start: 0,
    }
}

fn rail_with(page: RailPage) -> Rail {
    let mut rail = Rail::default();
    let (id, _) = rail.changed().unwrap();
    assert_eq!(rail.finish(id, Outcome::Ok(page)), None);
    rail
}

fn heading(repo: &str) -> ListEntry {
    ListEntry::Project {
        label: repo.into(),
        repo_url: repo.into(),
        machine: None,
    }
}

#[test]
fn lines_keep_the_backends_order_with_a_heading_where_it_starts_a_project() {
    // The backend's order, not the window's: "zeta" before "alpha" stays so.
    let (z1, z2, a1) = (
        parent("z1", "/zeta", true),
        parent("z2", "/zeta", false),
        parent("a1", "/alpha", true),
    );
    let rail = rail_with(page(vec![z1.clone(), z2.clone(), a1.clone()]));
    assert_eq!(
        list_entries(&rail),
        [
            heading("/zeta"),
            ListEntry::Session(Box::new(z1)),
            ListEntry::Session(Box::new(z2)),
            heading("/alpha"),
            ListEntry::Session(Box::new(a1)),
        ]
    );
}

#[test]
fn a_project_is_named_by_its_repo_or_its_folders_last_name() {
    assert_eq!(project_label("https://github.com/o/r"), "o/r");
    assert_eq!(project_label("/home/me/dev/Assembly/"), "Assembly");
    assert_eq!(project_label("/"), "/");
}

// Pins: sessions-list#2
#[test]
fn the_row_menu_moves_a_session_to_the_other_two_groups() {
    for status in [Status::Working, Status::Done, Status::Settled] {
        let targets: Vec<Status> = status.moves().iter().map(|(_, to)| *to).collect();
        assert!(!targets.contains(&status));
        assert_eq!(targets.len(), 2);
    }
    assert_eq!(Status::of(None), Status::Working);
    assert_eq!(Status::of(Some(5)), Status::Done);
}

fn line(id: &str) -> ListEntry {
    ListEntry::Session(Box::new(test_entry(id, "/r")))
}

#[test]
fn only_the_lines_between_what_both_share_change() {
    let old = [line("a"), line("b"), line("c"), line("d")];
    assert_eq!(changed_lines(&old, &old), (4..4, 0));
    // "c" moved up past "b": only those two lines change.
    assert_eq!(
        changed_lines(&old, &[line("a"), line("c"), line("b"), line("d")]),
        (1..3, 2)
    );
    // One added at the end, one removed at the start.
    assert_eq!(
        changed_lines(
            &old,
            &[line("a"), line("b"), line("c"), line("d"), line("e")]
        ),
        (4..4, 1)
    );
    assert_eq!(
        changed_lines(&old, &[line("b"), line("c"), line("d")]),
        (0..1, 0)
    );
    assert_eq!(changed_lines(&[], &old), (0..0, 4));
}

// Pins: sessions-list#9
#[test]
fn the_line_at_the_top_is_found_again_when_lines_change_around_it() {
    // An older page adds a session above the reader's line and one below it: the whole span is replaced.
    let old = [line("a"), line("b"), line("c")];
    let new = [line("a"), line("a2"), line("b"), line("c"), line("c2")];
    let (changed, _) = changed_lines(&old, &new);
    assert_eq!(changed, 1..3);
    assert_eq!(same_line(&old, &new, &changed, 1), Some(2));
    // Outside the changed lines the splice keeps the place itself; a line that's gone isn't found.
    assert_eq!(same_line(&old, &new, &changed, 0), None);
    assert_eq!(same_line(&old, &[line("a"), line("c")], &(1..2), 1), None);
}

#[test]
fn a_row_whose_state_changed_is_still_the_line_at_the_top() {
    // "b" started running: its line changed, but it's the same session, so the reader stays on it.
    let old = [line("a"), line("b"), line("c")];
    let running = ListEntry::Session(Box::new(RailEntry {
        running: true,
        ..test_entry("b", "/r")
    }));
    let new = [line("a2"), line("a"), running, line("c")];
    let (changed, _) = changed_lines(&old, &new);
    assert_eq!(same_line(&old, &new, &changed, 1), Some(2));
}
