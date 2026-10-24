use crate::claude_quota::{
    now_ms_u64, read_credentials, ClaudeCredentialError, ClaudeCredentials, ClaudeQuotaClient,
    ClaudeQuotaSnapshot, ClaudeRequestError,
};
use std::sync::OnceLock;
use std::time::{Duration, Instant, SystemTime};
use tokio::sync::Mutex;

const CACHE_TTL: Duration = Duration::from_secs(5 * 60);
const DEFAULT_RETRY_AFTER: Duration = Duration::from_secs(60);

pub async fn read_claude_quota() -> Result<ClaudeQuotaSnapshot, RemoteQuotaError> {
    static REMOTE: OnceLock<Result<UsageRemote, ClaudeRequestError>> = OnceLock::new();
    match REMOTE.get_or_init(UsageRemote::new) {
        Ok(remote) => remote.read().await,
        Err(error) => Err(RemoteQuotaError::Request(error.clone())),
    }
}

pub struct UsageRemote {
    claude: ClaudeQuotaClient,
    state: Mutex<RemoteState>,
}

#[derive(Default)]
struct RemoteState {
    cached: Option<CachedQuota>,
    retry_gate: RetryAfterGate,
}

struct CachedQuota {
    fetched_at: Instant,
    snapshot: ClaudeQuotaSnapshot,
}

#[derive(Debug, Default)]
struct RetryAfterGate {
    until: Option<Instant>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RemoteQuotaError {
    Credential(ClaudeCredentialError),
    Request(ClaudeRequestError),
    /// The sign-in could not be renewed, so usage was never asked for. Kept
    /// apart from `Request`: both used to read "usage request failed", which
    /// sent a whole investigation at the wrong endpoint.
    Refresh(ClaudeRequestError),
    /// The sign-in has run out and it is not this app's to renew — it was read
    /// from Claude Code's Keychain item, and Claude Code renews it as it works.
    SignInExpired,
    RateLimited {
        retry_after_seconds: u64,
    },
}

impl RemoteQuotaError {
    pub fn is_unavailable(&self) -> bool {
        matches!(self, Self::Credential(_))
    }

    pub fn reason(&self) -> String {
        match self {
            Self::Credential(error) => error.reason().to_string(),
            Self::RateLimited {
                retry_after_seconds,
            } => format!(
                "Claude usage request failed with HTTP status 429; retry after {retry_after_seconds} seconds"
            ),
            Self::Request(ClaudeRequestError::Http { status, .. }) => {
                format!("Claude usage request failed with HTTP status {status}")
            }
            Self::Request(ClaudeRequestError::Transport) => {
                "Claude usage request failed before receiving an HTTP status code".to_string()
            }
            Self::Request(ClaudeRequestError::Decode) => {
                "Claude usage response could not be decoded".to_string()
            }
            Self::Refresh(ClaudeRequestError::Http { status, .. }) => format!(
                "Claude sign-in could not be renewed (HTTP status {status}); open Claude Code to sign in again"
            ),
            Self::Refresh(_) => {
                "Claude sign-in could not be renewed; open Claude Code to sign in again".to_string()
            }
            Self::SignInExpired => {
                "Claude sign-in has run out; use Claude Code once and it renews itself".to_string()
            }
            Self::Request(ClaudeRequestError::CredentialWrite) => {
                "Claude credential refresh could not be persisted".to_string()
            }
        }
    }
}

impl UsageRemote {
    fn new() -> Result<Self, ClaudeRequestError> {
        Ok(Self {
            claude: ClaudeQuotaClient::new()?,
            state: Mutex::new(RemoteState::default()),
        })
    }

    #[cfg(test)]
    fn with_client(claude: ClaudeQuotaClient) -> Self {
        Self {
            claude,
            state: Mutex::new(RemoteState::default()),
        }
    }

