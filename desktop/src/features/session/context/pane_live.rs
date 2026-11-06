//! The selected session's live facts, which this pane publishes for the Context tab (context/live_facts.rs) while it
//! shows that session: on a refresh, when the composer changes and when the selection changes.

use super::*;
use crate::core::engine::SessionKey;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::context::live_facts::LiveFacts;

impl SessionPane {
    /// The session this pane shows, as the selection names it.
    fn live_identity(&self) -> Option<(String, Option<SessionKey>)> {
        let session = self.session.clone()?;
        let key = self.row_source.as_ref().map(|source| SessionKey {
            machine: source.machine.clone(),
            id: session.clone(),
        });
        Some((session, key))
    }

    /// Publishes this pane's session's facts if it's the selected one, and otherwise clears what this pane published.
    /// While a reload's page is still coming (`held`), the last value stays, so the tab never sees an empty session.
    pub(super) fn publish_live(&mut self, cx: &mut Context<Self>) {
        if LiveFacts::try_global(cx).is_none() {
            return;
        }
        let identity = self.live_identity();
        let selected = SelectedSession::try_global(cx).is_some_and(|selected| {
            let selected = selected.read(cx);
            identity.as_ref().is_some_and(|(session, key)| {
                selected.session_id() == Some(session.as_str()) && selected.key() == key.as_ref()
            })
        });
        let publisher = cx.entity_id();
        let (true, Some((session, key))) = (selected, identity) else {
            LiveFacts::clear(publisher, cx);
            return;
        };
        if self.held.is_some() {
            return;
        }
        let facts = self.live_facts(publisher, session, key, cx);
        LiveFacts::publish(facts, cx);
    }

    fn live_facts(
        &self,
        publisher: EntityId,
        session: String,
        key: Option<SessionKey>,
        cx: &App,
    ) -> LiveFacts {
        let stats = self.view.stats.values();
        // A cancelled turn reports all zeros, so the last real figures stay.
        let figures = (stats.clone())
            .filter(|turn| {
                turn.input_tokens.is_some_and(|n| n > 0)
                    || turn.output_tokens.is_some_and(|n| n > 0)
            })
            .max_by_key(|turn| turn.started_at);
        let reported_model = (stats.clone())
            .filter(|turn| turn.model.as_deref().is_some_and(|model| !model.is_empty()))
            .max_by_key(|turn| turn.started_at)
            .and_then(|turn| turn.model.clone());
        // The newest by start, so an older page loading leaves it as it is.
        let newest_ended_run = stats
            .filter(|turn| turn.status != "running")
            .max_by(|a, b| (a.started_at, &a.run_id).cmp(&(b.started_at, &b.run_id)))
            .map(|turn| turn.run_id.clone());
        let composer = self.composer.read(cx);
        let (model, effort, access) = composer.live_config();
        let positive = |n: Option<i64>| n.filter(|n| *n > 0);
        LiveFacts {
            publisher,
            session,
            key,
            // The agent's name once the composer has read it, else the session's provider as the engine named it, so
            // a read-only or offline session still says whose figures are missing.
            provider: (composer.agent_name())
                .or_else(|| self.active_provider.clone().map(SharedString::from)),
            context: self.view.context,
            input_tokens: figures.and_then(|turn| positive(turn.input_tokens)),
            output_tokens: figures.and_then(|turn| positive(turn.output_tokens)),
            model: model.or(reported_model),
            effort,
            access,
            newest_prompt_seq: self.view.newest_prompt,
            newest_ended_run,
            drafts: composer.draft_images(),
        }
    }
}
