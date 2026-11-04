//! The Board fixes from the UI check: the list stays at the top when you are at the top, a machine that comes back
//! rereads the expanded card, and each feed line shows its own question, answer or delivery status.

use serde_json::{Value, json};

use super::super::board_payload::BoardEvent;
use super::BoardState;
use super::tests::{page, question, task};

#[test]
fn saved_board_tabs_are_removed_without_losing_other_panels_or_the_selected_tab() {
    use super::super::board_pane::{BOARD, remove_saved_panel};
    use gpui_kit::component::dock::{DockAreaState, PanelInfo, PanelState};

    for active in 0..4 {
        let mut state = DockAreaState {
            version: Some(2),
            center: PanelState {
                panel_name: "TabPanel".into(),
                children: vec![
                    PanelState::new("Chat"),
                    PanelState::new(BOARD),
                    PanelState {
                        panel_name: "Editor".into(),
                        children: vec![],
                        info: PanelInfo::panel(json!({"path":"/repo/main.rs", "cursor":42})),
                    },
                    PanelState::new("Diff"),
                ],
                info: PanelInfo::tabs(active),
            },
            ..DockAreaState::default()
        };
        let editor = state.center.children[2].clone();
        remove_saved_panel(&mut state);
        assert_eq!(state.version, Some(2));
        assert_eq!(
            state
                .center
                .children
                .iter()
                .map(|panel| panel.panel_name.as_str())
                .collect::<Vec<_>>(),
            vec!["Chat", "Editor", "Diff"]
        );
        assert_eq!(state.center.children[1], editor);
        assert_eq!(state.center.info.active_index(), Some([0, 1, 1, 2][active]));
        let migrated = state.clone();
        remove_saved_panel(&mut state);
        assert_eq!(state, migrated);
    }
}

#[test]
fn saved_board_only_groups_drop_their_own_split_sizes_and_leave_other_docks_unchanged() {
    use super::super::board_pane::{BOARD, remove_saved_panel};
    use gpui_kit::component::dock::{
        DockAreaState, DockPlacement, DockState, PanelInfo, PanelState,
    };
    use gpui_kit::{Axis, px};
    let group = |panes: &[&str]| PanelState {
        panel_name: "TabPanel".into(),
        children: panes.iter().map(|name| PanelState::new(*name)).collect(),
        info: PanelInfo::tabs(0),
    };
    let mut state = DockAreaState {
        version: Some(2),
        center: PanelState {
            panel_name: "StackPanel".into(),
            children: vec![
                group(&["Chat"]),
                group(&[BOARD]),
                group(&["Editor", "Diff"]),
            ],
            info: PanelInfo::stack(vec![px(200.), px(300.), px(400.)], Axis::Horizontal),
        },
        left_dock: Some(DockState::new(
            group(&["Sessions", "Files"]),
            DockPlacement::Left,
            px(326.),
            true,
        )),
        right_dock: Some(DockState::new(
            group(&["Context", "History"]),
            DockPlacement::Right,
            px(320.),
            false,
        )),
        bottom_dock: Some(DockState::new(
            group(&[BOARD]),
            DockPlacement::Bottom,
            px(180.),
            true,
        )),
    };
    let (left, right) = (state.left_dock.clone(), state.right_dock.clone());
    remove_saved_panel(&mut state);
    assert_eq!(state.left_dock, left);
    assert_eq!(state.right_dock, right);
    assert_eq!(state.bottom_dock, None);
    assert_eq!(state.center.children.len(), 2);
    assert_eq!(state.center.info.sizes(), Some(&vec![px(200.), px(400.)]));
    assert_eq!(state.center.children[1], group(&["Editor", "Diff"]));
}

#[test]
fn saved_layout_load_migrates_board_only_and_leaves_an_unrelated_layout_exact() {
    use super::super::board_pane::BOARD;
    use crate::features::shell::default_layout::LAYOUT_VERSION;
    use crate::features::shell::layout_store::LayoutStore;
    use gpui_kit::component::dock::{DockAreaState, PanelInfo, PanelState};
    let mut state = DockAreaState {
        version: Some(LAYOUT_VERSION),
        center: PanelState {
            panel_name: "TabPanel".into(),
            children: vec![PanelState::new("Editor"), PanelState::new("Diff")],
            info: PanelInfo::tabs(1),
        },
        ..DockAreaState::default()
    };
    let unchanged = LayoutStore::layout_to_open(Some(serde_json::to_value(&state).unwrap()));
    assert_eq!(unchanged, state);
    state.center.children.insert(0, PanelState::new(BOARD));
    state.center.info = PanelInfo::tabs(2);
    let migrated = LayoutStore::layout_to_open(Some(serde_json::to_value(state).unwrap()));
    assert_eq!(migrated, unchanged);
    let only_board = DockAreaState {
        center: PanelState::new(BOARD),
        ..unchanged
    };
    let migrated = LayoutStore::layout_to_open(Some(serde_json::to_value(only_board).unwrap()));
    assert!(migrated.center.children.is_empty());
    assert_eq!(migrated.center.info.active_index(), Some(0));
}