    async fn read(&self) -> Result<ClaudeQuotaSnapshot, RemoteQuotaError> {
        let mut state = self.state.lock().await;
        let now = Instant::now();
        if let Some(remaining) = state.retry_gate.remaining(now) {
            return Err(RemoteQuotaError::RateLimited {
                retry_after_seconds: remaining.as_secs().max(1),
            });
        }
        if let Some(cached) = state
            .cached
            .as_ref()
            .filter(|cached| now.duration_since(cached.fetched_at) < CACHE_TTL)
        {
            return Ok(cached.snapshot.clone());
        }
        let credentials = read_credentials().map_err(RemoteQuotaError::Credential)?;
        self.fetch_and_cache(&mut state, credentials).await
    }

    async fn fetch_and_cache(
        &self,
        state: &mut RemoteState,
        mut credentials: ClaudeCredentials,
    ) -> Result<ClaudeQuotaSnapshot, RemoteQuotaError> {
        let mut refreshed = false;
        if credentials.is_expired(now_ms_u64()) {
            if !credentials.can_renew() {
                return Err(RemoteQuotaError::SignInExpired);
            }
            self.claude
                .refresh_credentials(&mut credentials)
                .await
                .map_err(RemoteQuotaError::Refresh)?;
            refreshed = true;
        }
        let first = self.claude.fetch_usage(&credentials).await;
        let result = if matches!(
            first.as_ref().err().and_then(ClaudeRequestError::status),
            Some(401)
        ) && !refreshed
        {
            if !credentials.can_renew() {
                return Err(RemoteQuotaError::SignInExpired);
            }
            self.claude
                .refresh_credentials(&mut credentials)
                .await
                .map_err(RemoteQuotaError::Refresh)?;
            self.claude.fetch_usage(&credentials).await
        } else {
            first
        };
        match result {
            Ok(snapshot) => {
                state.retry_gate.clear();
                state.cached = Some(CachedQuota {
                    fetched_at: Instant::now(),
                    snapshot: snapshot.clone(),
                });
                Ok(snapshot)
            }
            Err(error) if error.status() == Some(429) => {
                let retry_after = parse_retry_after(error.retry_after(), SystemTime::now())
                    .unwrap_or(DEFAULT_RETRY_AFTER);
                state.retry_gate.block(Instant::now(), retry_after);
                Err(RemoteQuotaError::RateLimited {
                    retry_after_seconds: retry_after.as_secs().max(1),
                })
            }
            Err(error) => Err(RemoteQuotaError::Request(error)),
        }
    }
}

impl RetryAfterGate {
    fn block(&mut self, now: Instant, duration: Duration) {
        self.until = Some(now + duration);
    }

    fn remaining(&mut self, now: Instant) -> Option<Duration> {
        let until = self.until?;
        if until <= now {
            self.until = None;
            return None;
        }
        Some(until.duration_since(now))
    }

