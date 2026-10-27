//! `SlashCommand` is one of the agent's own commands for the "/" menu (ACP availableCommands, from `config/get`). A
//! pick only fills the draft; it's sent as a normal prompt. "$" names are Codex skills. Replaces slashMenu.ts.

use serde::Deserialize;

#[derive(Clone, Debug, PartialEq, Deserialize)]
pub struct SlashCommand {
    pub name: String,
    pub description: String,
    pub input: Option<SlashInput>,
}

/// ACP's command input: the hint a pick selects, so typing replaces it.
#[derive(Clone, Debug, PartialEq, Deserialize)]
pub struct SlashInput {
    pub hint: Option<String>,
}

impl SlashCommand {
    /// The command word being typed, or `None` once the draft isn't one.
    pub fn query(_draft: &str) -> Option<&str> {
        todo!()
    }

    /// Names starting with the query first, then names containing it; "__" names are hidden.
    pub fn matches<'a>(_commands: &'a [SlashCommand], _query: &str) -> Vec<&'a SlashCommand> {
        todo!()
    }

    /// The draft after a pick, and the byte range to select (the hint).
    pub fn pick(&self) -> (String, std::ops::Range<usize>) {
        todo!()
    }
}
