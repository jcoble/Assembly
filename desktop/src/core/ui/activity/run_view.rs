//! A session's workflow in the rail: its run on a row that opens into its steps.
use gpui_kit::*;
use gpui_kit::component::{ActiveTheme as _,v_flex};
use gpui_kit::component::button::{Button,ButtonVariants as _};
use std::rc::Rc;
use crate::activity::{CardBody,RunRow,StatusChip,Tone,step_list};
use crate::core::ui::activity::run_reader::{RunReader,RunSource};
use crate::core::ui::activity::step_row::step_view;
use crate::core::ui::row_drop_down::RowDropDowns;
use crate::core::engine::SessionKey;
pub struct RunActivity {
    reader:Entity<RunReader>,
    /// The run's row and its steps each open on their own.
    runs:Entity<RowDropDowns>,steps:Entity<RowDropDowns>,opened:Option<String>,
    _reader:Subscription,_drops:[Subscription;2],
}
impl RunActivity {
    pub fn new(parent:SessionKey,cx:&mut Context<Self>)->Self {
        let reader=cx.new(|cx|RunReader::new(RunSource::Session(parent),cx));
        let watched=cx.observe(&reader,|view,_,cx|{view.open_first_time(cx);cx.notify();});
        let (runs,steps)=(cx.new(|_|RowDropDowns::default()),cx.new(|_|RowDropDowns::default()));
        let drops=[cx.observe(&runs,|_,_,cx|cx.notify()),cx.observe(&steps,|_,_,cx|cx.notify())];
        Self{reader,runs,steps,opened:None,_reader:watched,_drops:drops}
    }
    /// A run shows its steps when it first arrives, since it's already inside what the user opened.
    fn open_first_time(&mut self,cx:&mut Context<Self>) {
        let Some(id)=self.reader.read(cx).view().map(|page|page.run.id.clone()) else{return;};
        if self.opened.as_ref()==Some(&id){return;}
        self.opened=Some(id.clone());
        RunRow::open(id.into(),&self.runs,Self::steps_body(cx.entity().downgrade()),cx);
    }
    /// Reads the run as it is now on every frame, so an open list follows the steps.
    fn steps_body(this:WeakEntity<Self>)->CardBody {
        Rc::new(move |_,cx|{
            let Some((key,steps,drops))=this.upgrade().and_then(|this|{
                let this=this.read(cx);
                let reader=this.reader.read(cx);
                let page=reader.view()?;
                let now=now_ms();
                Some((page.run.id.clone(),page.steps.iter().map(|step|step_view(step,now)).collect::<Vec<_>>(),this.steps.clone()))
            }) else{return Empty.into_any_element();};
            step_list(key,steps,&drops,cx)
        })
    }
}
impl Render for RunActivity {
    fn render(&mut self,_:&mut Window,cx:&mut Context<Self>)->impl IntoElement {
        self.reader.update(cx,|reader,cx|reader.refresh(cx));
        let muted=cx.theme().muted_foreground;
        let reader=self.reader.read(cx);
        let mut body=v_flex().gap_2();
        if let Some(error)=reader.error() {
            body=body.child(div().text_color(muted).child(error.to_owned()));
            if reader.can_retry(cx){let retry=self.reader.clone();body=body.child(Button::new("reload-workflow").ghost().label("Retry").on_click(move|_,_,cx|retry.update(cx,|reader,cx|reader.retry(cx))));}
        }
        if let Some(page)=reader.view() {
            let tone=run_tone(&page.run.state);
            let done=page.steps.iter().filter(|step|step.state=="succeeded").count();
            let current=page.steps.iter().find(|step|step.state=="running").map(|step|step.label_present.clone());
            let step_line=page.run.display_prerequisite.clone().or(current).unwrap_or_else(||page.run.state.clone());
            body=body.child(RunRow{key:page.run.id.clone().into(),title:page.run.title.clone().unwrap_or_else(||page.run.id.clone()).into(),task_key:None,step_line:step_line.into(),done,total:page.steps.len(),tone,
                pill:Some(StatusChip::new(page.run.state.clone(),tone).into_any_element()),body:Self::steps_body(cx.entity().downgrade()),drops:self.runs.clone()});
        }else if reader.reading(){body=body.child("Loading workflow…");}
        else if reader.error().is_none(){body=body.child("No workflow is attached to this session.");}
        body
    }
}
pub(crate) fn run_tone(state:&str)->Tone {
    match state {"running"=>Tone::Info,"succeeded"=>Tone::Success,"failed"=>Tone::Danger,"waiting"=>Tone::Warning,_=>Tone::Neutral}
}
pub(crate) fn now_ms()->i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).ok().map(|time|time.as_millis().min(i64::MAX as u128) as i64).unwrap_or(0)
}
