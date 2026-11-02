//! Preview-only group: validate the complete draft before replacing its committed pair.
pub(super) struct GroupState {
    pub committed: [String; 2],
    pub draft: [String; 2],
    pub errors: [Option<&'static str>; 2],
    pub editing: bool,
}
impl GroupState {
    pub fn new(values: [String; 2]) -> Self {
        Self {
            committed: values.clone(),
            draft: values,
            errors: [None; 2],
            editing: false,
        }
    }
    pub fn edit(&mut self) {
        self.draft = self.committed.clone();
        self.errors = [None; 2];
        self.editing = true;
    }
    pub fn change(&mut self, index: usize, value: String) {
        if self.editing {
            self.draft[index] = value;
            self.errors[index] = None;
        }
    }
    pub fn cancel(&mut self) {
        self.draft = self.committed.clone();
        self.errors = [None; 2];
        self.editing = false;
    }
    pub fn save(&mut self) -> bool {
        if !self.editing {
            return false;
        }
        self.errors = self.draft.each_ref().map(|v| {
            if v.trim().is_empty() {
                Some("Required in this sample form.")
            } else {
                None
            }
        });
        if self.errors.iter().any(Option::is_some) {
            return false;
        }
        self.committed = self.draft.clone().map(|v| v.trim().to_string());
        self.draft = self.committed.clone();
        self.editing = false;
        true
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn validation_keeps_entire_draft_without_partial_commit_then_commits_together() {
        let mut group = GroupState::new(["Old name".into(), "Old destination".into()]);
        group.edit();
        group.change(0, " New name ".into());
        group.change(1, " ".into());
        assert!(!group.save());
        assert_eq!(group.committed, ["Old name", "Old destination"]);
        assert_eq!(group.draft, [" New name ", " "]);
        assert_eq!(group.errors, [None, Some("Required in this sample form.")]);
        assert!(group.editing);
        group.change(1, " New destination ".into());
        assert!(group.save());
        assert_eq!(group.committed, ["New name", "New destination"]);
        assert!(!group.editing);
    }
    #[test]
    fn cancel_restores_all_values_and_clears_all_errors() {
        let mut group = GroupState::new(["Name".into(), "Destination".into()]);
        group.edit();
        group.change(0, "".into());
        group.change(1, "".into());
        assert!(!group.save());
        assert!(group.errors.iter().all(Option::is_some));
        group.cancel();
        assert_eq!(group.draft, group.committed);
        assert_eq!(group.errors, [None; 2]);
        assert!(!group.editing);
        assert!(!group.save());
    }
}
