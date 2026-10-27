//! The "/" menu, ported from app/test/slashMenu.test.ts.

use super::slash_menu::{SlashCommand, SlashInput};

fn commands() -> Vec<SlashCommand> {
    let command = |name: &str, hint: Option<&str>| SlashCommand {
        name: name.into(),
        description: String::new(),
        input: hint.map(|hint| SlashInput {
            hint: Some(hint.into()),
        }),
    };
    vec![
        command("compact", None),
        command("context", None),
        command("autocompact", None),
        command("__remote-workflow", None),
        command("review", Some("<branch or PR>")),
        command("$imagegen", None),
    ]
}

fn names(commands: &[SlashCommand], query: &str) -> Vec<String> {
    SlashCommand::matches(commands, query)
        .into_iter()
        .map(|command| command.name.clone())
        .collect()
}

// Pins: g0-session#26
#[test]
fn the_menu_belongs_to_a_leading_command_word_only() {
    assert_eq!(SlashCommand::query("/"), Some(""));
    assert_eq!(SlashCommand::query("/con"), Some("con"));
    assert_eq!(SlashCommand::query("/context now"), None);
    assert_eq!(SlashCommand::query(" /context"), None);
    assert_eq!(SlashCommand::query("hi /context"), None);
}

// Pins: g0-session#26
#[test]
fn start_of_name_matches_come_first_then_anywhere_and_double_underscore_names_are_hidden() {
    let commands = commands();
    assert_eq!(names(&commands, "comp"), ["compact", "autocompact"]);
    assert_eq!(
        names(&commands, ""),
        ["compact", "context", "autocompact", "review", "$imagegen"]
    );
    assert!(names(&commands, "REM").is_empty());
}

// Pins: g0-session#26
#[test]
fn a_pick_inserts_the_name_and_selects_its_input_hint() {
    let commands = commands();
    assert_eq!(commands[1].pick(), ("/context ".into(), 9..9));
    assert_eq!(commands[4].pick(), ("/review <branch or PR>".into(), 8..22));
}

// Pins: g0-session#26
#[test]
fn codex_skills_go_by_their_dollar_names_typed_with_a_dollar_or_found_after_a_slash_picked_with_no_slash()
 {
    let commands = commands();
    assert_eq!(SlashCommand::query("$ima"), Some("$ima"));
    assert_eq!(names(&commands, "$ima"), ["$imagegen"]);
    assert_eq!(names(&commands, "image"), ["$imagegen"]);
    assert_eq!(commands[5].pick(), ("$imagegen ".into(), 10..10));
}
