//! What the Files panel says instead of rows. Pure: the pane says how its root stands, and this picks the words.

use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::error_code::ErrorCode;

pub const FOLDER_NOT_MADE: &str = "folder_not_made";

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum PanelMessage {
    NoSession,
    NoFolder,
    FolderNotMade,
    CheckoutDeleted,
    Waiting,
    Listing,
    Failed(String),
    EmptyFolder,
}

pub enum RootState<'a> {
    /// Nothing has been asked yet: the owner check, or the workspace key, comes first.
    Waiting,
    Listing,
    /// Stopped before the first answer. Nothing is known, so it never says the folder is empty.
    NotLoaded,
    Listed {
        has_rows: bool,
    },
    Failed(&'a CallFailure),
}

pub struct PanelInput<'a> {
    pub subject: bool,
    pub root: RootState<'a>,
}

/// `None` means draw the rows.
pub fn panel_message(input: PanelInput<'_>) -> Option<PanelMessage> {
    if !input.subject {
        return Some(PanelMessage::NoSession);
    }
    match input.root {
        RootState::Waiting => Some(PanelMessage::Waiting),
        RootState::Listing => Some(PanelMessage::Listing),
        RootState::NotLoaded | RootState::Listed { has_rows: true } => None,
        RootState::Listed { has_rows: false } => Some(PanelMessage::EmptyFolder),
        RootState::Failed(failure) => Some(from_failure(failure)),
    }
}

fn from_failure(failure: &CallFailure) -> PanelMessage {
    if let ErrorCode::Refused { code } = &failure.code {
        match code.as_str() {
            "no_folder" => return PanelMessage::NoFolder,
            FOLDER_NOT_MADE => return PanelMessage::FolderNotMade,
            "checkout_unavailable" => return PanelMessage::CheckoutDeleted,
            _ => {}
        }
    }
    PanelMessage::Failed(failure.message.clone())
}

impl PanelMessage {
    pub fn title(&self) -> &'static str {
        match self {
            Self::NoSession => "No session selected",
            Self::NoFolder => "This session has no folder",
            Self::FolderNotMade => "No folder yet",
            Self::CheckoutDeleted => "Checkout/Worktree deleted.",
            Self::Waiting => "Loading files…",
            Self::Listing => "Listing files…",
            Self::Failed(_) => "Could not list the files",
            Self::EmptyFolder => "No source files here",
        }
    }

    pub fn body(&self) -> String {
        match self {
            Self::NoSession => "Pick a session to view its files.".into(),
            Self::NoFolder => "It has only its conversation, so there are no files to show.".into(),
            Self::FolderNotMade => "This session's folder is made on its first message.".into(),
            Self::CheckoutDeleted => {
                "The conversation is still available, but this session’s project files no longer exist."
                    .into()
            }
            Self::Waiting => "Reading the selected session's project folder.".into(),
            Self::Listing => "Reading this project's top-level entries.".into(),
            Self::Failed(error) => error.clone(),
            Self::EmptyFolder => "This folder has no files the app can open.".into(),
        }
    }
}

#[cfg(test)]
#[path = "panel_message_tests.rs"]
mod tests;
