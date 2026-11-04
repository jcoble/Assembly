//! The panel as it draws: only what shows, its holds, and the top bar's dropdown.
use gpui::TestAppContext;
use gpui_kit::{
    AppContext as _, Entity, IntoElement as _, Modifiers, VisualTestContext, point, px, size,
};
use serde_json::{Value, json};

use super::{BoardMemory, BoardPanel};
use crate::core::engine::{EngineItem, EngineKey};
use crate::features::board::board_prefs::{BoardTab, SessionTab};
use crate::features::board::board_queries::BoardData;

fn item(kind: &str, id: &str, mut value: Value) -> EngineItem {
    value["entityKind"] = json!(kind);
    value["machine"] = json!("m");
    value["id"] = json!(id);
    value["version"] = json!(1);
    EngineItem {
        key: EngineKey {
            machine: "m".into(),
            kind: kind.into(),
            id: id.into(),
        },
        version: 1,
        value,
    }
}

fn need(id: &str, state: &str) -> EngineItem {
    item(
        "needs_me",
        id,
        json!({"sessionId":"s","kind":"question","state":state,"text":format!("Question {id}"),"createdMs":1}),
    )
}

fn run(id: &str, state: &str) -> EngineItem {
    item(
        "run",
        id,
        json!({"state":state,"activeStepIds":[],"paused":false,"reference":false,"title":id}),
    )
}

fn key(kind: &str, id: &str) -> EngineKey {
    EngineKey {
        machine: "m".into(),
        kind: kind.into(),
        id: id.into(),
    }
}

fn open_panel(
    cx: &mut TestAppContext,
    viewport_height: f32,
) -> (Entity<BoardPanel>, &mut VisualTestContext) {
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
    });
    let (panel, cx) = cx.add_window_view(|_, cx| BoardPanel::new(BoardMemory::default(), cx));
    cx.simulate_resize(size(px(800.), px(viewport_height)));
    cx.run_until_parked();
    (panel, cx)
}

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

#[gpui::test]
async fn needs_you_list_renders_only_visible_rows(cx: &mut TestAppContext) {
    let (panel, cx) = open_panel(cx, 600.);
    let needs = (0..225)
        .map(|index| need(&index.to_string(), "open"))
        .collect::<Vec<_>>();
    cx.update(|_, cx| {
        panel.update(cx, |panel, cx| {
            panel.test_set_data(BoardData::of(&needs, &[], &[], &[]), cx);
            panel.test_reset_built();
        })
    });
    draw(cx);
    let (built, ..) = cx.read(|cx| panel.read(cx).test_state());
    assert!(built > 0, "it drew rows");
    assert!(built <= 40, "225 questions in 600 px built {built} rows");
}

#[gpui::test]
async fn clock_held_only_while_running_row_visible(cx: &mut TestAppContext) {
    let (panel, cx) = open_panel(cx, 600.);
    let held = |cx: &mut VisualTestContext| cx.read(|cx| panel.read(cx).test_state().1);
    cx.update(|_, cx| panel.update(cx, |panel, cx| panel.test_show(BoardTab::Workflows, cx)));
    draw(cx);
    assert!(!held(cx), "nothing's counting on an empty tab");
    let running = BoardData::of(&[], &[], &[run("going", "running")], &[]);
    cx.update(|_, cx| panel.update(cx, |panel, cx| panel.test_set_data(running, cx)));
    draw(cx);
    assert!(held(cx), "a running row counts");
    let finished = BoardData::of(&[], &[], &[run("going", "succeeded")], &[]);
    cx.update(|_, cx| panel.update(cx, |panel, cx| panel.test_set_data(finished, cx)));
    draw(cx);
    cx.update(|_, cx| panel.update(cx, |_, cx| cx.notify()));
    draw(cx);
    assert!(!held(cx), "and it goes once nothing on screen counts");
}

