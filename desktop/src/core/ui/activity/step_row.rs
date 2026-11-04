//! One semantic workflow step renderer for rail and workflow/board consumers.
use gpui_kit::*;
use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _,Icon,Sizable as _,h_flex,v_flex};
use crate::core::engine::workflow_items::StepItem;
use crate::core::ui::spinner::Spinner;
#[derive(Clone,Copy,Debug,PartialEq,Eq)]
pub enum StepTone {Pending,Running,Succeeded,Failed,Cancelled,Skipped,Waiting}
#[derive(Clone,Debug)]
pub struct StepRowView {
    pub label:String,pub state:String,pub tone:StepTone,pub running:bool,pub detail:Option<String>,pub explanation:Option<String>,
    pub progress:Option<f64>,pub attempt:i64,pub max_attempts:i64,pub required_scope:Option<String>,pub scope_granted:bool,pub closed:bool,pub elapsed_ms:Option<i64>,
}
pub fn view(step:&StepItem,now_ms:i64)->StepRowView {
    let tone=match step.state.as_str(){"running"=>StepTone::Running,"succeeded"=>StepTone::Succeeded,"failed"=>StepTone::Failed,"cancelled"=>StepTone::Cancelled,"skipped"=>StepTone::Skipped,"waiting"=>StepTone::Waiting,_=>StepTone::Pending};
    let complete=matches!(tone,StepTone::Succeeded|StepTone::Failed|StepTone::Cancelled|StepTone::Skipped);
    let elapsed_ms=step.started_ms.and_then(|start|step.ended_ms.or_else(||(tone==StepTone::Running).then_some(now_ms)).map(|end|(end-start).max(0)));
    StepRowView{label:if complete{step.label_past.clone()}else{step.label_present.clone()},state:step.state.clone(),tone,running:tone==StepTone::Running,
      detail:step.detail.clone(),explanation:step.explanation.clone(),progress:step.progress,attempt:step.attempt,max_attempts:step.max_attempts,
      required_scope:step.required_scope.clone(),scope_granted:step.scope_granted,closed:step.closed,elapsed_ms}
}
/// The step's sign: a spinner while it runs, a check or a cross once it has finished, and otherwise a still dot.
fn sign(tone:StepTone,color:Hsla)->AnyElement {
    match tone {
        StepTone::Running=>Spinner::new().xsmall().color(color).into_any_element(),
        StepTone::Succeeded=>Icon::new(IconName::CircleCheck).xsmall().text_color(color).into_any_element(),
        StepTone::Failed=>Icon::new(IconName::CircleX).xsmall().text_color(color).into_any_element(),
        StepTone::Pending|StepTone::Cancelled|StepTone::Skipped|StepTone::Waiting=>Icon::new(IconName::Dot).xsmall().text_color(color).into_any_element(),
    }
}
pub fn render(step:&StepItem,now_ms:i64,cx:&mut App)->AnyElement {
    let row=view(step,now_ms);let muted=cx.theme().muted_foreground;
    let color=match row.tone {StepTone::Failed=>cx.theme().danger,StepTone::Succeeded=>cx.theme().success,_=>muted};
    let mut title=h_flex().id(SharedString::from(format!("step:{}",step.id))).gap_2().child(sign(row.tone,color)).child(div().font_weight(FontWeight::MEDIUM).child(row.label)).child(div().text_color(color).child(row.state));
    if row.attempt>0 {title=title.child(div().text_color(muted).child(format!("Attempt {}/{}",row.attempt,row.max_attempts)));}
    if let Some(ms)=row.elapsed_ms {title=title.child(div().text_color(muted).child(format!("{:.1}s",ms as f64/1000.)));}
    let mut body=v_flex().gap_1().py_1().child(title);
    if let Some(detail)=row.detail {body=body.child(detail);}
    if let Some(explanation)=row.explanation {body=body.child(div().text_color(muted).child(explanation));}
    if let Some(progress)=row.progress {body=body.child(div().text_color(muted).child(format!("Progress: {progress}")));}
    if let Some(scope)=row.required_scope {body=body.child(div().text_color(muted).child(format!("Scope: {scope} ({})",if row.scope_granted{"granted"}else{"not granted"})));}
    for badge in &step.badges {body=body.child(div().text_color(muted).child(badge.as_str().map(str::to_owned).unwrap_or_else(||badge.to_string())));}
    body.into_any_element()
}
