//! What a Notion edit's receipt means for the field that asked for it. A `cache_stale` edit landed in Notion and only
//! the list's copy is behind, so it counts as saved and a refresh follows. An `uncertain` one may or may not have landed,
//! so the owner reads the task again and shows what Notion holds.

use crate::inline_field::SaveFailure;

use super::task_export::{NotionEditReceipt, NotionError};
use super::tasks_state::error_text;

/// What happens after a save's answer, beyond the field's own state.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AfterEdit {
    Nothing,
    /// The list's copy is behind Notion.
    RefreshList,
    /// What's on screen can't be trusted until the task is read again.
    Reread,
}

pub fn edit_outcome(
    result: Result<&NotionEditReceipt, &NotionError>,
) -> (Result<(), SaveFailure>, AfterEdit) {
    match result {
        Ok(receipt) => match receipt.state.as_str() {
            "confirmed" => (Ok(()), AfterEdit::Nothing),
            "cache_stale" => (Ok(()), AfterEdit::RefreshList),
            "uncertain" => (
                Err(SaveFailure::Uncertain(UNCERTAIN.into())),
                AfterEdit::Reread,
            ),
            other => (
                Err(SaveFailure::Refused(error_text(
                    receipt.error.as_deref().unwrap_or(other),
                ))),
                AfterEdit::Nothing,
            ),
        },
        // The call didn't come back, so the edit may have gone through.
        Err(error) if matches!(error.code.as_str(), "cancelled" | "disconnected") => (
            Err(SaveFailure::Uncertain(UNCERTAIN.into())),
            AfterEdit::Reread,
        ),
        // Someone else changed the task, so what's on screen is old too.
        Err(error) if error.code == "notion_changed" => (
            Err(SaveFailure::Refused(error_text(&error.code))),
            AfterEdit::Reread,
        ),
        Err(error) => (
            Err(SaveFailure::Refused(error_text(&error.code))),
            AfterEdit::Nothing,
        ),
    }
}

const UNCERTAIN: &str = "Assembly can't tell whether Notion took this. Reading it again…";

#[cfg(test)]
mod tests {
    use super::*;

    fn receipt(state: &str, error: Option<&str>) -> NotionEditReceipt {
        NotionEditReceipt {
            page_id: "p".into(),
            revision: "r".into(),
            state: state.into(),
            error: error.map(str::to_owned),
        }
    }

    fn failure(code: &str) -> NotionError {
        NotionError {
            code: code.into(),
            message: code.into(),
        }
    }

    #[test]
    fn a_confirmed_edit_is_saved() {
        assert_eq!(
            edit_outcome(Ok(&receipt("confirmed", None))),
            (Ok(()), AfterEdit::Nothing)
        );
    }

    #[test]
    fn a_stale_cache_counts_as_saved_and_refreshes_the_list() {
        assert_eq!(
            edit_outcome(Ok(&receipt("cache_stale", None))),
            (Ok(()), AfterEdit::RefreshList)
        );
    }

    #[test]
    fn an_uncertain_edit_is_not_saved_and_rereads() {
        let (saved, after) =
            edit_outcome(Ok(&receipt("uncertain", Some("notion_outcome_unknown"))));
        assert!(matches!(saved, Err(SaveFailure::Uncertain(_))));
        assert_eq!(after, AfterEdit::Reread);
    }

    #[test]
    fn a_lost_call_is_uncertain() {
        let (saved, after) = edit_outcome(Err(&failure("disconnected")));
        assert!(matches!(saved, Err(SaveFailure::Uncertain(_))));
        assert_eq!(after, AfterEdit::Reread);
    }

    #[test]
    fn a_refused_edit_keeps_the_draft_with_its_reason() {
        let (saved, after) = edit_outcome(Err(&failure("notion_http_403")));
        assert!(
            matches!(saved, Err(SaveFailure::Refused(reason)) if reason.contains("can't read"))
        );
        assert_eq!(after, AfterEdit::Nothing);
    }

    #[test]
    fn a_changed_task_is_refused_and_read_again() {
        let (saved, after) = edit_outcome(Err(&failure("notion_changed")));
        assert!(matches!(saved, Err(SaveFailure::Refused(_))));
        assert_eq!(after, AfterEdit::Reread);
    }
}
