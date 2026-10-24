//! The guard policy for workflow agents: one matcher, checked in Rust at the
//! ACP permission handler for every provider. It is a first line, not the
//! guarantee: string rules can be dodged, and agents in Auto mode may never
//! ask. The engine's checks and GitHub's settings are the guarantee.

use serde::{Deserialize, Serialize};

/// The engine's own deny list, put in front of every workflow's policy. A
/// workflow's `policy:` block only adds to it; nothing in YAML can allow these.
/// Merges go through the orchestrator's merge tool, never an agent's shell.
pub(crate) const FIXED_DENY: &[&str] = &[
    "git reset --hard*",
    "gh pr merge*",
    "gh pr ready*",
    "git push * main*",
    "git push *:main*",
    "git push *:refs/heads/main*",
    "gh workflow run*",
    "gh api *dispatches*",
    "gh api *pulls/*/merge*",
    "git push *assembly-*",
];

/// Strictest wins: `Deny` beats `Ask`, `Ask` beats `Allow`.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, PartialOrd, Ord, Deserialize, Serialize)]
#[serde(rename_all = "lowercase")]
pub(crate) enum Decision {
    Allow,
    #[default]
    Ask,
    Deny,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct CommandRules {
    #[serde(default)]
    pub allow: Vec<String>,
    #[serde(default)]
    pub ask: Vec<String>,
    #[serde(default)]
    pub deny: Vec<String>,
}

/// A workflow's `policy:` block: `commands: { allow, ask, deny }` and the
/// `default` for a command no rule names. Patterns use `*` for any text.
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct GuardPolicy {
    #[serde(default)]
    pub commands: CommandRules,
    #[serde(default)]
    pub default: Decision,
}

impl GuardPolicy {
    /// Decides a shell command line. Each simple command in it (split on
    /// `&&`, `||`, `;`, `|`, newlines and `$(…)`) is judged on its own, with
    /// leading `NAME=value`, `env` and shell keywords removed, and the strictest
    /// answer wins. `sh -c` nested past `MAX_NESTING` goes to a person.
    pub(crate) fn decide(&self, command_line: &str) -> Decision {
        let Some(commands) = simple_commands(command_line, 0) else { return Decision::Ask };
        commands
            .iter()
            .map(|command| self.decide_one(command))
            .max()
            .unwrap_or(self.default)
    }

