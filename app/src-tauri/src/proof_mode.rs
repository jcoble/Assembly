//! `--proof-mode <run-folder>`: the app under test, and the head server with `--assembly-server`,
//! keep their data in the run folder, start agents read-only and refuse actions that reach
//! outside the run (workflow redesign plan 3.11). The engine hides the owner's config by giving
//! the process its own `HOME`; this switch covers what `HOME` doesn't.

use std::io::BufRead;
use std::path::PathBuf;
use std::sync::OnceLock;

use crate::agent_conversation::providers::descriptors::{LaunchFor, LaunchSetting, ProviderDescriptor};

pub struct ProofMode {
    run_folder: PathBuf,
    /// The planted value and the fixture label, shown in the app; kept in memory only.
    label: String,
}

static ACTIVE: OnceLock<ProofMode> = OnceLock::new();

#[cfg(test)]
thread_local! {
    static TEST_ACTIVE: std::cell::Cell<Option<&'static ProofMode>> = const { std::cell::Cell::new(None) };
}

pub fn active() -> Option<&'static ProofMode> {
    #[cfg(test)]
    return TEST_ACTIVE.get();
    #[cfg(not(test))]
    ACTIVE.get()
}

/// `--print-stamp` prints the commit the binary was built from and exits. `--proof-mode` turns proof
/// mode on before anything reads the data folder; the app reads the planted value and the fixture
/// label as two lines on stdin.
pub fn start_from_process() -> Result<(), String> {
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|argument| argument == "--print-stamp") {
        println!("{}", env!("ASSEMBLY_BUILD_STAMP"));
        std::process::exit(0);
    }
    if let Some(mode) = parse(&args, std::io::stdin().lock())? {
        for (key, value) in mode.environment() {
            std::env::set_var(key, value);
        }
        let _ = ACTIVE.set(mode);
    }
    Ok(())
}

fn parse(args: &[String], stdin: impl BufRead) -> Result<Option<ProofMode>, String> {
    let Some(index) = args.iter().position(|argument| argument == "--proof-mode") else { return Ok(None) };
    if !cfg!(target_os = "linux") {
        return Err("Proof mode runs on Linux only".into());
    }
    let run_folder = args.get(index + 1).map(PathBuf::from).filter(|folder| folder.is_absolute())
        .ok_or("--proof-mode needs the run folder as an absolute path")?;
    let label = if args.iter().any(|argument| argument == "--assembly-server") {
        String::new()
    } else {
        let mut lines = stdin.lines();
        let mut line = || lines.next().transpose().map_err(|error| error.to_string());
        let value = line()?.filter(|value| !value.trim().is_empty())
            .ok_or("Proof mode reads the planted value and the fixture label as two lines on stdin")?;
        let fixture = line()?.unwrap_or_default();
        if fixture.trim().is_empty() { value.trim().to_string() } else { format!("{} · {}", value.trim(), fixture.trim()) }
    };
    std::fs::create_dir_all(run_folder.join("data")).map_err(|error| error.to_string())?;
    Ok(Some(ProofMode { run_folder, label }))
}

impl ProofMode {
    /// Tauri's app data folder is `$XDG_DATA_HOME/<identifier>` on Linux, so every caller of
    /// `app_data_dir` lands in the run's data folder; the head server's goes beside it.
    fn environment(&self) -> [(&'static str, PathBuf); 2] {
        let data = self.run_folder.join("data");
        [("ASSEMBLY_SERVER_DATA_DIR", data.join("assembly-server")), ("XDG_DATA_HOME", data)]
    }
}

/// `Err` naming `what` while proof mode is on.
pub fn refuse(what: &str) -> Result<(), String> {
    match active() {
        Some(_) => Err(format!("{what} is off in proof mode")),
        None => Ok(()),
    }
}

/// App commands, and the head server's workspace operations of the same name, that reach outside
/// the run. Shared code guards the rest with `refuse`.
const REFUSED_COMMANDS: &[&str] = &[
    "start_terminal_session", "submit_github_pull_request_review", "reply_github_pull_request_comment",
    "comment_github_pull_request", "merge_github_pull_request",
    "connect_remote_assembly", "install_remote_assembly", "uninstall_remote_assembly",
    "remove_remote_assembly_profile", "deploy_remote_backend_dev",
    "update_provider_cli", "install_provider_updates", "restart_for_provider_updates",
    "install_remote_provider_updates", "restart_remote_for_provider_updates",
    "open_terminal_path", "open_terminal_command",
    "begin_agent_conversation_import", "extend_agent_conversation_import", "finish_agent_conversation_import",
    "create_workflow_run", "start_workflow_run",
];

/// Wraps the app's command handler: answers `read_proof_mode_label` (the label the app shows, or
/// none) and, in proof mode, answers refused commands with an error.
pub fn guard<R: tauri::Runtime>(
    handler: impl Fn(tauri::ipc::Invoke<R>) -> bool + Send + Sync + 'static,
) -> impl Fn(tauri::ipc::Invoke<R>) -> bool + Send + Sync + 'static {
    move |invoke| {
        let command = invoke.message.command();
        if command == "read_proof_mode_label" {
            invoke.resolver.resolve(active().map(|mode| mode.label.clone()));
            return true;
        }
        if active().is_none() {
            return handler(invoke);
        }
        let refused = match invoke.message.payload() {
            tauri::ipc::InvokeBody::Json(args) => refuse_command(command, args),
            _ => refuse_command(command, &serde_json::Value::Null),
        };
        match refused {
            Ok(()) => handler(invoke),
            Err(error) => {
                invoke.resolver.reject(error);
                true
            }
        }
    }
}