    fn clear(&mut self) {
        self.until = None;
    }
}

fn parse_retry_after(value: Option<&str>, now: SystemTime) -> Option<Duration> {
    let value = value?.trim();
    if let Ok(seconds) = value.parse::<u64>() {
        return Some(Duration::from_secs(seconds));
    }
    let target = httpdate::parse_http_date(value).ok()?;
    target.duration_since(now).ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::claude_quota::read_credentials_at;
    use serde_json::{json, Value};
    use std::fs;
    use std::io::{Read, Write};
    use std::net::{TcpListener, TcpStream};
    use std::os::unix::fs::PermissionsExt;
    use std::path::PathBuf;
    use std::sync::{Arc, Mutex as StdMutex};
    use std::thread;

    fn read_http_request(stream: &mut TcpStream) -> String {
        stream
            .set_read_timeout(Some(Duration::from_secs(5)))
            .expect("read timeout should be set");
        let mut bytes = Vec::new();
        let mut buffer = [0_u8; 1024];
        loop {
            let count = stream.read(&mut buffer).expect("request should be read");
            if count == 0 {
                break;
            }
            bytes.extend_from_slice(&buffer[..count]);
            let header_end = bytes
                .windows(4)
                .position(|window| window == b"\r\n\r\n")
                .map(|index| index + 4);
            let Some(header_end) = header_end else {
                continue;
            };
            let headers = String::from_utf8_lossy(&bytes[..header_end]);
            let content_length = headers
                .lines()
                .find_map(|line| {
                    let (name, value) = line.split_once(':')?;
                    name.eq_ignore_ascii_case("content-length")
                        .then(|| value.trim().parse::<usize>().ok())
                        .flatten()
                })
                .unwrap_or_default();
            if bytes.len() >= header_end + content_length {
                break;
            }
        }
        String::from_utf8(bytes).expect("request should be UTF-8")
    }

    fn write_http_response(stream: &mut TcpStream, status: &str, body: &str) {
        let response = format!(
            "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        );
        stream
            .write_all(response.as_bytes())
            .expect("response should be written");
    }

    fn temporary_credential_path() -> (PathBuf, PathBuf) {
        let directory = std::env::temp_dir().join(format!(
            "mcb-usage-remote-{}-{}",
            std::process::id(),
            now_ms_u64()
        ));
        fs::create_dir(&directory).expect("temporary directory should be created");
        let path = directory.join(".credentials.json");
        let document = json!({
            "claudeAiOauth": {
                "accessToken": "redacted-old-access",
                "refreshToken": "redacted-old-refresh",
                "expiresAt": now_ms_u64() + 3_600_000,
                "scopes": ["user:profile"],
                "rateLimitTier": "max_20x"
            }
        });
        fs::write(
            &path,
            serde_json::to_vec_pretty(&document).expect("credential fixture should encode"),
        )
        .expect("credential fixture should be written");
        fs::set_permissions(&path, fs::Permissions::from_mode(0o600))
            .expect("credential fixture mode should be set");
        (directory, path)
    }

    #[test]
    fn usage_remote_retry_after_gate_blocks_until_deadline() {
        let start = Instant::now();
        let mut gate = RetryAfterGate::default();
        gate.block(start, Duration::from_secs(90));
        assert_eq!(
            gate.remaining(start + Duration::from_secs(30)),
            Some(Duration::from_secs(60))
        );
        assert_eq!(gate.remaining(start + Duration::from_secs(90)), None);
        assert_eq!(gate.remaining(start + Duration::from_secs(91)), None);
    }

    #[test]
    fn usage_remote_retry_after_parser_accepts_seconds_and_http_date() {
        let now = SystemTime::UNIX_EPOCH + Duration::from_secs(1_000);
        assert_eq!(
            parse_retry_after(Some("120"), now),
            Some(Duration::from_secs(120))
        );
        let target = httpdate::fmt_http_date(now + Duration::from_secs(45));
        assert_eq!(
            parse_retry_after(Some(&target), now),
            Some(Duration::from_secs(45))
        );
    }

    #[test]
    fn usage_remote_error_messages_never_include_response_content() {
        let error = RemoteQuotaError::Request(ClaudeRequestError::Http {
            status: 403,
            retry_after: None,
        });
        assert_eq!(
            error.reason(),
            "Claude usage request failed with HTTP status 403"
        );
    }

    #[test]
    fn usage_remote_cache_ttl_is_five_minutes() {
        assert_eq!(CACHE_TTL, Duration::from_secs(300));
    }

    #[test]
    fn usage_remote_test_client_constructor_is_available() {
        let client = ClaudeQuotaClient::with_endpoints(
            "http://127.0.0.1:1/usage".to_string(),
            "http://127.0.0.1:1/token".to_string(),
        )
        .expect("test client should build");
        let remote = UsageRemote::with_client(client);
        assert!(remote.state.try_lock().is_ok());
    }

    #[test]
    fn usage_remote_401_refresh_persists_rotation_and_retries_once() {
        let listener = TcpListener::bind("127.0.0.1:0").expect("test server should bind");
        let address = listener.local_addr().expect("test address should resolve");
        let requests = Arc::new(StdMutex::new(Vec::new()));
        let server_requests = Arc::clone(&requests);
        let server = thread::spawn(move || {
            let responses = [
                ("401 Unauthorized", "{}"),
                (
                    "200 OK",
                    r#"{"access_token":"redacted-new-access","refresh_token":"redacted-new-refresh","expires_in":3600,"token_type":"bearer"}"#,
                ),
                (
                    "200 OK",
                    r#"{"five_hour":{"utilization":23.5,"resets_at":"2026-08-10T18:00:00Z"},"seven_day":null}"#,
                ),
            ];
            for (status, body) in responses {
                let (mut stream, _) = listener.accept().expect("test request should connect");
                let request = read_http_request(&mut stream);
                server_requests
                    .lock()
                    .expect("request list should lock")
                    .push(request);
                write_http_response(&mut stream, status, body);
            }
        });
        let base = format!("http://{address}");
        let client = ClaudeQuotaClient::with_endpoints(
            format!("{base}/api/oauth/usage"),
            format!("{base}/v1/oauth/token"),
        )
        .expect("test client should build");
        let remote = UsageRemote::with_client(client);
        let (directory, credential_path) = temporary_credential_path();
        let credentials = read_credentials_at(credential_path.clone())
            .expect("credential fixture should be valid");
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("test runtime should build");
        let mut state = RemoteState::default();
        let snapshot = runtime
            .block_on(remote.fetch_and_cache(&mut state, credentials))
            .expect("401 should refresh and retry once");
        server.join().expect("test server should stop");
        assert_eq!(snapshot.account.as_deref(), Some("Max 20x"));
        assert_eq!(snapshot.windows.len(), 1);
        let requests = requests.lock().expect("request list should lock");
        assert_eq!(requests.len(), 3);
        assert!(requests[0].starts_with("GET /api/oauth/usage "));
        assert!(requests[1].starts_with("POST /v1/oauth/token "));
        assert!(requests[2].starts_with("GET /api/oauth/usage "));
        let usage_request = requests[0].to_ascii_lowercase();
        assert!(usage_request.contains("authorization: bearer redacted-old-access"));
        assert!(usage_request.contains("accept: application/json"));
        assert!(usage_request.contains("content-type: application/json"));
        assert!(usage_request.contains("anthropic-beta: oauth-2025-04-20"));
        assert!(usage_request.contains("user-agent: claude-code"));
        let refresh_request = requests[1].to_ascii_lowercase();
        assert!(refresh_request.contains("content-type: application/x-www-form-urlencoded"));
        assert!(refresh_request.contains("grant_type=refresh_token"));
        assert!(refresh_request.contains("client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e"));
        drop(requests);
        let persisted = serde_json::from_slice::<Value>(
            &fs::read(&credential_path).expect("persisted credential should be readable"),
        )
        .expect("persisted credential should decode");
        assert_eq!(
            persisted.pointer("/claudeAiOauth/refreshToken"),
            Some(&Value::String("redacted-new-refresh".to_string()))
        );
        assert_eq!(
            fs::metadata(&credential_path)
                .expect("persisted credential should have metadata")
                .permissions()
                .mode()
                & 0o777,
            0o600
        );
        fs::remove_file(&credential_path).expect("temporary credential should be removed");
        fs::remove_dir(&directory).expect("temporary directory should be removed");
    }

    #[test]
    #[ignore = "calls the real provider endpoint with the selected local credential"]
    fn usage_remote_claude_quota_live_smoke_redacted() {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("live smoke runtime should build");
        let snapshot = runtime
            .block_on(read_claude_quota())
            .unwrap_or_else(|error| panic!("live quota failed: {}", error.reason()));
        assert!(
            !snapshot.windows.is_empty(),
            "live quota response contained no quota windows"
        );
        crate::debug_log::stderr_log!("live usage request status=200");
    }
}
