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

    /// Publishes this pane's session's facts if it's the selected one; clears what it published before for a session
    /// it no longer shows or that is no longer selected.
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
        if self.live.is_some() && (!selected || self.live != identity) {
            self.clear_live(cx);
        }
        let (true, Some((session, key))) = (selected, identity) else {
            return;
        };
        self.live = Some((session.clone(), key.clone()));
        let facts = self.live_facts(session, key, cx);
        LiveFacts::publish(facts, cx);
    }

    /// Clears what this pane published, if the value is still its own.
    pub(super) fn clear_live(&mut self, cx: &mut App) {
        if let Some((session, key)) = self.live.take() {
            LiveFacts::clear(&session, key.as_ref(), cx);
        }
    }

    fn live_facts(&self, session: String, key: Option<SessionKey>, cx: &App) -> LiveFacts {
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
        let turns_ended = stats.filter(|turn| turn.status != "running").count();
        let composer = self.composer.read(cx);
        let (model, effort, access) = composer.live_config();
        let positive = |n: Option<i64>| n.filter(|n| *n > 0);
        LiveFacts {
            session,
            key,
            provider: composer.agent_name(),
            context: self.view.context,
            input_tokens: figures.and_then(|turn| positive(turn.input_tokens)),
            output_tokens: figures.and_then(|turn| positive(turn.output_tokens)),
            model: model.or(reported_model),
            effort,
            access,
            newest_prompt_seq: self.view.newest_prompt,
            turns_ended,
            drafts: composer.draft_images(),
        }
    }
}
