//! Controlled edit lifecycle. Only a matching caller receipt commits a pending draft.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SaveRequest(u64);

pub struct EditState<T> {
    committed: T,
    draft: T,
    editing: bool,
    pending: Option<SaveRequest>,
    next_request: u64,
    error: Option<String>,
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
        if !self.editing || self.pending() {
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
        self.editing = false;
        true
    }
    pub fn save(&mut self) -> Option<(SaveRequest, T)> {
        if !self.editing || self.pending() {
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
    pub fn complete(&mut self, request: SaveRequest, result: Result<T, String>) -> bool {
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
            Err(error) => {
                self.error = Some(error);
            }
        }
        true
    }
    /// A new authoritative value supersedes any outstanding request. An open draft stays recoverable.
    pub fn set_committed(&mut self, value: T) {
        self.pending = None;
        self.error = None;
        self.committed = value.clone();
        if !self.editing {
            self.draft = value;
        }
    }
}
#[cfg(test)]
#[path = "state_tests.rs"]
mod tests;