    fn decide_one(&self, command: &str) -> Decision {
        let matches = |patterns: &[String]| patterns.iter().any(|pattern| glob(pattern, command));
        if FIXED_DENY.iter().any(|pattern| glob(pattern, command)) || matches(&self.commands.deny) {
            Decision::Deny
        } else if matches(&self.commands.ask) {
            Decision::Ask
        } else if matches(&self.commands.allow) {
            Decision::Allow
        } else {
            self.default
        }
    }
}

/// How many `sh -c` scripts may sit inside each other before a person decides.
const MAX_NESTING: usize = 8;

/// The simple commands of a shell line, each as its words joined by one space;
/// `None` when `sh -c` is nested deeper than `MAX_NESTING`.
fn simple_commands(line: &str, depth: usize) -> Option<Vec<String>> {
    let mut commands: Vec<Vec<String>> = vec![Vec::new()];
    let mut word: Option<String> = None;
    let mut quote: Option<char> = None;
    let mut chars = line.chars().peekable();
    fn end_word(word: &mut Option<String>, commands: &mut [Vec<String>]) {
        if let Some(word) = word.take() {
            commands.last_mut().unwrap().push(word);
        }
    }
    while let Some(c) = chars.next() {
        match (quote, c) {
            // A string ends only at its own quote character.
            (Some(open), c) if c == open => quote = None,
            (Some('"'), '\\') | (None, '\\') => {
                // A backslash before a newline joins the lines.
                if let Some(next) = chars.next().filter(|next| *next != '\n') {
                    word.get_or_insert_with(String::new).push(next);
                }
            }
            (Some(_), c) => word.get_or_insert_with(String::new).push(c),
            (None, '\'' | '"') => {
                quote = Some(c);
                word.get_or_insert_with(String::new);
            }
            // `2>&1` and `&>` are redirections, not a background `&`.
            (None, '&') if word.as_deref().is_some_and(|w| w.ends_with('>')) || chars.peek() == Some(&'>') => {
                word.get_or_insert_with(String::new).push(c)
            }
            (None, ';' | '&' | '|' | '\n' | '(' | ')' | '`') => {
                end_word(&mut word, &mut commands);
                commands.push(Vec::new());
            }
            (None, '$') if chars.peek() == Some(&'(') => {
                chars.next();
                end_word(&mut word, &mut commands);
                commands.push(Vec::new());
            }
            (None, c) if c.is_whitespace() => end_word(&mut word, &mut commands),
            (None, c) => word.get_or_insert_with(String::new).push(c),
        }
    }
    end_word(&mut word, &mut commands);
    let mut simple = Vec::new();
    for words in commands {
        simple.extend(normalize(words, depth)?);
    }
    Some(simple)
}

/// Removes what doesn't change which program runs, and opens `sh -c` scripts.
fn normalize(mut words: Vec<String>, depth: usize) -> Option<Vec<String>> {
    let assignment = |word: &str| {
        word.split_once('=').is_some_and(|(name, _)| {
            !name.is_empty() && name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_')
        })
    };
    loop {
        match words.first() {
            Some(word) if assignment(word) => drop(words.remove(0)),
            // Loop and block keywords start the command that follows them.
            Some(word) if matches!(word.as_str(), "do" | "then" | "else" | "elif" | "if" | "while" | "until" | "{" | "}" | "!" | "done" | "fi") => {
                words.remove(0);
            }
            Some(word) if word == "env" => {
                words.remove(0);
                while words.first().is_some_and(|word| word.starts_with('-')) {
                    words.remove(0);
                }
            }
            _ => break,
        }
    }
    let Some(program) = words.first_mut() else { return Some(Vec::new()) };
    if let Some((_, name)) = program.rsplit_once('/') {
        *program = name.to_string();
    }
    let flag_has_c = |word: &str| word.starts_with('-') && !word.starts_with("--") && word.contains('c');
    if matches!(words[0].as_str(), "sh" | "bash" | "zsh" | "dash") && words.get(1).is_some_and(|w| flag_has_c(w)) {
        // A quoted script is one word; an argument list joined without quotes is the rest.
        return if depth < MAX_NESTING { simple_commands(&words[2..].join(" "), depth + 1) } else { None };
    }
    let options: &[&str] = match words[0].as_str() {
        "git" => &["-C", "-c"],
        "gh" => &["-R", "--repo"],
        _ => &[],
    };
    while words.len() > 2 && options.contains(&words[1].as_str()) {
        words.drain(1..3);
    }
    Some(vec![words.join(" ")])
}

/// Whole-string match where `*` stands for any text, including none.
fn glob(pattern: &str, text: &str) -> bool {
    let (pattern, text) = (pattern.as_bytes(), text.as_bytes());
    let (mut p, mut t) = (0, 0);
    let mut star: Option<(usize, usize)> = None;
    while t < text.len() {
        if p < pattern.len() && pattern[p] == b'*' {
            star = Some((p, t));
            p += 1;
        } else if p < pattern.len() && pattern[p] == text[t] {
            p += 1;
            t += 1;
        } else if let Some((star_p, star_t)) = star {
            p = star_p + 1;
            t = star_t + 1;
            star = Some((star_p, star_t + 1));
        } else {
            return false;
        }
    }
    pattern[p..].iter().all(|&b| b == b'*')
}

#[cfg(test)]
mod tests {
    use super::*;

    fn policy(allow: &[&str], ask: &[&str], deny: &[&str], default: Decision) -> GuardPolicy {
        let list = |items: &[&str]| items.iter().map(|item| item.to_string()).collect();
        GuardPolicy {
            commands: CommandRules { allow: list(allow), ask: list(ask), deny: list(deny) },
            default,
        }
    }

    #[test]
    fn the_fixed_deny_list_catches_merge_and_ready_in_any_shell_form() {
        let empty = GuardPolicy::default();
        for command in [
            "gh pr merge 12 --merge",
            "ALLOW_MERGE=1 gh pr merge 12 --merge",
            "env ALLOW_MERGE=1 gh pr merge 12",
            "env -i PATH=/bin gh pr merge 12",
            "cd repo && gh pr merge 12",
            "pnpm test; gh pr ready 12",
            "git status | cat && gh pr ready",
            "false || gh pr ready 12",
            "bash -lc 'gh pr merge 12 --squash'",
            "/bin/zsh -lc gh pr merge 12",
            "sh -c \"cd x && gh pr ready 3\"",
            "\"gh\" pr 'merge' 12",
            "gh  pr   merge",
            "echo $(gh pr merge 1)",
            "(cd x; gh pr ready)",
            "/usr/bin/gh pr merge 3",
            "git reset --hard HEAD~1",
            "git -C /work/tree reset --hard",
            "git -c core.pager=cat reset --hard",
            "git push origin main",
            "git push -u origin main",
            "gh workflow run release.yml",
            "gh api repos/o/r/dispatches -f event_type=x",
            "git push origin assembly-v1.2.3",
            "cargo test 2>&1 | tail -3\ngh pr merge 5",
        ] {
            assert_eq!(empty.decide(command), Decision::Deny, "{command}");
        }
    }

