//! A visible history owner owns its page and commit reads; hiding or replacing it cancels them.
use std::collections::{HashMap, HashSet};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::*;
use serde_json::json;
use super::{git_history::{GitCommit,GitCommitFile,GitHistory},graph_lanes::{lanes,LaneRow,EdgeKind},history_filter::HistoryFilter};
use crate::core::{intents::open_commit_diff::OpenCommitDiff,socket::{client::SocketClient,outcome::Outcome}};

pub(super) enum CommitFiles { Reading, Read(Vec<GitCommitFile>), Failed(String) }
pub struct CommitTimeline {
    session: String,
    path: Option<String>,
    active: bool,
    pub(super) history: GitHistory,
    filter: HistoryFilter,
    visible: Vec<usize>,
    graph: Vec<LaneRow>,
    expanded: HashSet<String>,
    pub(super) files: HashMap<String,CommitFiles>,
    reading: bool,
    error: Option<String>,
    read: Option<Task<()>>,
    file_reads: HashMap<String,Task<()>>,
    list: ListState,
}
impl CommitTimeline {
    pub fn new(session: String,path: Option<String>,active: bool,cx: &mut Context<Self>)->Self {
        let list=ListState::new(0,ListAlignment::Top,px(200.));
        let watching=list.clone();let weak=cx.entity().downgrade();
        list.set_scroll_handler(move|_,_,cx| {
            if watching.is_scrolled_to_end()==Some(true) { _=weak.update(cx,|timeline,cx|timeline.load_more(cx)); }
        });
        let mut timeline=Self{session,path,active,history:GitHistory::default(),filter:HistoryFilter::default(),visible:Vec::new(),graph:Vec::new(),expanded:HashSet::new(),files:HashMap::new(),reading:false,error:None,read:None,file_reads:HashMap::new(),list};
        if active { timeline.refresh(cx); } timeline
    }
    pub fn set_active(&mut self,active:bool,cx:&mut Context<Self>) {
        self.active=active;
        if active { self.refresh(cx); }
        else { self.read=None;self.reading=false;self.file_reads.clear();self.files.retain(|_,files|!matches!(files,CommitFiles::Reading)); }
        cx.notify();
    }
    pub fn set_path(&mut self,path:Option<String>,cx:&mut Context<Self>) {
        if self.path!=path { self.path=path;self.refresh(cx); }
    }
    pub fn set_filter(&mut self,filter:HistoryFilter,cx:&mut Context<Self>) { self.filter=filter;self.sync_rows();cx.notify(); }
    pub fn refresh(&mut self,cx:&mut Context<Self>) {
        self.read=None;self.reading=false;self.file_reads.clear();self.files.clear();self.expanded.clear();
        self.history=GitHistory::default();self.sync_rows();
        if self.active { self.request(None,cx); } cx.notify();
    }
    pub fn load_more(&mut self,cx:&mut Context<Self>) {
        if self.active && !self.reading { if let Some(cursor)=self.history.next_cursor.clone() { self.request(Some(cursor),cx); } }
    }
    fn request(&mut self,cursor:Option<String>,cx:&mut Context<Self>) {
        let Some(socket)=SocketClient::try_global(cx) else { self.error=Some("The backend isn't running.".into());cx.notify();return; };
        let key=format!("git/history/{}",cx.entity_id());
        let call=socket.read(cx).call::<GitHistory>("git","history",json!({"session":self.session,"cursor":cursor,"path":self.path}),Some(&key));
        self.reading=true;self.error=None;
        self.read=Some(cx.spawn(async move|timeline,cx| {
            let outcome=call.await;
            _=timeline.update(cx,|timeline,cx| {
                timeline.reading=false;timeline.read=None;
                match outcome {
                    Outcome::Ok(page)=>{timeline.history.append(page);timeline.graph=lanes(&timeline.history.commits);timeline.sync_rows();},
                    Outcome::Failed(failure)=>timeline.error=Some(failure.message),
                    Outcome::Aborted=>timeline.error=Some("The connection dropped.".into()),
                } cx.notify();
            });
        }));cx.notify();
    }
    pub(super) fn toggle_commit(&mut self,sha:String,cx:&mut Context<Self>) {
        if !self.expanded.insert(sha.clone()) { self.expanded.remove(&sha);self.file_reads.remove(&sha);self.files.retain(|key,files| key!=&sha || !matches!(files,CommitFiles::Reading));self.list.reset(self.visible.len());cx.notify();return; }
        if !self.files.contains_key(&sha) {
            if let Some(socket)=SocketClient::try_global(cx) {
                let key=format!("git/commitFiles/{}/{sha}",cx.entity_id());
                let call=socket.read(cx).call::<Vec<GitCommitFile>>("git","commitFiles",json!({"session":self.session,"sha":sha}),Some(&key));
                self.files.insert(sha.clone(),CommitFiles::Reading);
                let target=sha.clone();
                let task=cx.spawn(async move|timeline,cx| {
                    let outcome=call.await;
                    _=timeline.update(cx,|timeline,cx| {
                        timeline.file_reads.remove(&target);
                        timeline.files.insert(target,match outcome {
                            Outcome::Ok(files)=>CommitFiles::Read(files),Outcome::Failed(failure)=>CommitFiles::Failed(failure.message),
                            Outcome::Aborted=>CommitFiles::Failed("The connection dropped.".into()),
                        });timeline.list.reset(timeline.visible.len());cx.notify();
                    });
                }); self.file_reads.insert(sha,task);
            }
        } self.list.reset(self.visible.len());cx.notify();
    }
    fn sync_rows(&mut self) {
        let next:Vec<usize>=self.history.commits.iter().enumerate().filter_map(|(index,commit)|self.filter.matches(commit).then_some(index)).collect();
        if next.starts_with(&self.visible) { self.list.splice(self.visible.len()..self.visible.len(),next.len()-self.visible.len()); }
        else { self.list.reset(next.len()); }
        self.visible=next;
    }
    fn row(&mut self,at:usize,_window:&mut Window,cx:&mut Context<Self>)->AnyElement {
        let Some(&index)=self.visible.get(at) else { return Empty.into_any_element(); };
        let commit=self.history.commits[index].clone();
        let sha=commit.sha.clone();let copy_sha=sha.clone();let copy_subject=commit.subject.clone();
        let color=cx.theme().muted_foreground;
        let header=h_flex().gap_2().h(px(32.)).px_2()
            .child(graph(&self.graph[index],cx))
            .child(Button::new(SharedString::from(format!("commit:{sha}"))).ghost().compact().label(commit.short_sha.clone())
                .on_click(cx.listener(move|timeline,_,_,cx|timeline.toggle_commit(sha.clone(),cx))))
            .child(div().flex_1().min_w_0().truncate().text_sm().child(commit.subject.clone()))
            .child(div().text_xs().text_color(color).child(commit.author.clone()))
            .child(Button::new(SharedString::from(format!("copy:{}",commit.sha))).ghost().compact().label("…")
                .dropdown_menu(move|menu,_,_| menu
                    .item(PopupMenuItem::new("Copy SHA").on_click({let sha=copy_sha.clone();move|_,_,cx|cx.write_to_clipboard(ClipboardItem::new_string(sha.clone()))}))
                    .item(PopupMenuItem::new("Copy subject").on_click({let subject=copy_subject.clone();move|_,_,cx|cx.write_to_clipboard(ClipboardItem::new_string(subject.clone()))}))));
        let mut row=v_flex().child(header);
        if self.expanded.contains(&commit.sha) {
            row=row.child(div().pl_4().pr_2().text_xs().text_color(color).child(commit.committed_at.clone()))
                .child(div().pl_4().pr_2().text_sm().child(commit.body.clone()));
            match self.files.get(&commit.sha) {
                Some(CommitFiles::Reading)=>row=row.child(div().pl_4().text_sm().child("Reading changed files…")),
                Some(CommitFiles::Failed(error))=>row=row.child(div().pl_4().text_sm().text_color(cx.theme().danger).child(error.clone())),
                Some(CommitFiles::Read(files))=>{
                    for file in files {
                        let intent=OpenCommitDiff{session_id:self.session.clone(),sha:commit.sha.clone(),path:file.path.clone(),title:format!("{} {}",commit.short_sha,commit.subject)};
                        let path=file.path.clone();let copied=path.clone();
                        row=row.child(h_flex().pl_4().gap_2()
                            .child(Button::new(SharedString::from(format!("file:{}:{path}",commit.sha))).ghost().compact().label(format!("{} {}",file.letter,path))
                                .on_click(move|_,window,cx|window.dispatch_action(Box::new(intent.clone()),cx)))
                            .child(Button::new(SharedString::from(format!("path:{}:{path}",commit.sha))).ghost().compact().label("Copy path")
                                .on_click(move|_,_,cx|cx.write_to_clipboard(ClipboardItem::new_string(copied.clone())))));
                    }
                },None=>{},
            }
        } row.into_any_element()
    }
}
impl Render for CommitTimeline {
    fn render(&mut self,_window:&mut Window,cx:&mut Context<Self>)->impl IntoElement {
        let weak=cx.entity().downgrade();let muted=cx.theme().muted_foreground;
        v_flex().size_full().min_h_0()
            .child(div().px_2().text_xs().text_color(muted).child(self.filter.summary(self.visible.len(),self.history.commits.len())))
            .children(self.error.iter().map(|error|div().px_2().text_sm().text_color(cx.theme().danger).child(error.clone())))
            .when(self.history.commits.is_empty(),|view|view.child(div().p_2().text_sm().text_color(muted).child(if self.reading {"Reading commits…"} else if self.error.is_some(){"History unavailable."}else{"No commits yet."})))
            .child(list(self.list.clone(),move|at,window,cx|weak.update(cx,|timeline,cx|timeline.row(at,window,cx)).unwrap_or_else(|_|Empty.into_any_element())).flex_1())
            .when(self.history.next_cursor.is_some(),|view|view.child(Button::new("history-more").ghost().compact().label(if self.reading{"Reading…"}else{"Load more"}).disabled(self.reading)
                .on_click(cx.listener(|timeline,_,_,cx|timeline.load_more(cx)))))
    }
}
fn graph(row:&LaneRow,cx:&App)->impl IntoElement {
    let row=row.clone();let lane=row.lane;let color=cx.theme().muted_foreground;
    let width=row.edges.iter().flat_map(|(_,from,to)|[*from,*to]).max().unwrap_or(row.lane).max(row.lane)+1;
    div().relative().w(px((width*12+8)as f32)).h(px(32.)).flex_none()
        .child(canvas(|_,_,_|{},move|bounds,_,window,_| {
            for (kind,from,to) in &row.edges {
                let (top,bottom)=match kind{EdgeKind::Passing=>(0.,32.),EdgeKind::Child=>(0.,16.),EdgeKind::Parent=>(16.,32.)};
                let mut path=PathBuilder::stroke(px(1.));
                path.move_to(bounds.origin+point(px((*from*12+8)as f32),px(top)));
                path.line_to(bounds.origin+point(px((*to*12+8)as f32),px(bottom)));
                if let Ok(path)=path.build(){window.paint_path(path,color);}
            }
        }).size_full())
        .child(div().absolute().left(px((lane*12+5)as f32)).top(px(13.)).size(px(6.)).rounded_full().bg(color))
}
#[cfg(test)]
#[path="commit_timeline_tests.rs"]
mod tests;
