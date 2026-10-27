//! The agent's own menus (model, effort, mode…) and its "/" commands, both from `config/get`. The config is read when
//! the composer is pointed at a session and again when a turn ends, since the agent can change a value itself (leaving
//! plan mode, say); a pick sets it with `config/set` and shows what the agent answers. A read that fails leaves the
//! menus as they were (the pane already says when the machine is offline); a pick that fails says so above the box.
//! The "/" list opens while the draft is a command word, Up and Down move through it, Enter or Tab picks and Esc hides
//! it until the draft changes again.

use gpui_kit::component::input::{Enter, Escape, IndentInline, MoveDown, MoveUp};
use gpui_kit::*;
use serde_json::{Value, json};

use super::Composer;
use crate::features::session::client::session_client::CallError;
use crate::features::session::composer::agent_config::{AgentConfig, ConfigOption};
use crate::features::session::composer::slash_menu::SlashCommand;

impl Composer {
    /// Reads the session's config; a newer read takes the place of one still going. One a pick started or answered
    /// during is let go: it may answer the value from before the pick, and the pick's own answer is newer.
    pub(super) fn read_config(&mut self, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        let picks = self.picks;
        let key = format!("config/get:{session}");
        let args = json!({ "session": session });
        let call = self.client.call("config", "get", args, Some(&key), cx);
        self._config = Some(cx.spawn(async move |composer, cx| {
            let answer = call.await;
            composer
                .update(cx, |composer, cx| {
                    let current = composer.session == Some(session) && composer.picks == picks;
                    if let (true, Ok(config)) = (current, parse(answer)) {
                        composer.config = Some(config);
                        cx.notify();
                    }
                })
                .ok();
        }));
    }

    /// Sets one of the agent's menus to `value`.
    pub(super) fn choose(&mut self, config_id: String, value: String, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        let name = self
            .menus()
            .into_iter()
            .find(|(option, _)| option.id == config_id)
            .map_or(config_id.clone(), |(option, _)| option.name.clone());
        self.picks += 1;
        self._config = None;
        let args = json!({ "session": session, "configId": config_id, "value": value });
        let call = self.client.call("config", "set", args, None, cx);
        self._tasks.retain(|task| !task.is_ready());
        self._tasks.push(cx.spawn(async move |composer, cx| {
            let answer = call.await;
            composer
                .update(cx, |composer, cx| {
                    composer.picks += 1;
                    if composer.session != Some(session) {
                        return;
                    }
                    match parse(answer) {
                        Ok(config) => (composer.config, composer.error) = (Some(config), None),
                        Err(error) => {
                            composer.error =
                                Some(format!("Couldn't change {name}: {}", error.reason()).into())
                        }
                    }
                    cx.notify();
                })
                .ok();
        }));
    }

    /// The agent's menus that show (selects with choices), each with this session's value, or the agent's own.
    pub(super) fn menus(&self) -> Vec<(&ConfigOption, &str)> {
        let Some(config) = &self.config else {
            return Vec::new();
        };
        config
            .options
            .iter()
            .flatten()
            .filter(|option| option.kind == "select" && !option.options.is_empty())
            .map(|option| {
                let current = config
                    .choices
                    .get(&option.id)
                    .or(option.current_value.as_ref());
                (option, current.map_or("", String::as_str))
            })
            .collect()
    }

    /// The commands the "/" list shows now: none while it's hidden or the draft isn't a command word.
    pub(super) fn slash_list(&self, cx: &App) -> Vec<&SlashCommand> {
        let (Some(_), Some(config)) = (self.slash_selected, &self.config) else {
            return Vec::new();
        };
        let draft = self.draft.read(cx).value();
        match SlashCommand::query(&draft) {
            Some(query) => {
                SlashCommand::matches(config.commands.as_deref().unwrap_or_default(), query)
            }
            None => Vec::new(),
        }
    }

    /// The draft changed: the list opens on its first row while the draft is a command word.
    pub(super) fn draft_changed(&mut self, cx: &mut Context<Self>) {
        let draft = self.draft.read(cx).value();
        self.slash_selected = SlashCommand::query(&draft).map(|_| 0);
        self.slash_scroll.scroll_to_item(0);
        cx.notify();
    }

    /// Fills the draft with the command at `at` and selects its hint.
    pub(super) fn pick_command(&mut self, at: usize, window: &mut Window, cx: &mut Context<Self>) {
        let Some(command) = self
            .slash_list(cx)
            .get(at)
            .map(|command| (*command).clone())
        else {
            return;
        };
        let (text, hint) = command.pick();
        self.slash_selected = None;
        self.draft.update(cx, |draft, cx| {
            draft.set_value(text, window, cx);
            draft.set_selected_range(hint, cx);
            draft.focus(window, cx);
        });
        cx.notify();
    }

    /// The list's keys, caught before the draft sees them while it's open.
    pub(super) fn slash_keys(&self, root: Div, cx: &mut Context<Self>) -> Div {
        if self.slash_list(cx).is_empty() {
            return root;
        }
        let step = |by: isize| {
            move |composer: &mut Composer, cx: &mut Context<Composer>| {
                let shown = composer.slash_list(cx).len() as isize;
                if shown == 0 {
                    return;
                }
                let at = composer.slash_selected.unwrap_or_default() as isize;
                let at = (at.min(shown - 1) + by).rem_euclid(shown) as usize;
                composer.slash_selected = Some(at);
                composer.slash_scroll.scroll_to_item(at);
                cx.stop_propagation();
                cx.notify();
            }
        };
        let (up, down) = (step(-1), step(1));
        root.capture_action(cx.listener(move |composer, _: &MoveUp, _, cx| up(composer, cx)))
            .capture_action(cx.listener(move |composer, _: &MoveDown, _, cx| down(composer, cx)))
            .capture_action(cx.listener(|composer, enter: &Enter, window, cx| {
                if !enter.shift && !enter.secondary {
                    composer.pick_selected(window, cx);
                }
            }))
            .capture_action(cx.listener(|composer, _: &IndentInline, window, cx| {
                composer.pick_selected(window, cx)
            }))
            .capture_action(cx.listener(|composer, _: &Escape, _, cx| {
                composer.slash_selected = None;
                cx.stop_propagation();
                cx.notify();
            }))
    }

    /// The row lit now, kept in range when the list got shorter under it (a re-read config with fewer commands).
    pub(super) fn slash_lit(&self, cx: &App) -> usize {
        let shown = self.slash_list(cx).len();
        (self.slash_selected.unwrap_or_default()).min(shown.saturating_sub(1))
    }

    fn pick_selected(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.slash_list(cx).is_empty() {
            return;
        }
        self.pick_command(self.slash_lit(cx), window, cx);
        cx.stop_propagation();
    }
}

fn parse(answer: Result<Value, CallError>) -> Result<AgentConfig, CallError> {
    answer.and_then(|config| {
        serde_json::from_value(config).map_err(|error| CallError::Failed(error.to_string()))
    })
}

#[cfg(test)]
#[path = "composer_config_tests.rs"]
mod tests;
