//! Terminal view return through the real dock lifecycle.

use super::*;
use gpui_kit::AppContext as _;

// Pins: terminal#5, terminal#9
#[gpui::test]
async fn the_real_dock_restores_the_remote_terminal_when_reopened(cx: &mut TestAppContext) {
    use gpui_kit::component::dock::{
        DockArea, DockPlacement, DockState, PanelInfo, PanelState, panel_handle, register_panel,
    };
    let mut open = open(cx, Terminals::default(), true).await;
    open.until("a's shell", |_, pane| following(pane)).await;
    pick("b", open.cx);
    open.until("b's shell", |t, pane| {
        following(pane) && t.of("start").len() == 2
    })
    .await;
    let pane = open.pane.clone();
    let dock = open.cx.update(|window, cx| {
        crate::features::shell::placeholder_pane::PlaceholderPane::register_all(cx);
        register_panel(cx, "Terminal", move |_, _, _| panel_handle(pane.clone()));
        let dock = cx.new(|cx| DockArea::new("terminal-reopen", None, window, cx));
        let mut state = crate::features::shell::default_layout::default_state();
        state.bottom_dock = Some(DockState::new(
            PanelState {
                panel_name: "TabPanel".into(),
                children: vec![PanelState::new("Terminal")],
                info: PanelInfo::tabs(0),
            },
            DockPlacement::Bottom,
            px(300.),
            true,
        ));
        dock.update(cx, |dock, cx| {
            dock.load(state, window, cx).unwrap();
        });
        dock
    });
    open.cx.run_until_parked();
    for _ in 0..5 {
        open.cx.update(|window, cx| {
            dock.update(cx, |dock, cx| {
                dock.toggle_dock(DockPlacement::Bottom, window, cx)
            })
        });
        open.until("hidden", |_, pane| pane.shown == Shown::Nothing)
            .await;
        let reads = open.terminals.lock().unwrap().of("list").len();
        open.cx.update(|window, cx| {
            dock.update(cx, |dock, cx| {
                dock.toggle_dock(DockPlacement::Bottom, window, cx)
            })
        });
        open.until("remote shell restored by dock", |t, pane| {
            following(pane) && t.of("list").len() > reads
        })
        .await;
    }
    assert_eq!(open.terminals.lock().unwrap().of("start").len(), 2);
}
