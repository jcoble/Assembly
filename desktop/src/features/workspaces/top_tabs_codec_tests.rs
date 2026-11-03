use super::{TopTabsCodec,center_ids,WorkspaceKey,WorkspacePanes,CenterPanes,ChangesPane,HistoryPane,PrPane,DockArea};
use gpui_kit::{AppContext as _,Context,IntoElement,Render,Window,div};
use serde_json::Value;
use gpui::TestAppContext;
use crate::core::state::{selected_session::SelectedSession,preferences::Preferences};
use crate::features::{session::view::session_pane::SessionPane,shell::default_layout::default_state};
struct Host;
impl Render for Host {fn render(&mut self,_:&mut Window,_:&mut Context<Self>)->impl IntoElement {div()}}
fn key()->WorkspaceKey {WorkspaceKey {machine:"actual-owner".into(),session_id:"session".into(),checkout_root:"/checkout".into()}}
fn saved()->Value {serde_json::json!({"version":1,"files":[],"topTabs":{
    "order":["Chat","git-history:session","Changes","pull-requests"],"activeKey":"Changes","panes":[
        {"key":"git-history:session","state":{"panel_name":"Git history","children":[],"info":{"panel":{"session":"session","path":"src/λ.rs"}}}},
        {"key":"Changes","state":{"panel_name":"Changes","children":[],"info":{"panel":{"session":"session","path":"src/λ.rs","split":true}}}},
        {"key":"pull-requests","state":{"panel_name":"Pull requests","children":[],"info":{"panel":{"session":null}}}}
    ]}})}
#[gpui::test]
fn actual_registered_factories_restore_order_and_active_pane_while_retaining_the_fixed_chat(cx:&mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);SelectedSession::init(cx);Preferences::init(cx);SessionPane::register(cx);
        ChangesPane::register(cx);HistoryPane::register(cx);PrPane::register(cx);
        WorkspacePanes::register(Box::new(TopTabsCodec),cx).unwrap();
    });let (_,cx)=cx.add_window_view(|_,_|Host);
    cx.update(|window,cx| {
        let dock=cx.new(|cx|DockArea::new("workspace-top-tabs",Some(2),window,cx));CenterPanes::init(&dock,cx);
        dock.update(cx,|dock,cx|dock.load(default_state(),window,cx).unwrap());
        let chat=center_ids(&dock,cx).into_iter().find(|id|dock.read(cx).panel(*id).unwrap().panel_name(cx)=="Chat").unwrap();
        WorkspacePanes::set_key(key(),cx);WorkspacePanes::restore(&saved(),window,cx).unwrap();
        let snapshot=WorkspacePanes::capture(window,cx).unwrap();assert_eq!(snapshot["topTabs"],saved()["topTabs"]);
        assert!(dock.read(cx).panel(chat).is_some(),"Chat is retained, never rebuilt from workspace metadata");
        let before=dock.read(cx).dump(cx);let mut corrupt=saved();corrupt["topTabs"]["panes"][0]["state"]["info"]["panel"]["session"]=Value::from("foreign");
        assert!(WorkspacePanes::restore(&corrupt,window,cx).is_err());assert_eq!(dock.read(cx).dump(cx),before);
        WorkspacePanes::deactivate(window,cx);assert_eq!(center_ids(&dock,cx),vec![chat]);
    });
}

#[gpui::test]
fn a_real_split_layout_is_refused_before_restore_or_rootless_teardown_changes_its_geometry(cx:&mut TestAppContext) {
    use gpui_kit::component::dock::{PanelState,PanelInfo};
    use gpui_kit::px;
    cx.update(|cx| {
        gpui_kit::init(cx);SelectedSession::init(cx);Preferences::init(cx);SessionPane::register(cx);
        ChangesPane::register(cx);HistoryPane::register(cx);PrPane::register(cx);
        WorkspacePanes::register(Box::new(TopTabsCodec),cx).unwrap();
    });let (_,cx)=cx.add_window_view(|_,_|Host);
    cx.update(|window,cx| {
        let dock=cx.new(|cx|DockArea::new("workspace-split",Some(2),window,cx));CenterPanes::init(&dock,cx);
        let mut state=default_state();let chat=state.center.clone();let mut changes=chat.clone();
        changes.children=vec![PanelState {panel_name:"Changes".into(),children:vec![],info:PanelInfo::panel(Value::Null)}];
        state.center=PanelState {panel_name:"StackPanel".into(),children:vec![chat,changes],info:PanelInfo::Stack {sizes:vec![px(400.),px(400.)],axis:0}};
        dock.update(cx,|dock,cx|dock.load(state,window,cx).unwrap());WorkspacePanes::set_key(key(),cx);
        let before=dock.read(cx).dump(cx);assert!(WorkspacePanes::validate_deactivate(cx).is_err());
        assert!(WorkspacePanes::restore(&saved(),window,cx).is_err());assert_eq!(dock.read(cx).dump(cx),before);
    });
}
