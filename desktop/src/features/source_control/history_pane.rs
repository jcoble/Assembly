use std::collections::HashMap;
use gpui_kit::component::button::{Button,ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel,Panel,PanelEvent,PanelId,PanelInfo,PanelState,panel_handle,register_panel};
use gpui_kit::component::input::{Input,InputEvent,InputState};
use gpui_kit::component::menu::{DropdownMenu as _,PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _,Sizable as _,h_flex,v_flex};
use gpui_kit::*;
use serde_json::json;
use super::{commit_timeline::CommitTimeline,history_filter::HistoryFilter};
use crate::core::{intents::open_git_history::OpenGitHistory,ui::center_panes::CenterPanes};

const HISTORY:&str="Git history";
struct OpenHistory(HashMap<String,WeakEntity<HistoryPane>>);
impl Global for OpenHistory {}
pub struct HistoryPane {
    focus:FocusHandle,session:String,path:Option<String>,timeline:Entity<CommitTimeline>,
    search:Entity<InputState>,filter:HistoryFilter,_heard:Vec<Subscription>,
}
fn key(session:&str)->String {format!("git-history:{session}")}
impl HistoryPane {
    pub fn register(cx:&mut App) {
        cx.set_global(OpenHistory(HashMap::new()));
        register_panel(cx,HISTORY,|build,window,cx| {
            let saved=match build.state().info.clone(){PanelInfo::Panel(saved)=>saved,_=>serde_json::Value::Null};
            let session=saved["session"].as_str().unwrap_or_default().to_string();
            let path=saved["path"].as_str().map(String::from);
            let pane=cx.new(|cx|Self::new(session.clone(),path,window,cx));
            Self::remember(&session,&pane,cx);panel_handle(pane)
        });
        cx.on_action(|intent:&OpenGitHistory,cx| {
            let intent=intent.clone();
            if let Some(window)=cx.active_window().or_else(||cx.windows().into_iter().next()) {
                cx.defer(move|cx|{_=window.update(cx,|_,window,cx|Self::open(intent,window,cx));});
            }
        });
    }
    fn remember(session:&str,pane:&Entity<Self>,cx:&mut App) {
        CenterPanes::remember(key(session),PanelId::from(pane.entity_id()),cx);
        cx.global_mut::<OpenHistory>().0.insert(session.into(),pane.downgrade());
    }
    fn open(intent:OpenGitHistory,window:&mut Window,cx:&mut App) {
        let existing=cx.global::<OpenHistory>().0.get(&intent.session_id).and_then(WeakEntity::upgrade);
        if let Some(pane)=existing {
            pane.update(cx,|pane,cx|pane.set_path(intent.path,cx));
            CenterPanes::open(key(&intent.session_id),window,cx,|_,_|pane);
        } else {
            CenterPanes::open(key(&intent.session_id),window,cx,|window,cx| {
                let session=intent.session_id;
                let pane=cx.new(|cx|Self::new(session.clone(),intent.path,window,cx));
                Self::remember(&session,&pane,cx);pane
            });
        }
    }
    fn new(session:String,path:Option<String>,window:&mut Window,cx:&mut Context<Self>)->Self {
        let timeline=cx.new(|cx|CommitTimeline::new(session.clone(),path.clone(),true,cx));
        let search=cx.new(|cx|InputState::new(window,cx).placeholder("Subject or SHA prefix"));
        let heard=vec![cx.observe(&timeline,|_,_,cx|cx.notify()),cx.subscribe(&search,|pane,_,event:&InputEvent,cx| {
            if matches!(event,InputEvent::Change) { pane.filter.text=pane.search.read(cx).value().to_string();pane.apply_filter(cx); }
        })];
        Self{focus:cx.focus_handle(),session,path,timeline,search,filter:HistoryFilter::default(),_heard:heard}
    }
    fn set_path(&mut self,path:Option<String>,cx:&mut Context<Self>) {self.path=path.clone();self.timeline.update(cx,|timeline,cx|timeline.set_path(path,cx));cx.notify();}
    fn apply_filter(&mut self,cx:&mut Context<Self>){let filter=self.filter.clone();self.timeline.update(cx,|timeline,cx|timeline.set_filter(filter,cx));cx.notify();}
}
impl EventEmitter<PanelEvent> for HistoryPane {}
impl Focusable for HistoryPane {fn focus_handle(&self,_:&App)->FocusHandle{self.focus.clone()}}
impl BasePanel for HistoryPane {
    fn panel_name(&self)->&'static str{HISTORY}
    fn set_active(&mut self,active:bool,_:&mut Window,cx:&mut Context<Self>){self.timeline.update(cx,|timeline,cx|timeline.set_active(active,cx));}
    fn on_removed(&mut self,_:&mut Window,cx:&mut Context<Self>){self.timeline.update(cx,|timeline,cx|timeline.set_active(false,cx));}
    fn dump(&self,_:&App)->PanelState{PanelState{panel_name:HISTORY.into(),children:Vec::new(),info:PanelInfo::panel(json!({"session":self.session,"path":self.path}))}}
}
impl Panel for HistoryPane {fn title(&mut self,_:&mut Window,_:&mut Context<Self>)->impl IntoElement{HISTORY}}
impl Render for HistoryPane {
    fn render(&mut self,_:&mut Window,cx:&mut Context<Self>)->impl IntoElement {
        let history=&self.timeline.read(cx).history;
        let mut branches:Vec<String>=history.commits.iter().flat_map(|commit|commit.refs.iter().map(|reference|reference.strip_prefix("HEAD -> ").unwrap_or(reference).to_string())).collect();branches.sort();branches.dedup();
        let mut authors:Vec<String>=history.commits.iter().map(|commit|commit.author.clone()).collect();authors.sort();authors.dedup();
        let weak=cx.entity().downgrade();let branch_target=weak.clone();let author_target=weak;
        let branch=self.filter.branch.clone();let author=self.filter.author.clone();
        let header=h_flex().px_2().py_2().gap_2()
            .child(div().flex_1().child(Input::new(&self.search).small()))
            .child(Button::new("history-branch").ghost().compact().label(branch.clone().unwrap_or_else(||"All branches".into())).dropdown_menu(move|menu,_,_| {
                std::iter::once(None).chain(branches.iter().cloned().map(Some)).fold(menu,|menu,value| {
                    let target=branch_target.clone();let selected=branch==value;
                    menu.item(PopupMenuItem::new(value.clone().unwrap_or_else(||"All branches".into())).checked(selected).on_click(move|_,_,cx|{_=target.update(cx,|pane,cx|{pane.filter.branch=value.clone();pane.apply_filter(cx);});}))
                })
            }))
            .child(Button::new("history-author").ghost().compact().label(author.clone().unwrap_or_else(||"All authors".into())).dropdown_menu(move|menu,_,_| {
                std::iter::once(None).chain(authors.iter().cloned().map(Some)).fold(menu,|menu,value| {
                    let target=author_target.clone();let selected=author==value;
                    menu.item(PopupMenuItem::new(value.clone().unwrap_or_else(||"All authors".into())).checked(selected).on_click(move|_,_,cx|{_=target.update(cx,|pane,cx|{pane.filter.author=value.clone();pane.apply_filter(cx);});}))
                })
            }))
            .child(Button::new("history-refresh").ghost().compact().label("Refresh").on_click(cx.listener(|pane,_,_,cx|pane.timeline.update(cx,|timeline,cx|timeline.refresh(cx)))));
        v_flex().track_focus(&self.focus).size_full().min_h_0().child(header)
            .children(self.path.clone().map(|path|Button::new("history-path").ghost().compact().label(format!("File: {path} ×")).on_click(cx.listener(|pane,_,_,cx|pane.set_path(None,cx)))))
            .child(self.timeline.clone())
    }
}