    #[test]
    fn accidental_forms_of_a_denied_command_are_denied() {
        let empty = GuardPolicy::default();
        for command in [
            "git push origin HEAD:main",
            "git push origin tsk-1:refs/heads/main",
            "git push origin +HEAD:main",
            "for n in 1 2 3; do gh pr merge $n; done",
            "if true; then gh pr merge 1; else gh pr ready 1; fi",
            "true && { gh pr merge 1; }",
            "! gh pr merge 1",
            "gh pr \\\nmerge 1",
            "git push \\\n  origin HEAD:main",
            "gh -R o/r pr merge 1",
            "gh --repo o/r pr merge 1",
            "gh api -X PUT repos/o/r/pulls/1/merge",
            "gh api repos/o/r/pulls/1/merge --method PUT",
        ] {
            assert_eq!(empty.decide(command), Decision::Deny, "{command}");
        }
        assert_eq!(empty.decide("gh api repos/o/r/pulls/1"), Decision::Ask);
        assert_eq!(empty.decide("for n in 1 2; do echo $n; done"), Decision::Ask);

        // A quote inside the other kind of quote does not end the string.
        let allow_printf = policy(&["printf *", "echo *"], &[], &[], Decision::Ask);
        for command in [
            r#"printf '%s\n' '{"ok":true}' && gh pr merge 1"#,
            r#"echo "it's" && gh pr merge 1"#,
        ] {
            assert_eq!(allow_printf.decide(command), Decision::Deny, "{command}");
        }
        assert_eq!(allow_printf.decide(r#"printf '%s' '{"a":1}'"#), Decision::Allow);
    }

    #[test]
    fn nesting_past_the_depth_limit_goes_to_a_person_and_stays_small() {
        let allow_all = policy(&[], &[], &[], Decision::Allow);
        let within = format!("{}gh pr merge 1", "sh -c ".repeat(5));
        assert_eq!(allow_all.decide(&within), Decision::Deny);
        let too_deep = format!("{}gh pr merge 1", "sh -c ".repeat(100_000));
        assert_eq!(allow_all.decide(&too_deep), Decision::Ask);
    }

    #[test]
    fn other_commands_and_quoted_text_are_not_denied() {
        let empty = GuardPolicy::default();
        for command in [
            "gh pr view 12",
            "gh pr create --draft --base main",
            "git push -u origin tsk-1596-policy-memory",
            "git reset --soft HEAD~1",
            "echo 'gh pr merge 12'",
            "grep -n \"gh pr ready\" notes.md",
            "git log --oneline origin/main..HEAD",
        ] {
            assert_eq!(empty.decide(command), Decision::Ask, "{command}");
        }
    }

    #[test]
    fn deny_beats_ask_beats_allow_and_unmatched_commands_take_the_default() {
        let policy = policy(&["pnpm test", "cargo test*", "pnpm install*"], &["pnpm install*"], &["rm -rf *"], Decision::Allow);
        assert_eq!(policy.decide("pnpm test"), Decision::Allow);
        assert_eq!(policy.decide("cargo test --no-default-features 2>&1 | tail -3"), Decision::Allow);
        assert_eq!(policy.decide("pnpm install --frozen-lockfile"), Decision::Ask);
        assert_eq!(policy.decide("pnpm test && rm -rf /"), Decision::Deny);
        assert_eq!(policy.decide("ls"), Decision::Allow);
        let strict = GuardPolicy { default: Decision::Deny, ..policy };
        assert_eq!(strict.decide("ls"), Decision::Deny);
        assert_eq!(strict.decide("pnpm test"), Decision::Allow);
        assert_eq!(strict.decide("pnpm test && ls"), Decision::Deny);
    }

    #[test]
    fn a_workflow_cannot_allow_what_the_engine_denies() {
        let policy = policy(&["gh pr merge*", "git push*"], &[], &[], Decision::Allow);
        assert_eq!(policy.decide("gh pr merge 12"), Decision::Deny);
        assert_eq!(policy.decide("git push origin main"), Decision::Deny);
        assert_eq!(policy.decide("git push origin tsk-1"), Decision::Allow);
    }

    #[test]
    fn the_yaml_block_reads_and_rejects_unknown_fields() {
        let parsed: GuardPolicy = serde_json::from_value(serde_json::json!({
            "commands": { "allow": ["pnpm test"], "deny": ["curl *"] }, "default": "deny"
        }))
        .unwrap();
        assert_eq!(parsed, policy(&["pnpm test"], &[], &["curl *"], Decision::Deny));
        assert_eq!(serde_json::from_value::<GuardPolicy>(serde_json::json!({})).unwrap(), GuardPolicy::default());
        assert!(serde_json::from_value::<GuardPolicy>(serde_json::json!({ "comands": {} })).is_err());
        assert!(serde_json::from_value::<GuardPolicy>(serde_json::json!({ "default": "maybe" })).is_err());
    }
}
