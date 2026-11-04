use super::{CenterPanes, DrawerCodec, WorkspaceKey, WorkspacePanes};
use crate::features::shell::{default_layout::default_state, placeholder_pane::PlaceholderPane};
use gpui::TestAppContext;
use gpui_kit::component::dock::{DockArea, DockPlacement};
use gpui_kit::{AppContext as _, Context, IntoElement, Render, Window, div};
use serde_json::json;
struct Host;
impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
#[gpui::test]
fn drawer_selection_restores_existing_identity_without_opening_device_local_drawer_geometry(
    cx: &mut TestAppContext,
) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        PlaceholderPane::register_all(cx);
        WorkspacePanes::register(Box::new(DrawerCodec), cx).unwrap();
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("workspace-drawer", Some(2), window, cx));
        CenterPanes::init(&dock, cx);
        dock.update(cx, |dock, cx| {
            dock.load(default_state(), window, cx).unwrap()
        });
        WorkspacePanes::set_key(
            WorkspaceKey {
                machine: "actual-owner".into(),
                session_id: "session".into(),
                checkout_root: "/checkout".into(),
            },
            cx,
        );
        assert!(!dock.read(cx).is_dock_open(DockPlacement::Right));
        WorkspacePanes::restore(&json!({"version":1,"drawerTab":"Run"}), window, cx).unwrap();
        assert_eq!(
            WorkspacePanes::capture(window, cx).unwrap()["drawerTab"],
            "Run"
        );
        assert!(!dock.read(cx).is_dock_open(DockPlacement::Right));
        let before = dock.read(cx).dump(cx);
        assert!(
            WorkspacePanes::restore(&json!({"version":1,"drawerTab":"missing"}), window, cx)
                .is_err()
        );
        assert_eq!(dock.read(cx).dump(cx), before);
    });
}
