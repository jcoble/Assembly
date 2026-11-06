use super::*;
use gpui_kit::component::dock::{DockPlacement, DockState};
use gpui_kit::px;
use serde_json::json;
fn captured() -> LegacyFileCapture {
    LegacyFileCapture {
        legacy_session: "old-alias".into(),
        path: "src/λ.rs".into(),
        owner: Some(crate::core::state::workspace_panes::WorkspaceKey {
            machine: "actual-owner".into(),
            session_id: "resolved-session".into(),
            checkout_root: "/checkout".into(),
        }),
        document: None,
        version: None,
        entry: json!({"path":"src/λ.rs","baseRevision":"r","draft":"actual λ\r\n"}),
        active: true,
    }
}
fn layout() -> DockAreaState {
    let file = PanelState {
        panel_name: "File".into(),
        children: vec![],
        info: PanelInfo::panel(json!({"session":"old-alias","path":"src/λ.rs"})),
    };
    DockAreaState {
        version: Some(2),
        center: PanelState {
            panel_name: "tabs".into(),
            children: vec![PanelState::new("Chat"), file, PanelState::new("Changes")],
            info: PanelInfo::tabs(2),
        },
        left_dock: Some(DockState::new(
            PanelState::new("Files"),
            DockPlacement::Left,
            px(326.),
            true,
        )),
        right_dock: Some(DockState::new(
            PanelState::new("Terminal"),
            DockPlacement::Right,
            px(350.),
            false,
        )),
        bottom_dock: None,
    }
}
#[test]
fn confirmed_file_references_leave_the_old_layout_without_losing_other_metadata() {
    let original = layout();
    let migrated = prune(&original, &[captured()]).unwrap();
    assert_eq!(migrated.version, original.version);
    assert_eq!(migrated.left_dock, original.left_dock);
    assert_eq!(migrated.right_dock, original.right_dock);
    assert_eq!(
        migrated
            .center
            .children
            .iter()
            .map(|panel| panel.panel_name.as_str())
            .collect::<Vec<_>>(),
        vec!["Chat", "Changes"]
    );
    assert_eq!(
        migrated.center.info.active_index(),
        Some(1),
        "same retained active pane after removed preceding File"
    );
    let json = serde_json::to_value(&migrated).unwrap();
    assert_eq!(
        serde_json::from_value::<DockAreaState>(json).unwrap(),
        migrated
    );
    assert_eq!(
        original.center.children.len(),
        3,
        "preflight never mutates the input"
    );
}
#[test]
fn dormant_unknown_or_duplicate_file_references_preserve_the_entire_old_layout() {
    for kind in ["unknown", "duplicate", "malformed"] {
        let mut original = layout();
        let mut file = original.center.children[1].clone();
        if kind == "unknown" {
            file.info = PanelInfo::panel(json!({"session":"other-source","path":"src/λ.rs"}));
        }
        if kind == "malformed" {
            file.info = PanelInfo::panel(
                json!({"session":"old-alias","path":"src/λ.rs","draft":"unmigrated bytes"}),
            );
        }
        if kind == "duplicate" {
            original.center.children.push(file);
        } else {
            original.center.children[1] = file;
        }
        let before = serde_json::to_value(&original).unwrap();
        assert!(prune(&original, &[captured()]).is_err());
        assert_eq!(serde_json::to_value(&original).unwrap(), before);
    }
}

#[test]
fn split_size_entries_follow_retained_children_and_corrupt_sizes_refuse_the_transform() {
    let mut original = layout();
    original.center.info = PanelInfo::Stack {
        sizes: vec![px(100.), px(200.), px(300.)],
        axis: 0,
    };
    let migrated = prune(&original, &[captured()]).unwrap();
    assert_eq!(
        migrated.center.info,
        PanelInfo::Stack {
            sizes: vec![px(100.), px(300.)],
            axis: 0
        }
    );
    original.center.info = PanelInfo::Stack {
        sizes: vec![px(100.)],
        axis: 0,
    };
    let before = original.clone();
    assert!(prune(&original, &[captured()]).is_err());
    assert_eq!(original, before);
}
fn outside_file() -> PanelState {
    PanelState {
        panel_name: "File".into(),
        children: vec![],
        info: PanelInfo::panel(json!({"outside": true, "machine": "box", "path": "/etc/hosts"})),
    }
}
#[test]
fn an_outside_file_is_stripped_or_pruned_by_its_tab_key_and_never_called_an_old_file() {
    let mut original = layout();
    original.center.children.push(outside_file());
    let stripped = strip_outside(&original).unwrap();
    assert_eq!(stripped.center.children.len(), 3);
    assert_eq!(stripped.center.children[1].panel_name, "File");
    let alias = FileView::session_tab_key("old-alias", "src/λ.rs");
    let PanelInfo::Panel(fields) = outside_file().info else {
        unreachable!()
    };
    let outside = saved_alias(fields.as_object().unwrap()).unwrap();
    let pruned = prune_refs(&original, &[alias, outside]).unwrap();
    assert_eq!(pruned.center.children.len(), 2);

    let root = DockAreaState {
        center: outside_file(),
        ..layout()
    };
    assert_eq!(
        strip_outside(&root).unwrap_err(),
        "An outside file fills a whole dock, so the layout wasn't saved."
    );
}
