//! The page's moves that aren't posts: resolving a thread, marking ready, updating the branch, re-running jobs, and
//! asking an agent. Each write names the head the person saw, and the backend refuses it if GitHub's has moved.
use super::super::pr_writes::PrAct;
use super::ask_sheet;
use super::*;
use crate::core::engine::SessionKey;

impl PrDetail {
    /// The pull request as read, open or not, once no read is running.
    pub(super) fn read_identity(&self) -> Option<ReviewIdentity> {
        if self.loading || self.needs_refresh {
            return None;
        }
        let detail = self.detail.as_ref()?;
        Some(ReviewIdentity {
            repository: detail.repository.clone(),
            number: detail.number,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        })
    }
    pub(super) fn act(&mut self, act: PrAct, extra: Value, cx: &mut Context<Self>) {
        let Some(identity) = self.read_identity() else {
            self.failure = Some("Refresh this pull request before trying again.".into());
            cx.notify();
            return;
        };
        let mut args = json!({"number":identity.number,"expectedHead":identity.head});
        if let (Some(args), Value::Object(extra)) = (args.as_object_mut(), extra) {
            args.extend(extra);
        }
        self.acted = None;
        if let Err(error) = self
            .writes
            .update(cx, |writes, cx| writes.act(act, args, cx))
        {
            self.failure = Some(error);
        }
        cx.notify();
    }
    /// The sessions an agent can be asked in: the one whose checkout this page reads, then those linked to the pull
    /// request.
    fn ask_sessions(&self, cx: &App) -> Vec<(SessionKey, String)> {
        let mut sessions = Vec::new();
        if let PrOwner::Session {
            id,
            source: Some(source),
        } = &self.owner
        {
            sessions.push((
                SessionKey {
                    machine: source.machine.clone(),
                    id: id.clone(),
                },
                "This page's session".to_string(),
            ));
        }
        for (key, title) in self.threads.read(cx).sessions() {
            match sessions.iter_mut().find(|(known, _)| *known == key) {
                Some(known) => known.1 = title,
                None => sessions.push((key, title)),
            }
        }
        sessions
    }
    pub(super) fn ask_agent(
        &mut self,
        prompt: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let sessions = self.ask_sessions(cx);
        ask_sheet::open(sessions, prompt, window, cx);
    }
}
