use serde_json::json;

#[test]
fn saved_board_tabs_are_removed_without_losing_other_panels_or_the_selected_tab() {
    use super::{BOARD, remove_saved_panel};
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
    use super::{BOARD, remove_saved_panel};
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
    use super::BOARD;
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
