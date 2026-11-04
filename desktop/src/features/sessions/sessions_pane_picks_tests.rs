//! The sessions pane's filter and view picks: how they reach the rail read, are saved and are restored.
use super::*;

// Pins: sessions-list#7
#[test]
fn set_picks_sends_one_read_with_providers_locations_and_statuses() {
    let mut rail = Rail::default();
    let (_, args) = rail.set_picks(
        vec!["claude".into()],
        vec!["local".into()],
        vec!["working".into()],
    );
    assert_eq!(args["providers"], json!(["claude"]));
    assert_eq!(args["locations"], json!(["local"]));
    assert_eq!(args["statuses"], json!(["working"]));
    assert!(rail.changed().is_none(), "the one read is out");
}

// Pins: sessions-list#7
#[test]
fn saved_filters_reads_the_rail_filters_preference() {
    let saved = json!({"provider": ["claude"], "location": ["local"]});
    let state = Update::State(vec![
        ("theme".into(), json!({"key": "theme", "value": "dark"})),
        (
            "rail.filters".into(),
            json!({"key": "rail.filters", "value": saved}),
        ),
    ]);
    assert_eq!(
        saved_filters(&state),
        Some((vec!["claude".into()], vec!["local".into()], vec![]))
    );
    assert_eq!(saved_filters(&Update::State(Vec::new())), None);
}

// Pins: sessions-list#7
#[test]
fn a_saved_pick_not_yet_in_agents_still_shows() {
    let options = provider_options(&["claude".into()], &["codex".into()]);
    let shown: Vec<_> = (options.iter())
        .map(|option| (option.value.as_str(), option.label.as_str()))
        .collect();
    assert_eq!(shown, [("claude", "Claude"), ("codex", "Codex")]);
}

// Pins: sessions-list#6
#[test]
fn view_options_reach_the_rail_read() {
    let mut rail = Rail::default();
    let (_, args) = rail.set_view(View {
        group_by: GroupBy {
            status: true,
            project: false,
        },
        sort: Sort::Name,
        dir: Dir::Desc,
    });
    assert_eq!(args["groupBy"], json!({"status": true, "project": false}));
    assert_eq!(args["sort"], "name");
    assert_eq!(args["dir"], "desc");
}

// Pins: sessions-list#6
#[test]
fn saved_view_is_restored_into_the_rail() {
    let state = Update::State(vec![(
        "rail.view".into(),
        json!({"key": "rail.view", "value": {
            "groupBy": {"status": true, "project": false}, "sort": "name", "dir": "desc"
        }}),
    )]);
    assert_eq!(
        saved_view(&state),
        View {
            group_by: GroupBy {
                status: true,
                project: false,
            },
            sort: Sort::Name,
            dir: Dir::Desc,
        }
    );
    assert_eq!(saved_view(&Update::State(vec![])), View::default());
}

// Pins: sessions-list#7
#[test]
fn status_picks_are_saved_with_the_filters() {
    let saved = json!({"provider": ["claude"], "location": ["local"], "status": ["settled"]});
    let state = Update::State(vec![(
        "rail.filters".into(),
        json!({"key": "rail.filters", "value": saved}),
    )]);
    assert_eq!(
        saved_filters(&state),
        Some((
            vec!["claude".into()],
            vec!["local".into()],
            vec!["settled".into()]
        ))
    );
}

// Pins: sessions-list#9
#[test]
fn folding_settled_sends_one_read() {
    let mut rail = Rail::default();
    let (first, _) = rail.changed().unwrap();
    rail.finish(
        first,
        Outcome::Ok(page(vec![
            test_entry("above", "/r"),
            test_entry("settled-row", "/r"),
        ])),
    );
    rail.look_at(Some("settled-row".into()));
    let (_, args) = rail.toggle_fold("settled".into(), "section:settled".into());
    assert_eq!(args["folded"], json!(["settled"]));
    assert!(rail.changed().is_none(), "the one read is out");
}

// Pins: sessions-list#9
#[test]
fn folding_a_group_keeps_its_header_in_the_page() {
    let mut rail = Rail::default();
    let (first, _) = rail.changed().unwrap();
    let header = RailEntry {
        kind: "project".into(),
        header: 1,
        ..test_entry("project:::/r", "/r")
    };
    rail.finish(
        first,
        Outcome::Ok(page(vec![
            test_entry("above", "/q"),
            header,
            test_entry("member", "/r"),
        ])),
    );
    // The header is at the top, so the row in view is its first member, which folding removes.
    rail.look_at(Some("member".into()));
    let (_, args) = rail.toggle_fold("::/r".into(), "project:::/r".into());
    assert_eq!(
        (&args["anchor"], &args["above"]),
        (&json!("project:::/r"), &json!(1))
    );
}

// Selecting the middle status pill must keep status and location separate in
// the rail query, including after saved filters are restored.
#[gpui::test]
fn status_then_location_picks_keep_their_query_meaning(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    pane.update(cx, |pane, cx| {
        pane.restore(
            vec!["codex".into()],
            vec!["remote".into()],
            vec!["done".into()],
            View::default(),
            cx,
        );
        pane.pills.update(cx, |pills, cx| {
            assert_eq!(pills.state.groups[1].label, "Status");
            assert_eq!(pills.state.groups[2].label, "Location");
            pills.state.toggle(1, "working");
            pills.state.toggle(2, "local");
            cx.emit(crate::core::ui::filter_pills::FilterPillsChanged);
        });
    });
    cx.run_until_parked();
    pane.read_with(cx, |pane, _| {
        assert_eq!(pane.rail.providers, ["codex"]);
        assert_eq!(pane.rail.statuses, ["working", "done"]);
        assert_eq!(pane.rail.locations, ["local", "remote"]);
    });
}
