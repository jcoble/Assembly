//! The selected existing drawer pane is workspace metadata; dock geometry and open/closed state stay device-local.
use gpui_kit::*;
use gpui_kit::component::dock::{DockPlacement,PaneRef,PanelId};
use serde_json::Value;
use crate::core::{state::workspace_panes::{PaneCodec,WorkspaceKey,WorkspacePanes},ui::center_panes::CenterPanes};
pub struct DrawerCodec;
fn panes(cx:&App)->Vec<(PanelId,String,bool)> {
    let Some(dock)=CenterPanes::dock(cx) else{return Vec::new();};
    let mut panes=Vec::new();
    if let Some(tree)=dock.read(cx).layout(DockPlacement::Right) {tree.root().walk(&mut |node| {
        if let PaneRef::Tabs {panels,active_ix}=node.kind() {for (index,id) in panels.iter().enumerate() {
            if let Some(panel)=dock.read(cx).panel(*id).filter(|panel|panel.visible(cx)) {panes.push((*id,panel.panel_name(cx).into(),index==active_ix));}
        }}
    });}panes
}
fn selected(value:&Value,cx:&App)->Result<Option<PanelId>,String> {
    if value.is_null(){return Ok(None);}
    let name=value.as_str().ok_or("The saved drawer tab is invalid.")?;
    let matches:Vec<_>=panes(cx).into_iter().filter(|(_,actual,_)|actual==name).collect();
    if matches.len()!=1 {return Err("The saved drawer tab does not identify one existing drawer pane.".into());}
    Ok(Some(matches[0].0))
}
impl PaneCodec for DrawerCodec {
    fn name(&self)->&'static str {"drawerTab"}
    fn capture(&self,_:&Window,cx:&App)->Result<Value,String> {
        if WorkspacePanes::key(cx).is_none(){return Err("The workspace checkout identity is unavailable.".into());}
        Ok(panes(cx).into_iter().find(|(_,_,active)|*active).map(|(_,name,_)|Value::String(name)).unwrap_or(Value::Null))
    }
    fn validate(&self,value:&Value,_:&WorkspaceKey,_:&Value,cx:&App)->Result<(),String> {selected(value,cx).map(|_|())}
    fn restore(&self,value:&Value,window:&mut Window,cx:&mut App) {
        match selected(value,cx) {
            Ok(Some(id))=>if let Some(dock)=CenterPanes::dock(cx) {
                let open=dock.read(cx).is_dock_open(DockPlacement::Right);
                dock.update(cx,|dock,cx| {
                    dock.select_panel(id,window,cx);
                    if dock.is_dock_open(DockPlacement::Right)!=open {dock.toggle_dock(DockPlacement::Right,window,cx);}
                });
            },
            Ok(None)=>{},Err(error)=>WorkspacePanes::refuse_restore(error,cx),
        }
    }
}

#[cfg(test)]
#[path="drawer_codec_tests.rs"]
mod tests;
