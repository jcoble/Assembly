use super::{NewSessionForm,Created,SessionKey,SelectedSession,SessionOpenRequest,SessionOpenResult,SessionOpenTarget,SessionPane,ComposerEvent};
use gpui_kit::*;
impl NewSessionForm {
    pub(super) fn open_first_prompt(&mut self,key:SessionKey,request_id:String,window:&mut Window,cx:&mut Context<Self>) {
        if self.attempt.as_ref().and_then(|attempt|attempt.source.as_ref()).is_none_or(|source|!source.current(cx)) {
            self.start_error("The accepted source changed before opening Chat.",cx);return;
        }
        let selected=SelectedSession::global(cx);
        let navigation=cx.subscribe_in(&selected,window,|form,_,result:&SessionOpenResult,window,cx|form.first_prompt_opened(result,window,cx));
        self.attempt.as_mut().expect("accepted start").navigation=Some(navigation);
        window.dispatch_action(Box::new(SessionOpenRequest{key,request_id,target:SessionOpenTarget::Engine}),cx);
    }
    fn first_prompt_opened(&mut self,result:&SessionOpenResult,window:&mut Window,cx:&mut Context<Self>) {
        let Some(attempt)=self.attempt.as_ref()else{return;};let Some(key)=self.start_key()else{return;};
        if attempt.opening.as_deref()!=Some(result.request_id.as_str())||result.key.as_ref()!=Some(&key){return;}
        if let Err(error)=&result.result {self.attempt.as_mut().expect("same open").opening=None;self.start_error(&error.message,cx);return;}
        if attempt.source.as_ref().is_none_or(|source|!source.current(cx)||SelectedSession::global(cx).read(cx).source()!=Some(&source.source)) {
            self.start_error("The completed Chat open belongs to a different source. The first prompt was retained.",cx);return;
        }
        let composer=match SessionPane::composer_for_open(&key,&result.request_id,cx){Ok(composer)=>composer,Err(error)=>{self.attempt.as_mut().expect("same open").opening=None;self.start_error(&error,cx);return;}};
        let client_id=attempt.request.client_id().to_string();let text=attempt.text.clone();let images=attempt.images.clone();
        let accepted=cx.subscribe(&composer,|form,_,event:&ComposerEvent,cx|{
            if let ComposerEvent::Accepted{key,receipt}=event {
                if form.start_key().as_ref()==Some(key)&&form.attempt.as_ref().is_some_and(|attempt|receipt.session_id==key.id&&receipt.client_id==attempt.request.client_id()) {
                    cx.emit(Created);
                }
            }
        });
        self.attempt.as_mut().expect("same first prompt").prompt=Some(accepted);
        let submitted=composer.update(cx,|composer,cx|composer.submit_initial(&key,&client_id,&text,images,window,cx));
        match submitted {
            Ok(task)=>{
                self._create=Some(cx.spawn(async move|form,cx|{let answer=task.await;let _=form.update(cx,|form,cx|{
                    match answer {
                        Ok(())=>form.attempt.as_mut().expect("same first prompt").prompt_dispatched=true,
                        Err(error)=>{form.attempt.as_mut().expect("same first prompt").opening=None;form.start_error(&error,cx);}
                    }
                });}));
            }
            Err(error)=>{self.attempt.as_mut().expect("same first prompt").opening=None;self.start_error(&error,cx);}
        }
    }
}