/// Refused commands, and mode changes: the mode picker is locked.
pub fn refuse_command(command: &str, args: &serde_json::Value) -> Result<(), String> {
    let changes_mode = (command == "set_agent_conversation_config_option" && args["request"]["optionId"] == "mode")
        || (command == "set_agent_conversation_config" && !args["request"]["approvalPolicy"].is_null());
    if changes_mode { return refuse("Changing the mode") }
    if REFUSED_COMMANDS.contains(&command) { refuse(command) } else { Ok(()) }
}

/// Past sessions from provider transcript folders; none in proof mode.
pub fn past_sessions<T>(scan: impl FnOnce() -> Vec<T>) -> Vec<T> {
    if active().is_some() { Vec::new() } else { scan() }
}

/// A session's launch settings. In proof mode it starts read-only, and a provider without a
/// `read_only` setting can't start.
pub fn session_launch(descriptor: &ProviderDescriptor, pre_approval: bool) -> Result<LaunchSetting, String> {
    let read_only = active().is_some();
    descriptor.launch(LaunchFor { pre_approval, read_only, ..LaunchFor::default() }).ok_or_else(|| {
        format!("{} can't run read-only, so it can't start in proof mode", descriptor.display_name)
    })
}

/// A mode a send or config change asks for; dropped in proof mode, where the mode is locked.
pub fn requested_mode(mode: Option<String>) -> Option<String> {
    mode.filter(|_| active().is_none())
}

