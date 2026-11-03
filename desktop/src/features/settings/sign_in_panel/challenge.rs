//! Sensitive challenge material stays only in this panel and the existing backend board.
use serde::Deserialize;
use serde_json::{Value, json};
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Challenge {
    pub operation_id: String,
    pub generation: i64,
    pub link: Option<String>,
    pub wants_code: bool,
    pub terminal_id: Option<String>,
}
#[derive(Clone, PartialEq, Eq)]
pub(super) struct DisplayedLink {
    pub machine: String,
    pub operation_id: String,
    pub generation: i64,
    pub original_url: String,
}
#[derive(Default)]
pub(super) struct ChallengeState {
    pub current: Option<Challenge>,
    operation_id: String,
    latest: i64,
    consumed: i64,
    pub opening: bool,
}
impl ChallengeState {
    pub fn replace_operation(&mut self, operation: &str) {
        if self.operation_id != operation {
            *self = Self {
                operation_id: operation.into(),
                ..Self::default()
            };
        }
    }
    pub fn clear_display(&mut self) {
        self.current = None;
        self.consumed = self.latest;
        self.opening = false;
    }
    pub fn install(&mut self, challenge: Challenge) -> bool {
        if challenge.operation_id != self.operation_id
            || self.operation_id.is_empty()
            || challenge.generation <= 0
            || challenge.generation < self.latest
            || challenge.generation <= self.consumed
        {
            return false;
        }
        if challenge.generation > self.latest {
            self.opening = false;
        }
        self.latest = challenge.generation;
        self.current = Some(challenge);
        true
    }
    pub fn begin_click(&mut self, machine: &str) -> Option<DisplayedLink> {
        if self.opening {
            return None;
        }
        let challenge = self.current.as_ref()?;
        let original_url = challenge.link.as_ref()?.clone();
        if challenge.generation <= self.consumed {
            return None;
        }
        self.opening = true;
        Some(DisplayedLink {
            machine: machine.into(),
            operation_id: challenge.operation_id.clone(),
            generation: challenge.generation,
            original_url,
        })
    }
    pub fn is_current(&self, link: &DisplayedLink) -> bool {
        self.current.as_ref().is_some_and(|current| {
            current.operation_id == link.operation_id
                && current.generation == link.generation
                && current.link.as_ref() == Some(&link.original_url)
                && current.generation > self.consumed
        })
    }
    pub fn opened(&mut self, link: &DisplayedLink, success: bool) -> Option<Value> {
        if !self.is_current(link) {
            return None;
        }
        if !success {
            self.opening = false;
            return None;
        }
        Some(
            json!({"operationId":link.operation_id,"expectedGeneration":link.generation,"originalUrl":link.original_url,"machine":link.machine}),
        )
    }
    pub fn acknowledged(&mut self, link: &DisplayedLink) {
        if self.is_current(link) {
            self.consumed = link.generation;
            self.current = None;
            self.opening = false;
        }
    }
    pub fn release_click(&mut self, link: &DisplayedLink) {
        if self.is_current(link) {
            self.opening = false;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn challenge(generation: i64, link: &str) -> Challenge {
        serde_json::from_value(serde_json::json!({"operationId":"operation","generation":generation,"link":link,"wantsCode":false,"callbackHost":null,"callbackPort":null,"terminalId":null})).unwrap()
    }
    #[test]
    fn only_a_successful_open_of_the_still_displayed_exact_tuple_can_acknowledge() {
        let mut state = ChallengeState::default();
        state.replace_operation("operation");
        let url = "https://example.invalid/authorize?x=%2f&state=exact+value";
        assert!(state.install(challenge(1, url)));
        let click = state.begin_click("owner").unwrap();
        assert!(state.begin_click("owner").is_none());
        assert!(state.opened(&click, false).is_none());
        let click = state.begin_click("owner").unwrap();
        let args = state.opened(&click, true).unwrap();
        assert_eq!(
            args,
            serde_json::json!({"operationId":"operation","expectedGeneration":1,"originalUrl":url,"machine":"owner"})
        );
        assert!(state.install(challenge(2, url)));
        assert!(state.opened(&click, true).is_none());
        assert!(!state.install(challenge(1, url)));
    }
    #[test]
    fn consumed_or_terminal_challenges_never_reappear_from_late_reads() {
        let mut state = ChallengeState::default();
        state.replace_operation("operation");
        state.install(challenge(1, "https://example.invalid/a"));
        let click = state.begin_click("owner").unwrap();
        state.acknowledged(&click);
        assert!(!state.install(challenge(1, "https://example.invalid/a")));
        assert!(state.begin_click("owner").is_none());
        state.replace_operation("");
        assert!(!state.install(challenge(2, "https://example.invalid/b")));
    }
}
