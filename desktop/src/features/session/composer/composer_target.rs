//! One configured-target lookup and warm per selected owning target/reconnect generation.
use super::Composer;
use crate::features::session::composer::agent_config::{AgentConfig, AgentList};
use gpui_kit::*;
use serde_json::json;

impl Composer {
    pub fn set_owner(
        &mut self,
        agent: Option<String>,
        machine: Option<String>,
        route: String,
        cx: &mut Context<Self>,
    ) {
        let owner = agent.map(|agent| (agent, machine, route));
        if self.owner == owner {
            return;
        }
        self.owner = owner;
        self.refresh_config_target(cx);
    }
    pub(super) fn connection_changed(&mut self, connected: bool, cx: &mut Context<Self>) {
        if !connected {self.invalidate_source(cx);}
        if self.backend_connected == connected {
            return;
        }
        self.backend_connected = connected;
        self.refresh_config_target(cx);
    }
    pub(super) fn refresh_config_target(&mut self, cx: &mut Context<Self>) {
        if !self.operational(){return;}
        self.target_generation += 1;
        self._target = None;
        self._warm = None;
        self._config = None;
        self.advertised_target = None;
        self.picking = 0;
        self.read_config(cx);
        let (Some(session), Some(owner)) = (self.session.clone(), self.owner.clone()) else {
            return;
        };
        if !self.backend_connected || self.offline.is_some() {
            return;
        }
        if owner.0 == "unknown" || owner.0.is_empty() {
            self.error = Some(
                "Agent settings unavailable: this transcript has no proven configured provider."
                    .into(),
            );
            cx.notify();
            return;
        }
        let generation = self.target_generation;
        let call = self
            .client
            .call("machines", "agents", json!({"machine":owner.1}), None, cx);
        self._target = Some(cx.spawn(async move |composer, cx| {
            let answer = call.await;
            _ = composer.update(cx, |composer, cx| {
                if composer.target_generation != generation
                    || composer.session.as_ref() != Some(&session)
                    || composer.owner.as_ref() != Some(&owner)
                {
                    return;
                }
                let result = answer
                    .map_err(|e| e.reason())
                    .and_then(|v| serde_json::from_value::<AgentList>(v).map_err(|e| e.to_string()))
                    .and_then(|list| {
                        let agent = list.agents.into_iter().find(|a| a.id == owner.0);
                        composer.agent_name =
                            agent.as_ref().map(|a| a.name.clone().into());
                        agent
                            .and_then(|a| a.target_id)
                            .filter(|id| !id.is_empty())
                            .ok_or_else(|| {
                                "This provider/backend did not advertise a composer target."
                                    .to_string()
                            })
                    });
                match result {
                    Ok(target) => {
                        composer.advertised_target = Some(target);
                        composer.warm_config(cx);
                    }
                    Err(reason) => {
                        composer.error =
                            Some(format!("Agent settings unavailable: {reason}").into())
                    }
                }
                cx.notify();
            });
        }));
    }
    fn warm_config(&mut self, cx: &mut Context<Self>) {
        let (Some(session), Some(target), Some(owner)) = (
            self.session.clone(),
            self.advertised_target.clone(),
            self.owner.clone(),
        ) else {
            return;
        };
        let (generation, picks) = (self.target_generation, self.picks);
        let call = self.client.call(
            "config",
            "warm",
            self.source_args(json!({"session":session,"targetId":target,"machine":owner.1})),
            None,
            cx,
        );
        self._warm = Some(cx.spawn(async move |composer, cx| {
            let answer = call.await;
            _ = composer.update(cx, |composer, cx| {
                if composer.target_generation != generation
                    || composer.picks != picks
                    || composer.session.as_ref() != Some(&session)
                    || composer.advertised_target.as_ref() != Some(&target)
                {
                    return;
                }
                if let Err(error)=&answer{composer.source_refused(error,cx);}
                match answer.map_err(|e| e.reason()).and_then(|v| {
                    serde_json::from_value::<AgentConfig>(v).map_err(|e| e.to_string())
                }) {
                    Ok(config) => {
                        composer.picks += 1; // A late cache read cannot replace this actual observation.
                        composer.config = Some(config);
                        composer.error = None;
                    }
                    Err(reason) => {
                        composer.error =
                            Some(format!("Agent settings unavailable: {reason}").into())
                    }
                }
                cx.notify();
            });
        }));
    }
}
