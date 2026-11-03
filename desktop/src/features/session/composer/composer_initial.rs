//! Initial images and text join the existing prompt/receipt path only after checked navigation.
use gpui_kit::*;
use crate::core::engine::SessionKey;
use super::{Composer,StagedAttachment,Attachment,AttachmentState,owner_prompt::PromptRequest};
impl Composer {
    pub fn submit_initial(&mut self,key:&SessionKey,client_id:&str,text:&str,images:Vec<StagedAttachment>,window:&mut Window,cx:&mut Context<Self>)->Result<Task<Result<(),String>>,String> {
        self.initial_current(key,self.source_serial)?;
        if self.initial_upload.as_ref().is_some_and(|lease|lease.strong_count()>0){return Err("The first prompt upload is still in progress.".into());}
        if client_id.is_empty()||client_id.len()>64||!client_id.bytes().all(|byte|byte.is_ascii_alphanumeric()||matches!(byte,b'_'|b'-')) {return Err("The first prompt needs its original valid client ID.".into());}
        let typed=text.trim().to_string();
        if typed.is_empty()&&images.is_empty(){return Err("Write a message or attach an image.".into());}
        if self.pending(&key.id).iter().any(|pending|pending.client_id()==client_id) {return Err("The first prompt already has a pending outcome. Reconcile its original receipt before retrying.".into());}
        let key=key.clone();let client_id=client_id.to_string();let serial=self.source_serial;
        let binding=self.expected_source.clone().ok_or("The session has no checked attachment source.")?;
        let active=std::rc::Rc::new(());self.initial_upload=Some(std::rc::Rc::downgrade(&active));
        Ok(cx.spawn_in(window,async move|composer,cx| {
            let _active=active;
            let mut attached=Vec::with_capacity(images.len());
            for image in images {
                let upload=composer.update(cx,|composer,cx| {
                    composer.initial_current(&key,serial)?;
                    Ok::<_,String>(crate::features::session::composer::upload::upload_image(composer.client.clone(),key.id.clone(),
                        crate::features::session::composer::upload::UploadSource::Image(image.image().clone()),image.mime(),Some(binding.clone()),cx))
                }).map_err(|_|"The composer closed.".to_string())??;
                let path=match upload.await {
                    Ok(path)=>path,
                    Err(error)=>{let _=composer.update(cx,|composer,cx|{if composer.source_serial==serial{composer.source_refused(&error,cx);}});return Err(error.reason());},
                };
                let attachment=composer.update(cx,|composer,_| {
                    composer.initial_current(&key,serial)?;composer.attached+=1;
                    Ok::<_,String>(Attachment{id:composer.attached,name:image.name().clone(),size:image.size(),state:AttachmentState::Saved{path},preview:Some(image.image().clone())})
                }).map_err(|_|"The composer closed.".to_string())??;
                attached.push(attachment);
            }
            composer.update_in(cx,|composer,window,cx| {
                composer.initial_current(&key,serial)?;
                if composer.pending(&key.id).iter().any(|pending|pending.client_id()==client_id){return Err("The original first prompt is already pending; it was not replayed.".into());}
                let attachment_ids=attached.iter().filter_map(|attachment|match &attachment.state {AttachmentState::Saved{path}=>Some(path.clone()),_=>None}).collect();
                let request=PromptRequest{text:Attachment::with_lines(&typed,&attached),client_id,attachment_ids,epoch:None};
                composer.submit(key.id,request,typed,attached,window,cx);Ok(())
            }).map_err(|_|"The composer closed.".to_string())?
        }))
    }
    fn initial_current(&self,key:&SessionKey,serial:u64)->Result<(),String> {
        if self.start_draft.is_some()||!self.operational()||self.source_serial!=serial||self.session_key.as_ref()!=Some(key)||self.expected_source.as_ref().is_none_or(|source|source.machine!=key.machine)||self.offline.is_some() {
            Err("The first prompt's prepared session source changed. Your start draft was kept.".into())
        }else{Ok(())}
    }
}
