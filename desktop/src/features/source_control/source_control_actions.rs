use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::SourceControlPane;
use crate::core::state::session_read::Shown;
use crate::features::source_control::git_status::ChangedFile;
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::{WindowExt as _, v_flex};
use gpui_kit::*;
use serde_json::{Value, json};
use std::cell::Cell;
use std::rc::Rc;

impl SourceControlPane {
    pub(super) fn start_write(
        &mut self,
        method: &'static str,
        args: Value,
        cx: &mut Context<Self>,
    ) {
        let Some(session) = self.read.read(cx).session().map(String::from) else {
            return;
        };
        if self.action.read(cx).pending.is_some() {
            return;
        }
        self.submitted_revision = if method == "commit" {
            Some(self.edit_revision)
        } else {
            None
        };
        self.action.update(cx, |action, cx| {
            action.run(session, method, args, cx);
        });
    }
    pub(super) fn commit(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.action.read(cx).pending.is_some() {
            return;
        }
        let (session, status) = {
            let read = self.read.read(cx);
            let Shown::Read(status) = read.shown() else {
                return;
            };
            (
                read.session().unwrap_or_default().to_string(),
                status.clone(),
            )
        };
        let message = self.message.read(cx).value().to_string();
        if (!self.amend && message.trim().is_empty()) || (!self.amend && !status.has_staged_files())
        {
            return;
        }
        let args = json!({"session":session,"message":message,"amend":self.amend,"expectedHead":status.head});
        if self.amend && status.has_upstream && status.ahead == 0 {
            let pane = cx.entity().downgrade();
            window.open_alert_dialog(cx,move|alert,_,_|{
                let pane=pane.clone();let args=args.clone();
                alert.title("Amend a pushed commit?")
                    .description("This commit is already pushed. Amending it means the next push will be refused, and Assembly never force-pushes.")
                    .show_cancel(true).on_ok(move|_,_,cx|{_=pane.update(cx,|pane,cx|pane.start_write("commit",args.clone(),cx));true})
            });
        } else {
            self.start_write("commit", args, cx);
        }
    }
    pub(super) fn ask_discard(
        &mut self,
        files: Vec<ChangedFile>,
        fingerprint: Option<String>,
        omitted: usize,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.action.read(cx).pending.is_some() {
            return;
        }
        let Some(session) = self.read.read(cx).session().map(String::from) else {
            return;
        };
        let include_new = Rc::new(Cell::new(false));
        let pane = cx.entity().downgrade();
        window.open_alert_dialog(cx,move|alert,_,cx|{
            let toggled=include_new.clone();let chosen=include_new.clone();let target=pane.clone();
            let mut body=v_flex().gap_2().child("These changes cannot be got back.")
                .child(v_flex().id("discard-files").max_h(px(240.)).overflow_y_scroll().children(files.iter().map(|file|{
                    div().text_role(TextRole::Body, cx).child(if file.index=="?"||file.index=="A"{format!("{} (will be deleted)",file.path)}else{file.label()})
                })));
            if omitted>0{body=body.child(format!("and {omitted} more"));}
            if fingerprint.is_some(){body=body.child(Checkbox::new("discard-include-new").label("Also delete new files").checked(include_new.get()).on_click(move|value,_,cx|{
                toggled.set(*value);_=target.update(cx,|_,cx|cx.notify());
            }));}
            let pane=pane.clone();let session=session.clone();let files=files.clone();let fingerprint=fingerprint.clone();
            alert.title("Discard changes?").description(body).show_cancel(true).on_ok(move|_,_,cx|{
                let args=if let Some(fingerprint)=&fingerprint{
                    json!({"session":session,"expectedFingerprint":fingerprint,"includeUntracked":chosen.get()})
                }else{
                    let tracked:Vec<&str>=files.iter().filter(|file|file.index!="?").map(|file|file.path.as_str()).collect();
                    let untracked:Vec<&str>=files.iter().filter(|file|file.index=="?").map(|file|file.path.as_str()).collect();
                    json!({"session":session,"tracked":tracked,"untracked":untracked,"expected":files})
                };
                _=pane.update(cx,|pane,cx|pane.start_write("discard",args,cx));true
            })
        });
    }
}