#[gpui::test]
async fn list_remeasures_on_moved(cx: &mut TestAppContext) {
    let (panel, cx) = open_panel(cx, 600.);
    let data = BoardData::of(&[], &[], &[run("a", "running"), run("b", "running")], &[]);
    cx.update(|_, cx| {
        panel.update(cx, |panel, cx| {
            panel.test_show(BoardTab::Workflows, cx);
            panel.test_set_data(data, cx);
        })
    });
    draw(cx);
    let list = cx.read(|cx| panel.read(cx).test_list());
    let second =
        |list: &gpui_kit::ListState| list.bounds_for_item(2).expect("second run's row").top();
    let shut = second(&list);
    cx.update(|_, cx| panel.update(cx, |panel, cx| panel.test_open(&key("run", "a"), cx)));
    // The motion runs on frames, and each step tells the list.
    for _ in 0..40 {
        cx.executor()
            .advance_clock(std::time::Duration::from_millis(16));
        draw(cx);
    }
    assert!(
        second(&list) > shut,
        "the row under an opening one moves down"
    );
}

#[gpui::test]
async fn closing_card_keeps_its_own_body(cx: &mut TestAppContext) {
    let (panel, cx) = open_panel(cx, 600.);
    let data = BoardData::of(&[], &[], &[run("a", "running"), run("b", "running")], &[]);
    cx.update(|_, cx| {
        panel.update(cx, |panel, cx| {
            panel.test_show(BoardTab::Workflows, cx);
            panel.test_set_data(data, cx);
            panel.test_open(&key("run", "a"), cx);
        })
    });
    draw(cx);
    assert!(
        cx.read(|cx| panel.read(cx).test_run_card_held(&key("run", "a"))),
        "an open row has its card"
    );
    cx.update(|_, cx| panel.update(cx, |panel, cx| panel.test_open(&key("run", "b"), cx)));
    draw(cx);
    assert!(cx.read(|cx| panel.read(cx).test_run_card_held(&key("run", "b"))));
    cx.update(|_, cx| panel.update(cx, |_, cx| cx.notify()));
    draw(cx);
    assert!(
        cx.read(|cx| panel.read(cx).test_run_card_held(&key("run", "a"))),
        "and keeps it through its closing motion"
    );
}

#[gpui::test]
async fn filtered_out_card_is_released_and_selected_stays_open(cx: &mut TestAppContext) {
    let (panel, cx) = open_panel(cx, 600.);
    let both = BoardData::of(&[need("a", "open"), need("b", "open")], &[], &[], &[]);
    cx.update(|_, cx| panel.update(cx, |panel, cx| panel.test_set_data(both, cx)));
    draw(cx);
    let rows = cx.read(|cx| panel.read(cx).test_questions());
    let (a, b): (gpui_kit::SharedString, gpui_kit::SharedString) = ("m:a".into(), "m:b".into());
    let drops = cx.read(|cx| rows.read(cx).drops().clone());
    let open = |key: &gpui_kit::SharedString, drops: &Entity<_>, cx: &mut VisualTestContext| {
        let drops: &Entity<crate::core::ui::row_drop_down::RowDropDowns> = drops;
        cx.read(|cx| drops.read(cx).is_open(key))
    };
    cx.update(|_, cx| {
        drops.update(cx, |drops, cx| {
            drops.toggle(a.clone(), |_, _| gpui_kit::Empty.into_any_element(), cx)
        })
    });
    draw(cx);
    assert!(open(&a, &drops, cx));
    let without_b = BoardData::of(&[need("a", "open")], &[], &[], &[]);
    cx.update(|_, cx| panel.update(cx, |panel, cx| panel.test_set_data(without_b, cx)));
    draw(cx);
    assert!(open(&a, &drops, cx), "the open row stays open");
    let without_a = BoardData::of(&[need("b", "open")], &[], &[], &[]);
    cx.update(|_, cx| panel.update(cx, |panel, cx| panel.test_set_data(without_a, cx)));
    draw(cx);
    assert!(!open(&a, &drops, cx), "and goes once it's not listed");
    assert!(!open(&b, &drops, cx));
}

