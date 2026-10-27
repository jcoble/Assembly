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
    /// The command word being typed, or `None` once the draft isn't one: it starts with "/", or "$" for Codex's
    /// skills, and the menu closes when arguments begin. A "$" stays in the query, so it matches those names.
    pub fn query(draft: &str) -> Option<&str> {
        let rest = match draft.strip_prefix('/') {
            Some(rest) => rest,
            None if draft.starts_with('$') => draft,
            None => return None,
        };
        (!rest.contains(char::is_whitespace)).then_some(rest)
    }

    /// Names starting with the query first, then names containing it; "__" names are hidden.
    pub fn matches<'a>(commands: &'a [SlashCommand], query: &str) -> Vec<&'a SlashCommand> {
        let query = query.to_lowercase();
        let shown = || {
            commands
                .iter()
                .filter(|command| !command.name.starts_with("__"))
        };
        let starts = |command: &&SlashCommand| command.name.to_lowercase().starts_with(&query);
        let contains = |command: &&SlashCommand| {
            !starts(command) && command.name.to_lowercase().contains(&query)
        };
        shown()
            .filter(starts)
            .chain(shown().filter(contains))
            .collect()
    }

    /// The draft after a pick, and the byte range to select (the hint). A "$" name goes in as is, with no slash.
    pub fn pick(&self) -> (String, std::ops::Range<usize>) {
        let head = format!(
            "{}{} ",
            if self.name.starts_with('$') { "" } else { "/" },
            self.name
        );
        let hint = self
            .input
            .as_ref()
            .and_then(|input| input.hint.as_deref())
            .unwrap_or_default();
        (format!("{head}{hint}"), head.len()..head.len() + hint.len())
    }
}