/// Proof mode on for this test's thread only, so other tests run normally.
#[cfg(test)]
pub fn turn_on_for_this_thread(stdin: &str) {
    let folder = std::env::temp_dir().join(format!("assembly-proof-mode-{}", uuid::Uuid::new_v4()));
    let args = ["assembly".into(), "--proof-mode".into(), folder.display().to_string()];
    TEST_ACTIVE.set(Some(Box::leak(Box::new(parse(&args, stdin.as_bytes()).unwrap().unwrap()))));
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;
    use crate::agent_conversation::providers::descriptors::for_adapter;

    fn run_folder() -> PathBuf {
        std::env::temp_dir().join(format!("assembly-proof-mode-{}", uuid::Uuid::new_v4()))
    }

    fn args(folder: &Path) -> Vec<String> {
        vec!["assembly".into(), "--proof-mode".into(), folder.display().to_string()]
    }

    #[test]
    fn the_data_folder_is_the_run_folder() {
        let folder = run_folder();
        let mode = parse(&args(&folder), "planted\n\n".as_bytes()).unwrap().unwrap();
        assert!(folder.join("data").is_dir());
        assert_eq!(mode.environment(), [
            ("ASSEMBLY_SERVER_DATA_DIR", folder.join("data/assembly-server")),
            ("XDG_DATA_HOME", folder.join("data")),
        ]);
        let mut server_args = args(&folder);
        server_args.push("--assembly-server".into());
        assert_eq!(parse(&server_args, "".as_bytes()).unwrap().unwrap().label, "", "the server reads no stdin");
        assert!(parse(&["assembly".into(), "--proof-mode".into(), "relative".into()], "v\n".as_bytes()).is_err());
        assert!(parse(&["assembly".into()], "".as_bytes()).unwrap().is_none());
        let _ = std::fs::remove_dir_all(folder);
    }

    #[test]
    fn no_transcripts_or_saved_connections_load() {
        assert_eq!(past_sessions(|| vec![1]), vec![1]);
        turn_on_for_this_thread("planted\n\n");
        assert!(past_sessions(|| -> Vec<u8> { panic!("scanned transcripts") }).is_empty());
        for command in ["begin_agent_conversation_import", "connect_remote_assembly", "install_remote_assembly",
            "remove_remote_assembly_profile", "update_provider_cli", "start_workflow_run"] {
            assert!(refuse_command(command, &serde_json::Value::Null).is_err(), "{command}");
        }
        assert!(refuse_command("list_agent_sessions", &serde_json::Value::Null).is_ok());
        let handler = include_str!("main.rs");
        for command in REFUSED_COMMANDS {
            assert!(handler.contains(&format!("{command},")), "{command} is not one of the app's commands");
        }
    }

    #[test]
    fn the_terminal_keyring_and_github_writes_are_refused() {
        let none = serde_json::Value::Null;
        assert!(refuse_command("start_terminal_session", &none).is_ok(), "only in proof mode");
        assert_eq!(requested_mode(Some("auto".into())), Some("auto".into()));
        turn_on_for_this_thread("planted\n\n");
        for command in ["start_terminal_session", "open_terminal_path", "merge_github_pull_request",
            "comment_github_pull_request", "reply_github_pull_request_comment", "submit_github_pull_request_review"] {
            assert_eq!(refuse_command(command, &none).unwrap_err(), format!("{command} is off in proof mode"));
        }
        assert!(refuse_command("read_github_pull_request", &none).is_ok());
        assert!(crate::helper::keychain::read_key("openai").is_err());
        assert!(crate::helper::keychain::write_key("openai", "key").is_err());
        assert!(crate::helper::keychain::delete_key("openai").is_err());
        let mode = serde_json::json!({"request": {"optionId": "mode"}});
        assert_eq!(refuse_command("set_agent_conversation_config_option", &mode).unwrap_err(), "Changing the mode is off in proof mode");
        let model = serde_json::json!({"request": {"optionId": "model"}});
        assert!(refuse_command("set_agent_conversation_config_option", &model).is_ok());
        assert_eq!(requested_mode(Some("auto".into())), None, "a send that asks for a mode keeps the session's mode");
        let policy = serde_json::json!({"request": {"approvalPolicy": "auto", "model": null}});
        assert!(refuse_command("set_agent_conversation_config", &policy).is_err());
        assert!(refuse_command("set_agent_conversation_config", &serde_json::json!({"request": {"model": "m"}})).is_ok());
    }

    #[test]
    fn in_app_sessions_start_read_only_and_a_provider_without_read_only_cannot_start() {
        let options = |launch: LaunchSetting| serde_json::Value::Object(launch.meta)["claudeCode"]["options"].clone();
        let claude = for_adapter("claude-agent-acp").unwrap();
        assert!(options(session_launch(claude, true).unwrap())["tools"].is_null());
        turn_on_for_this_thread("planted\n\n");
        let launch = options(session_launch(claude, true).unwrap());
        assert_eq!(launch["tools"], serde_json::json!([]));
        assert!(launch["allowedTools"].is_array());
        let codex = session_launch(for_adapter("codex-acp").unwrap(), false).unwrap();
        assert_eq!(codex.env["CODEX_CONFIG"]["sandbox_mode"], "read-only");
        assert_eq!(session_launch(for_adapter("agy-acp").unwrap(), true).unwrap_err(),
            "Antigravity can't run read-only, so it can't start in proof mode");
    }

    #[test]
    fn the_planted_value_is_read_from_stdin_and_never_written_to_disk() {
        let planted = format!("planted-{}", uuid::Uuid::new_v4());
        let folder = run_folder();
        let mode = parse(&args(&folder), format!("{planted}\nempty-list\n").as_bytes()).unwrap().unwrap();
        assert_eq!(mode.label, format!("{planted} · empty-list"));
        assert_eq!(parse(&args(&folder), format!("{planted}\n").as_bytes()).unwrap().unwrap().label, planted);
        assert!(parse(&args(&folder), "".as_bytes()).is_err(), "no planted value");
        let written = walk(&folder).into_iter().any(|file| std::fs::read(&file).is_ok_and(|bytes|
            String::from_utf8_lossy(&bytes).contains(&planted)));
        assert!(!written);
        let _ = std::fs::remove_dir_all(folder);
    }

    fn walk(folder: &Path) -> Vec<PathBuf> {
        std::fs::read_dir(folder).into_iter().flatten().flatten().flat_map(|entry| {
            let path = entry.path();
            if path.is_dir() { walk(&path) } else { vec![path] }
        }).collect()
    }

    #[test]
    fn build_stamp_matches_git_head() {
        let head = std::process::Command::new("git").args(["rev-parse", "HEAD"])
            .current_dir(env!("CARGO_MANIFEST_DIR")).output().unwrap();
        assert_eq!(env!("ASSEMBLY_BUILD_STAMP"), String::from_utf8_lossy(&head.stdout).trim());
    }
}