#[gpui::test]
async fn board_button_and_palette_share_a_dropdown_that_releases_its_reader_on_dismiss(
    cx: &mut gpui::TestAppContext,
) {
    use super::super::OpenBoard;
    use super::super::board_pane::BoardPane;
    use crate::features::shell::default_layout::{LAYOUT_VERSION, default_state};
    use crate::features::shell::placeholder_pane::PlaceholderPane;
    use crate::features::shell::top_bar::TopBar;
    use gpui_kit::component::dock::DockArea;
    use gpui_kit::{AppContext as _, Entity, Modifiers, point, px, size};
    use std::cell::RefCell;
    use std::rc::Rc;

    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        PlaceholderPane::register_all(cx);
        BoardPane::register(cx);
        crate::core::state::selected_session::SelectedSession::init(cx);
    });
    let held: Rc<RefCell<Option<Entity<DockArea>>>> = Rc::default();
    let capture = held.clone();
    let (bar, cx) = cx.add_window_view(move |window, cx| {
        let dock =
            cx.new(|cx| DockArea::new("board-dropdown-test", Some(LAYOUT_VERSION), window, cx));
        dock.update(cx, |dock, cx| _ = dock.load(default_state(), window, cx));
        *capture.borrow_mut() = Some(dock.clone());
        TopBar::new(dock, cx)
    });
    let dock = held.borrow_mut().take().unwrap();
    cx.simulate_resize(size(px(1400.), px(900.)));
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let layout = cx.update(|_, cx| dock.read(cx).dump(cx));
    assert!(cx.debug_bounds("board-dropdown-body").is_none());
    assert!(cx.read(|cx| bar.read(cx).board_reader().is_none()));
    let button = cx
        .debug_bounds("board-dropdown-button")
        .expect("Board button");
    cx.simulate_click(button.center(), Modifiers::none());
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let body = cx
        .debug_bounds("board-dropdown-body")
        .expect("Board dropdown");
    assert!(body.top() >= button.bottom());
    assert!(body.size.width <= px(900.));
    assert!(body.right() <= px(1392.));
    assert!(body.bottom() <= px(864.));
    let reader = cx.read(|cx| bar.read(cx).board_reader().unwrap());
    cx.update(|window, cx| window.dispatch_action(Box::new(OpenBoard), cx));
    cx.run_until_parked();
    assert_eq!(
        cx.read(|cx| bar.read(cx).board_reader().unwrap().entity_id()),
        reader.entity_id()
    );
    assert_eq!(cx.update(|_, cx| dock.read(cx).dump(cx)), layout);
    cx.simulate_keystrokes("escape");
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("board-dropdown-body").is_none());
    assert!(cx.read(|_| reader.upgrade().is_none()));
    cx.simulate_click(button.center(), Modifiers::none());
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("board-dropdown-body").is_some());
    cx.simulate_click(point(px(1396.), px(896.)), Modifiers::none());
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(cx.debug_bounds("board-dropdown-body").is_none());
    assert_eq!(cx.update(|_, cx| dock.read(cx).dump(cx)), layout);
    cx.simulate_click(button.center(), Modifiers::none());
    cx.run_until_parked();
    let reader = cx.read(|cx| bar.read(cx).board_reader().unwrap());
    // The expanded card this session created, which is all the Board opens a session from.
    cx.update(|_, cx| {
        reader.upgrade().unwrap().update(cx, |pane, _| {
            let mut cards = super::tests::page(&["task"], None);
            cards.items[0].task.creator_session_id = "selected".into();
            pane.state.items = cards.items;
            pane.state.selected = Some(("local".into(), "task".into()));
        })
    });
    cx.update(|window, cx| {
        reader
            .upgrade()
            .unwrap()
            .update(cx, |pane, cx| pane.open_session("selected", window, cx))
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(
        cx.debug_bounds("board-dropdown-body").is_some(),
        "an unhandled action must leave the Board open"
    );
    let mut nav = cx.update(|window, cx| {
        crate::features::session::view::session_pane::test_navigation::NavigationFixture::new(
            false, window, cx,
        )
    });
    nav.ready(cx).await;
    cx.update(|window, cx| {
        reader
            .upgrade()
            .unwrap()
            .update(cx, |pane, cx| pane.open_session("selected", window, cx))
    });
    nav.opened("selected", cx).await;
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert_eq!(
        cx.read(
            |cx| crate::core::state::selected_session::SelectedSession::global(cx)
                .read(cx)
                .session_id()
                .map(String::from)
        ),
        Some("selected".into())
    );
    assert!(cx.debug_bounds("board-dropdown-body").is_none());
    assert!(cx.read(|_| reader.upgrade().is_none()));
    cx.simulate_click(button.center(), Modifiers::none());
    cx.run_until_parked();
    let reader = cx.read(|cx| bar.read(cx).board_reader().unwrap());
    cx.update(|_, cx| {
        reader.upgrade().unwrap().update(cx, |pane, _| {
            let mut cards = super::tests::page(&["task"], None);
            cards.items[0].task.creator_session_id = "selected".into();
            pane.state.items = cards.items;
            pane.state.selected = Some(("local".into(), "task".into()));
        })
    });
    nav.forget_results();
    cx.update(|window, cx| {
        reader
            .upgrade()
            .unwrap()
            .update(cx, |pane, cx| pane.open_session("selected", window, cx))
    });
    nav.opened("selected", cx).await;
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(
        cx.debug_bounds("board-dropdown-body").is_none(),
        "an already accepted selection also dismisses the Board"
    );
    assert_eq!(cx.update(|_, cx| dock.read(cx).dump(cx)), layout);
}

#[test]
fn board_refresh_stays_at_top_but_preserves_a_scrolled_anchor() {
    use gpui_kit::{ListOffset, px};
    for (index, offset, expected) in [(0, 0., None), (0, 12., Some("a")), (1, 0., Some("b"))] {
        let mut state = BoardState::default();
        let read = state.changed().unwrap();
        state.apply_page(read.epoch, page(&["a", "b"], None));
        let anchor = state.scroll_anchor(ListOffset {
            item_ix: index,
            offset_in_item: px(offset),
        });
        assert_eq!(anchor.as_ref().map(|(_, key)| key.as_str()), expected);
        let read = state.changed().unwrap();
        let mut fresh = page(&["needs-you", "a", "b"], None);
        fresh.items[0].attention_group = 0;
        state.apply_page(read.epoch, fresh);
        let top = anchor
            .and_then(|key| {
                state.items.iter().position(|item| {
                    (item.task.machine_id.clone(), item.task.task_key.clone()) == key
                })
            })
            .unwrap_or(0);
        assert_eq!(
            top,
            expected.map_or(0, |key| if key == "a" { 1 } else { 2 })
        );
    }
}

#[test]
fn board_remote_reconnect_rereads_only_the_expanded_source() {
    let mut state = BoardState::default();
    assert!(
        state
            .machines_changed(vec!["Workbox Test".into()])
            .is_none()
    );
    let read = state.select("Workbox Test", "t1").unwrap();
    assert!(
        state
            .machines_changed(vec!["Workbox Test".into(), "other".into()])
            .is_none()
    );
    assert_eq!(state.machines_changed(vec!["other".into()]), Some(read));
    assert!(state.machines_changed(vec![]).is_none());
    state.select("Workbox Test", "t1");
    state.machines_changed(vec!["Workbox Test".into()]);
    assert!(state.machines_changed(vec![]).is_none());
    state.select("local", "t1");
    state.machines_changed(vec!["Workbox Test".into()]);
    assert!(state.machines_changed(vec![]).is_none());
}

#[test]
fn board_feed_uses_each_events_question_answer_and_delivery() {
    use super::super::board_feed::event_text;
    for (kind, answer, delivery, expected) in [
        ("question_asked", Value::Null, "none", "Merge now?"),
        (
            "answer_recorded",
            json!({"kind":"text", "text":"Ship it"}),
            "pending",
            "Ship it",
        ),
        (
            "answer_recorded",
            json!({"kind":"approve", "text":null}),
            "pending",
            "Approved",
        ),
        (
            "answer_recorded",
            json!({"kind":"deny", "text":null}),
            "pending",
            "Denied",
        ),
        (
            "answer_delivered",
            json!({"kind":"text", "text":"Ship it"}),
            "delivered",
            "Delivered to the agent.",
        ),
        (
            "answer_delivery_failed",
            json!({"kind":"text", "text":"Ship it"}),
            "delivery_failed",
            "Not delivered; the answer is kept.",
        ),
        ("task_reported", Value::Null, "none", "Working"),
    ] {
        let mut q = question("local", 1, answer);
        q["deliveryState"] = json!(delivery);
        let event: BoardEvent = serde_json::from_value(json!({"kind":kind, "occurredAt":5000,
            "task":task("t1", "running"), "questions":[q]}))
        .unwrap();
        assert_eq!(event_text(&event), expected, "{kind}");
    }
}
