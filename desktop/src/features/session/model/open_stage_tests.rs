use serde_json::json;
use super::open_stage::OpenStage;
use super::test_rows::row;
use crate::features::session::rows::row_page::RowPage;

fn page(rows: Vec<crate::features::session::rows::row::Row>) -> RowPage {
    RowPage { rows, has_before: false, active_run_id: Some("run".into()) }
}

#[test]
fn first_page_and_pushed_tail_are_prepared_without_touching_the_outgoing_view() {
    let mut outgoing = super::session_view::SessionView::default();
    outgoing.apply(&row(1, "old", Some("old"), "user", json!({"text":"outgoing"})));
    let mut stage = OpenStage::new("t".into());
    let ticket = stage.jump(4).unwrap();
    let mut pushed = row(5, "run", Some("new"), "user", json!({"text":"tail"}));
    pushed.prev_seq = Some(4);
    stage.push(pushed).unwrap();
    assert!(!stage.ready());
    assert!(stage.install(ticket, page(vec![row(4,"run",Some("first"),"user",json!({"text":"page"}))])).unwrap());
    assert_eq!(outgoing.items.len(), 1);
    let prepared = stage.take().unwrap();
    assert_eq!(prepared.view.items.len(), 2);
    assert_eq!(prepared.view.newest, 5);
    assert_eq!(prepared.known_run.as_deref(), Some("run"));
}

#[test]
fn a_reconnect_jump_invalidates_the_old_first_page_even_for_the_same_session() {
    let mut stage = OpenStage::new("t".into());
    let old = stage.jump(4).unwrap();
    let current = stage.jump(9).unwrap();
    assert!(!stage.install(old, page(vec![])).unwrap());
    assert!(!stage.ready());
    assert!(stage.install(current, page(vec![])).unwrap());
    assert!(stage.ready());
}

#[test]
fn foreign_rows_or_a_gap_refuse_the_prepared_target() {
    let mut stage = OpenStage::new("t".into());
    let ticket = stage.jump(3).unwrap();
    let mut foreign = row(4,"run",Some("x"),"user",json!({"text":"wrong"}));
    foreign.session_id = "other".into();
    assert!(stage.install(ticket, page(vec![foreign])).is_err());
    assert!(!stage.ready());
    let ticket = stage.jump(3).unwrap();
    stage.install(ticket, page(vec![])).unwrap();
    let mut gap = row(5,"run",Some("x"),"user",json!({"text":"gap"}));
    gap.prev_seq = Some(4);
    assert!(stage.push(gap).is_err());
    assert!(!stage.ready());
}

#[test]
fn a_page_read_after_publication_does_not_regress_when_held_older_rows_replay() {
    let mut stage = OpenStage::new("t".into());
    let ticket = stage.jump(3).unwrap();
    let mut older = row(4,"run",Some("x"),"thinking",json!({"text":"old","done":false}));
    older.prev_seq = Some(3);
    stage.push(older).unwrap();
    stage.install(ticket, page(vec![row(5,"run",Some("x"),"thinking",json!({"text":"new","done":true}))])).unwrap();
    assert!(!stage.ready(), "a page can include a newer compacted row before its push arrives");
    let mut published = row(5,"run",Some("x"),"thinking",json!({"text":"new","done":true}));
    published.prev_seq = Some(4);
    stage.push(published).unwrap();
    let prepared = stage.take().unwrap();
    let super::item::ItemKind::Thinking {text,done} = &prepared.view.items[0].kind else {panic!()};
    assert_eq!(text,"new");
    assert!(*done);
}

#[test]
fn a_guard_that_outlives_the_bounded_pushed_tail_must_prepare_again() {
    let mut stage = OpenStage::new("t".into());
    let ticket = stage.jump(0).unwrap();
    stage.install(ticket, page(vec![])).unwrap();
    for seq in 1..=500 {
        let mut next = row(seq,"run",Some("x"),"thinking",json!({"text":"work","done":false}));
        next.prev_seq = Some(seq-1);
        stage.push(next).unwrap();
    }
    let mut extra = row(501,"run",Some("x"),"thinking",json!({"text":"work","done":false}));
    extra.prev_seq = Some(500);
    assert!(stage.push(extra).is_err());
    assert!(stage.take().is_err());
}

#[test]
fn a_terminal_watch_refusal_invalidates_an_already_prepared_first_page() {
    let mut stage=OpenStage::new("t".into());let ticket=stage.jump(0).unwrap();
    stage.install(ticket,page(vec![])).unwrap();assert!(stage.ready());
    stage.invalidate();
    assert!(!stage.ready());assert!(stage.take().is_err());
    assert!(stage.install(ticket,page(vec![])).is_err());
}
