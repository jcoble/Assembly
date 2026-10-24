use super::{process::SidecarEnvironment, AcpClient};
use crate::agent_conversation::protocol::{AgentConversationProvider, AgentProviderManifest};
use serde_json::Value;
use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::sync::watch;

type Attempts = Arc<Mutex<HashMap<(String, u64), watch::Sender<bool>>>>;

#[derive(Clone, Default)]
pub(crate) struct Authentications(Attempts);

pub(crate) struct AuthenticationAttempt {
    attempts: Attempts,
    key: (String, u64),
    cancelled: watch::Receiver<bool>,
}

impl Authentications {
    pub fn begin(&self, owned_id: &str, generation: u64) -> Result<AuthenticationAttempt, String> {
        let mut attempts = self
            .0
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if !attempts.is_empty() {
            return Err("Sign-in is already in progress".into());
        }
        let key = (owned_id.to_string(), generation);
        let (sender, cancelled) = watch::channel(false);
        attempts.insert(key.clone(), sender);
        Ok(AuthenticationAttempt {
            attempts: self.0.clone(),
            key,
            cancelled,
        })
    }

    pub fn cancel(&self, owned_id: &str, generation: u64) -> bool {
        self.0
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .get(&(owned_id.to_string(), generation))
            .is_some_and(|sender| sender.send(true).is_ok())
    }

    pub fn pending(&self) -> bool {
        !self
            .0
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .is_empty()
    }
}

impl AuthenticationAttempt {
    pub fn was_cancelled(&self) -> bool {
        *self.cancelled.borrow()
    }

    pub async fn authenticate(
        &mut self,
        manifest: &AgentProviderManifest,
        provider: AgentConversationProvider,
        cwd: &Path,
    ) -> Result<(), String> {
        if self.was_cancelled() {
            return Err("Sign-in cancelled".into());
        }
        // A browser sign-in started by the remote server opens on a machine nobody is looking at.
        if std::env::args().any(|argument| argument == "--assembly-server") {
            return Err(sign_in_needed(provider));
        }
        let mut client = AcpClient::spawn_with_environment(
            manifest,
            cwd,
            &self.key.0,
            &SidecarEnvironment::default(),
        )
        .map_err(|error| error.to_string())?;
        let result = tokio::select! {
            biased;
            _ = self.cancelled.changed() => Err("Sign-in cancelled".to_string()),
            result = tokio::time::timeout(Duration::from_secs(300), client.authenticate(provider)) => {
                match result {
                    Ok(Ok(true)) => Ok(()),
                    Ok(Ok(false)) => Err(sign_in_needed(provider)),
                    Ok(Err(error)) => Err(error.to_string()),
                    Err(_) => Err("Sign-in timed out. Send again to retry.".into()),
                }
            }
        };
        // No pool membership or session context is retained by an auth attempt.
        let _ = client.detach().await;
        result
    }
}

impl Drop for AuthenticationAttempt {
    fn drop(&mut self) {
        self.attempts
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .remove(&self.key);
    }
}

// Whether an adapter's JSON-RPC error asks for sign-in, with or without the `acp-error: ` prefix.
// -32000 is ACP's auth_required code (Claude ACP's bundled library, TSK-1508 claude-0.93.0-additional.txt:6);
// Codex 2.2.2 and Antigravity 1.2.1 send it on session/new, Claude 0.88 on prompt. Claude 0.93 wraps it
// as -32603 with data.details "Authentication required" on prompt (TSK-1508 probe-unauth.log:5).
pub(crate) fn required(error: &str) -> bool {
    let json = error.split_once("acp-error: ").map_or(error, |(_, json)| json);
    serde_json::from_str::<Value>(json).is_ok_and(|error| {
        error.get("code").and_then(Value::as_i64) == Some(-32000)
            || error.pointer("/data/details").and_then(Value::as_str) == Some("Authentication required")
    })
}

// The first advertised sign-in Assembly can start with `authenticate` alone. A method with a `type`
// other than "agent" (Claude 0.93's terminal logins) or with `_meta` (Codex's api-key, which reads an
// environment variable) needs something Assembly does not supply. TSK-1508 initialize receipts:
// Antigravity oauth-personal, Codex chat-gpt, Claude none.
pub(crate) fn sign_in_method(initialize: &Value) -> Option<String> {
    initialize.get("authMethods")?.as_array()?.iter()
        .find(|method| method.get("type").is_none_or(|kind| kind == "agent") && method.get("_meta").is_none())
        .and_then(|method| method.get("id")?.as_str().map(str::to_string))
}

