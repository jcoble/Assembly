//! The pane drawn whole, in each of the states a person meets. There's no backend here, so a test hands it what the
//! backend would have pushed.
use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};

use super::TasksPane;
use crate::features::tasks::task_export::*;
use crate::features::tasks::tasks_state::PAGE_SIZE as PAGE;

const PAGE_SIZE: usize = PAGE as usize;

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.run_until_parked();
}

fn open(cx: &mut TestAppContext) -> (Entity<TasksPane>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(crate::inline_field::init);
    let (pane, cx) = cx.add_window_view(TasksPane::new);
    draw(cx);
    (pane, cx)
}

fn settings(connected: bool, generation: i64) -> NotionSettingsView {
    NotionSettingsView {
        data_source_id: if connected { "s".into() } else { String::new() },
        has_token: connected,
        generation,
        state: "ready".into(),
        error: None,
    }
}

fn row(index: usize) -> NotionTaskRow {
    NotionTaskRow {
        source_id: "s".into(),
        page_id: format!("11111111-1111-1111-1111-{index:012}"),
        title: format!("Task {index}"),
        key_number: index as i64,
        task_key: format!("TSK-{index}"),
        project_id: "assembly".into(),
        project_title: "Assembly".into(),
        status: "In progress".into(),
        priority: "High".into(),
        assignee: String::new(),
        due: None,
        url: String::new(),
        fetched_ms: 1,
    }
}

fn page(from: usize, count: usize, has_more: bool) -> NotionTaskPage {
    NotionTaskPage {
        rows: (from..from + count).map(row).collect(),
        has_more,
        statuses: vec!["In progress".into(), "Done".into()],
        priorities: vec!["High".into()],
        projects: vec![NotionProjectOption {
            id: "assembly".into(),
            title: "Assembly".into(),
        }],
        fetched_ms: 1,
        truncated: false,
        generation: 1,
        revision: 1,
    }
}

/// Connects the pane and gives it its first page.
fn listed(pane: &Entity<TasksPane>, cx: &mut VisualTestContext) {
    cx.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.settings_pushed(settings(true, 1), cx);
            let (token, _) = pane.state.first_page();
            pane.page_arrived(token, Ok(page(0, PAGE_SIZE, true)), false, cx);
        });
    });
    draw(cx);
}

#[gpui::test]
fn with_no_connection_it_draws_the_card_and_says_so(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    cx.update(|_, cx| pane.update(cx, |pane, cx| pane.settings_pushed(settings(false, 0), cx)));
    draw(cx);
    assert!(cx.read(|cx| pane.read(cx).state.show_setup()));
    assert!(cx.read(|cx| pane.read(cx).state.rows.is_empty()));
}

#[gpui::test]
fn a_page_draws_and_the_next_one_keeps_the_rows_already_there(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    listed(&pane, cx);
    assert_eq!(cx.read(|cx| pane.read(cx).list.item_count()), PAGE_SIZE);
    cx.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            let (token, _) = pane.state.next_page().expect("there is more");
            pane.page_arrived(token, Ok(page(PAGE_SIZE, 3, false)), true, cx);
        });
    });
    draw(cx);
    assert_eq!(cx.read(|cx| pane.read(cx).list.item_count()), PAGE_SIZE + 3);
    assert!(cx.read(|cx| !pane.read(cx).state.has_more));
}

#[gpui::test]
fn a_row_opens_its_page_and_back_returns_to_the_list(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    listed(&pane, cx);
    cx.update(|window, cx| pane.update(cx, |pane, cx| pane.open_row(2, window, cx)));
    draw(cx);
    assert!(cx.read(|cx| pane.read(cx).detail.is_some()));
    cx.update(|window, cx| pane.update(cx, |pane, cx| pane.go_back(window, cx)));
    draw(cx);
    assert!(cx.read(|cx| pane.read(cx).detail.is_none()));
    assert_eq!(cx.read(|cx| pane.read(cx).list.item_count()), PAGE_SIZE);
}

#[gpui::test]
fn another_connection_drops_what_the_old_one_showed(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    listed(&pane, cx);
    cx.update(|window, cx| pane.update(cx, |pane, cx| pane.open_row(0, window, cx)));
    cx.update(|_, cx| pane.update(cx, |pane, cx| pane.settings_pushed(settings(true, 2), cx)));
    draw(cx);
    let (rows, detail, drawn) = cx.read(|cx| {
        let pane = pane.read(cx);
        (
            pane.state.rows.len(),
            pane.detail.is_some(),
            pane.list.item_count(),
        )
    });
    assert_eq!((rows, detail, drawn), (0, false, 0));
}