#[gpui::test]
async fn only_selected_tab_body_is_built(cx: &mut TestAppContext) {
    let (panel, cx) = open_panel(cx, 800.);
    let data = BoardData::of(
        &[],
        &[item(
            "session",
            "s",
            json!({"provider":"claude","folder":"/w","state":"running","activityState":"working","epoch":1,"runId":"r"}),
        )],
        &[run("r", "running")],
        &[],
    );
    let row = key("session", "s");
    cx.update(|_, cx| {
        panel.update(cx, |panel, cx| {
            panel.test_show(BoardTab::Sessions, cx);
            panel.test_set_data(data, cx);
            panel.remember_session_tab("m:s".into(), SessionTab::Prs, cx);
            panel.test_open(&row, cx);
        })
    });
    for _ in 0..4 {
        cx.executor()
            .advance_clock(std::time::Duration::from_millis(16));
        draw(cx);
    }
    let tabs = cx
        .read(|cx| panel.read(cx).test_session_tabs(&row))
        .expect("the open row's tabs");
    assert_eq!(
        cx.read(|cx| tabs.read(cx).test_held()),
        (false, false),
        "PRs needs neither the workflow nor the sub-agents"
    );
}

#[gpui::test]
async fn board_button_and_palette_share_a_dropdown_that_releases_its_reader_on_dismiss(
    cx: &mut TestAppContext,
) {
    use crate::features::board::ToggleBoard;
    use crate::features::shell::default_layout::{LAYOUT_VERSION, default_state};
    use crate::features::shell::placeholder_pane::PlaceholderPane;
    use crate::features::shell::top_bar::TopBar;
    use gpui_kit::component::dock::DockArea;
    use std::cell::RefCell;
    use std::rc::Rc;

    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        PlaceholderPane::register_all(cx);
        BoardPanel::register(cx);
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
    draw(cx);
    let layout = cx.update(|_, cx| dock.read(cx).dump(cx));
    assert!(cx.debug_bounds("board-dropdown-body").is_none());
    assert!(cx.read(|cx| bar.read(cx).board_reader().is_none()));
    let button = cx
        .debug_bounds("board-dropdown-button")
        .expect("Board button");
    cx.simulate_click(button.center(), Modifiers::none());
    draw(cx);
    let body = cx.debug_bounds("board-panel").expect("Board panel");
    assert!(body.top() >= button.bottom());
    assert!(body.size.width <= px(720.));
    assert!(body.bottom() <= px(900.));
    let reader = cx.read(|cx| bar.read(cx).board_reader().unwrap());
    cx.update(|window, cx| window.dispatch_action(Box::new(ToggleBoard), cx));
    draw(cx);
    assert!(
        cx.read(|cx| bar.read(cx).board_reader().is_none()),
        "the palette's action closes what's open"
    );
    assert!(
        cx.read(|_| reader.upgrade().is_none()),
        "and closing releases it"
    );
    assert_eq!(
        cx.update(|_, cx| dock.read(cx).dump(cx)),
        layout,
        "the panes never change"
    );
    cx.update(|window, cx| window.dispatch_action(Box::new(ToggleBoard), cx));
    draw(cx);
    let reader = cx.read(|cx| bar.read(cx).board_reader().unwrap());
    assert!(
        cx.debug_bounds("board-panel").is_some(),
        "and it opens the same drop-down"
    );
    cx.simulate_keystrokes("escape");
    draw(cx);
    assert!(cx.debug_bounds("board-panel").is_none(), "Escape closes it");
    assert!(cx.read(|_| reader.upgrade().is_none()));
    cx.simulate_click(button.center(), Modifiers::none());
    draw(cx);
    assert!(cx.debug_bounds("board-panel").is_some());
    cx.simulate_click(point(px(1396.), px(896.)), Modifiers::none());
    draw(cx);
    assert!(
        cx.debug_bounds("board-panel").is_none(),
        "an outside click closes it"
    );
    assert_eq!(cx.update(|_, cx| dock.read(cx).dump(cx)), layout);
}
