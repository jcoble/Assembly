//! Controlled edit lifecycle. Only a matching caller receipt commits a pending draft.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SaveRequest(u64);

/// Why a save didn't commit. `Refused` nothing changed, so it can be retried. `Uncertain` the owner can't tell whether
/// it landed, so nothing more is sent until the owner has read what's really there and calls `set_committed`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SaveFailure {
    Refused(String),
    Uncertain(String),
}

pub struct EditState<T> {
    committed: T,
    draft: T,
    editing: bool,
    pending: Option<SaveRequest>,
    next_request: u64,
    error: Option<String>,
    uncertain: bool,
}
impl<T: Clone + PartialEq> EditState<T> {
    pub fn new(value: T) -> Self {
        Self {
            committed: value.clone(),
            draft: value,
            editing: false,
            pending: None,
            next_request: 0,
            error: None,
            uncertain: false,
        }
    }
    pub fn committed(&self) -> &T {
        &self.committed
    }
    pub fn draft(&self) -> &T {
        &self.draft
    }
    pub fn editing(&self) -> bool {
        self.editing
    }
    pub fn pending(&self) -> bool {
        self.pending.is_some()
    }
    pub fn uncertain(&self) -> bool {
        self.uncertain
    }
    pub fn error(&self) -> Option<&str> {
        self.error.as_deref()
    }
    pub fn edit(&mut self) -> bool {
        if self.editing {
            return false;
        }
        self.draft = self.committed.clone();
        self.error = None;
        self.editing = true;
        true
    }
    pub fn change(&mut self, value: T) -> bool {
        if !self.editing || self.pending() || self.uncertain {
            return false;
        }
        self.draft = value;
        self.error = None;
        true
    }
    pub fn cancel(&mut self) -> bool {
        if !self.editing || self.pending() {
            return false;
        }
        self.draft = self.committed.clone();
        self.error = None;
        self.uncertain = false;
        self.editing = false;
        true
    }
    pub fn save(&mut self) -> Option<(SaveRequest, T)> {
        if !self.editing || self.pending() || self.uncertain {
            return None;
        }
        if self.draft == self.committed {
            self.cancel();
            return None;
        }
        self.next_request += 1;
        let request = SaveRequest(self.next_request);
        self.pending = Some(request);
        self.error = None;
        Some((request, self.draft.clone()))
    }
    /// A value that failed validation: it stays open with the reason, and nothing was sent.
    pub fn reject(&mut self, message: String) {
        if self.editing && !self.pending() {
            self.error = Some(message);
        }
    }
    pub fn clear_error(&mut self) {
        self.error = None;
    }
    pub fn complete(&mut self, request: SaveRequest, result: Result<T, SaveFailure>) -> bool {
        if self.pending != Some(request) {
            return false;
        }
        self.pending = None;
        match result {
            Ok(value) => {
                self.committed = value.clone();
                self.draft = value;
                self.editing = false;
                self.error = None;
            }
            Err(SaveFailure::Refused(error)) => {
                self.error = Some(error);
            }
            Err(SaveFailure::Uncertain(error)) => {
                self.error = Some(error);
                self.uncertain = true;
            }
        }
        true
    }
    /// A new authoritative value supersedes any outstanding request. An open draft stays recoverable, unless the owner
    /// was only finding out whether this draft landed and it did.
    pub fn set_committed(&mut self, value: T) {
        let landed = self.uncertain && self.draft == value;
        self.pending = None;
        self.error = None;
        self.uncertain = false;
        self.committed = value.clone();
        if landed {
            self.editing = false;
        }
        if !self.editing {
            self.draft = value;
        }
    }
}
#[cfg(test)]
#[path = "state_tests.rs"]
mod tests;