pub(crate) fn sign_in_needed(provider: AgentConversationProvider) -> String {
    let name = &provider.descriptor().display_name;
    format!("{name} needs you to sign in. Sign in with the {name} CLI where this session runs, then send again.")
}

// A failed prompt's error, in words when it asks for sign-in.
pub(crate) fn explain(provider: AgentConversationProvider, message: String) -> String {
    if required(&message) { sign_in_needed(provider) } else { message }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cancellation_is_generation_scoped_and_attempts_are_released() {
        let authentications = Authentications::default();
        let attempt = authentications.begin("session", 7).unwrap();
        assert!(authentications.pending());
        assert!(authentications.begin("other", 1).is_err());
        assert!(!authentications.cancel("session", 6));
        assert!(!attempt.was_cancelled());
        assert!(authentications.cancel("session", 7));
        assert!(attempt.was_cancelled());
        drop(attempt);
        assert!(!authentications.pending());
        assert!(authentications.begin("other", 1).is_ok());
    }
    #[tokio::test]
    async fn authentication_uses_advertised_method_and_cleans_process() {
        let root = std::env::temp_dir().join(format!("mcb-auth-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        let log = root.join("frames");
        let manifest = super::super::acp_client::tests::fixture_manifest_named(&log, "auth");
        let authentications = Authentications::default();
        let mut attempt = authentications.begin("session", 1).unwrap();
        attempt.authenticate(&manifest, AgentConversationProvider::Codex, &root).await.unwrap();
        let frames = std::fs::read_to_string(&log).unwrap();
        assert!(frames.contains("authenticate"));
        assert!(frames.contains("\"methodId\":\"chat-gpt\""));
        assert!(!frames.contains("api-key"));
        assert!(!frames.contains("session/new"));
        drop(attempt);
        assert!(!authentications.pending());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn stop_cancels_waiting_authentication_without_retaining_attempt() {
        let root = std::env::temp_dir().join(format!("mcb-auth-cancel-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        let log = root.join("frames");
        let manifest = super::super::acp_client::tests::fixture_manifest_named(&log, "auth_wait");
        let authentications = Authentications::default();
        let mut attempt = authentications.begin("session", 1).unwrap();
        let task_root = root.clone();
        let task = tokio::spawn(async move { attempt.authenticate(&manifest, AgentConversationProvider::Codex, &task_root).await });
        tokio::time::timeout(Duration::from_secs(5), async {
            while !std::fs::read_to_string(&log)
                .unwrap_or_default()
                .contains("authenticate")
            {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .unwrap();
        assert!(authentications.cancel("session", 1));
        let result = tokio::time::timeout(Duration::from_secs(5), task)
            .await
            .unwrap()
            .unwrap();
        assert!(result.unwrap_err().contains("cancelled"));
        assert!(!authentications.pending());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_provider_check_that_needs_sign_in_says_so() {
        let root = std::env::temp_dir().join(format!("mcb-auth-probe-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        let manifest = super::super::acp_client::tests::fixture_manifest_named(&root.join("frames"), "auth");
        let providers = super::super::ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)]).unwrap();
        let manager = crate::agent_conversation::manager::AgentRuntimeManager::new(providers);
        let error = manager.probe_provider_config(AgentConversationProvider::Claude, root.to_str().unwrap()).await.unwrap_err();
        assert_eq!(error, sign_in_needed(AgentConversationProvider::Claude));
        std::fs::remove_dir_all(root).unwrap();
    }

    // Adapter frames from the TSK-1508 unauthenticated probe (probe-unauth.log:2,5,9).
    const CODEX_SIGN_IN: &str = "acp-error: {\"code\":-32000,\"message\":\"Authentication required\"}";
    const CLAUDE_093_SIGN_IN: &str = "acp-error: {\"code\":-32603,\"message\":\"Internal error\",\"data\":{\"details\":\"Authentication required\"}}";

    #[test]
    fn sign_in_needed_is_recognised_for_every_adapter() {
        assert!(required(CODEX_SIGN_IN));
        assert!(required(CLAUDE_093_SIGN_IN));
        // Antigravity 1.2.1 puts its reason in data (server.py:1002).
        assert!(required("acp-error: {\"code\":-32000,\"message\":\"Authentication required\",\"data\":{\"message\":\"No authentication method selected.\"}}"));
        // A failed prompt records the frame without the prefix.
        assert!(required(CLAUDE_093_SIGN_IN.trim_start_matches("acp-error: ")));
        for other in [
            "acp-error: {\"code\":-32603}",
            "{\"code\":-32603,\"message\":\"Internal error: Prompt is too long\"}",
            "acp-error: permission denied",
            "transport: closed",
        ] {
            assert!(!required(other), "{other}");
        }
    }

    #[test]
    fn sign_in_method_picks_first_method_assembly_can_start() {
        // initialize authMethods from the TSK-1508 receipts (row-evidence.txt:67-80).
        let antigravity = serde_json::json!({"authMethods":[{"id":"oauth-personal","name":"Log in with Google"},{"id":"oauth-business","name":"Log in with Gemini Enterprise"},{"id":"gemini-api-key","name":"Gemini API key"},{"id":"agent-platform","name":"Gemini Enterprise Agent Platform"}]});
        let codex = serde_json::json!({"authMethods":[{"id":"api-key","name":"API Key","_meta":{"api-key":{"provider":"openai"}}},{"id":"chat-gpt","name":"ChatGPT"}]});
        // Claude 0.93 lists terminal logins only for clients that ask for terminal sign-in.
        let claude_terminal = serde_json::json!({"authMethods":[{"id":"claude-ai-login","name":"Claude Subscription","type":"terminal","args":["--cli","auth","login","--claudeai"]}]});
        assert_eq!(sign_in_method(&antigravity).as_deref(), Some("oauth-personal"));
        assert_eq!(sign_in_method(&codex).as_deref(), Some("chat-gpt"));
        assert_eq!(sign_in_method(&serde_json::json!({"authMethods":[]})), None);
        assert_eq!(sign_in_method(&claude_terminal), None);
    }

    #[tokio::test]
    async fn adapter_without_a_startable_method_gets_plain_sign_in_text() {
        let root = std::env::temp_dir().join(format!("mcb-auth-none-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        let log = root.join("frames");
        let manifest = super::super::acp_client::tests::fixture_manifest_named(&log, "agy");
        let authentications = Authentications::default();
        let mut attempt = authentications.begin("session", 1).unwrap();
        let error = attempt
            .authenticate(&manifest, AgentConversationProvider::Claude, &root)
            .await
            .unwrap_err();
        assert_eq!(error, sign_in_needed(AgentConversationProvider::Claude));
        assert!(!std::fs::read_to_string(&log).unwrap().contains("\"method\":\"authenticate\""));
        drop(attempt);
        assert!(!authentications.pending());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn remote_server_never_starts_browser_sign_in() {
        if !std::env::args().any(|arg| arg == "--assembly-server") {
            // libtest takes the server flag as a skip filter, so the child runs this test as the server process.
            let output = std::process::Command::new(std::env::current_exe().unwrap())
                .args(["--exact",
                    "agent_conversation::providers::authentication::tests::remote_server_never_starts_browser_sign_in",
                    "--skip", "--assembly-server", "--nocapture"])
                .output().unwrap();
            let stdout = String::from_utf8_lossy(&output.stdout);
            assert!(output.status.success() && stdout.contains("1 passed"), "{stdout}\n{}",
                String::from_utf8_lossy(&output.stderr));
            return;
        }
        let root = std::env::temp_dir().join(format!("mcb-auth-server-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        let log = root.join("frames");
        // This fixture advertises Codex's chat-gpt browser sign-in.
        let manifest = super::super::acp_client::tests::fixture_manifest_named(&log, "auth");
        let authentications = Authentications::default();
        let mut attempt = authentications.begin("session", 1).unwrap();
        let error = attempt
            .authenticate(&manifest, AgentConversationProvider::Codex, &root)
            .await
            .unwrap_err();
        assert_eq!(error, sign_in_needed(AgentConversationProvider::Codex));
        assert!(!std::fs::read_to_string(&log).unwrap_or_default().contains("\"method\":\"authenticate\""));
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn prompt_sign_in_error_is_explained() {
        let claude = AgentConversationProvider::Claude;
        assert_eq!(
            sign_in_needed(claude),
            "Claude needs you to sign in. Sign in with the Claude CLI where this session runs, then send again."
        );
        let frame = CLAUDE_093_SIGN_IN.trim_start_matches("acp-error: ").to_string();
        assert_eq!(explain(claude, frame), sign_in_needed(claude));
        let full = "{\"code\":-32603,\"message\":\"Internal error: Prompt is too long\"}";
        assert_eq!(explain(claude, full.to_string()), full);
    }
}
