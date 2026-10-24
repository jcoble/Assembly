use reqwest::header::{ACCEPT, AUTHORIZATION, CONTENT_TYPE, RETRY_AFTER, USER_AGENT};
#[cfg(target_os = "macos")]
use security_framework::item::{ItemClass, ItemSearchOptions, Limit};
use serde::Deserialize;
use serde_json::Value;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
use std::path::{Path, PathBuf};
#[cfg(target_os = "macos")]
use std::process::{Command, Stdio};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

pub const USAGE_SOURCE: &str = "anthropic-oauth-usage";
pub const USAGE_SOURCE_VERSION: &str = "oauth-2025-04-20";
const USAGE_URL: &str = "https://api.anthropic.com/api/oauth/usage";
const TOKEN_URL: &str = "https://platform.claude.com/v1/oauth/token";
const OAUTH_CLIENT_ID: &str = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
const USER_PROFILE_SCOPE: &str = "user:profile";
#[cfg(target_os = "macos")]
const KEYCHAIN_SERVICE: &str = "Claude Code-credentials";
#[cfg(target_os = "macos")]
const KEYCHAIN_ITEM_NOT_FOUND: i32 = -25_300;
const CLAUDE_USER_AGENT: &str = "claude-code";

pub struct ClaudeCredentials {
    owner: CredentialOwner,
    document: Value,
    access_token: String,
    refresh_token: Option<String>,
    expires_at: u64,
    subscription_type: Option<String>,
    rate_limit_tier: Option<String>,
}

struct CredentialOwner {
    store: Arc<dyn CredentialStore>,
    record_id: String,
}

#[derive(Clone)]
struct StoredCredential {
    record_id: String,
    document: Vec<u8>,
}

trait CredentialStore: Send + Sync {
    fn label(&self) -> &'static str;
    fn read(&self) -> Result<Vec<StoredCredential>, CredentialStoreError>;
    fn write(&self, record_id: &str, document: &[u8]) -> Result<(), ClaudeRequestError>;
    /// May this app renew a sign-in it read here? Renewing rotates the
    /// refresh token, and a token another program is also holding must not be
    /// rotated out from under it.
    fn renews_here(&self) -> bool {
        true
    }
}

#[derive(Debug, Clone, Copy)]
struct CredentialStoreError {
    reason: &'static str,
    fatal: bool,
}

impl CredentialStoreError {
    const fn missing(reason: &'static str) -> Self {
        Self {
            reason,
            fatal: false,
        }
    }

    const fn access(reason: &'static str) -> Self {
        Self {
            reason,
            fatal: true,
        }
    }
}

struct FileStore {
    path: Option<PathBuf>,
}

impl FileStore {
    fn new(path: PathBuf) -> Self {
        Self { path: Some(path) }
    }

    fn unavailable() -> Self {
        Self { path: None }
    }
}

impl CredentialStore for FileStore {
    fn label(&self) -> &'static str {
        "Claude credential file"
    }

    fn read(&self) -> Result<Vec<StoredCredential>, CredentialStoreError> {
        let path = self.path.as_deref().ok_or_else(|| {
            CredentialStoreError::missing("no Claude configuration directory could be resolved")
        })?;
        let document = fs::read(path).map_err(|error| {
            if error.kind() == std::io::ErrorKind::NotFound {
                CredentialStoreError::missing("credential file is missing")
            } else {
                CredentialStoreError::access("credential file could not be read")
            }
        })?;
        Ok(vec![StoredCredential {
            record_id: "file".to_string(),
            document,
        }])
    }

    fn write(&self, record_id: &str, document: &[u8]) -> Result<(), ClaudeRequestError> {
        if record_id != "file" {
            return Err(ClaudeRequestError::CredentialWrite);
        }
        let path = self
            .path
            .as_deref()
            .ok_or(ClaudeRequestError::CredentialWrite)?;
        atomic_write_private_json(path, document)
    }
}

#[cfg(target_os = "macos")]
#[derive(Default)]
struct KeychainStore;

/// `security find-generic-password` when the item is not there.
#[cfg(target_os = "macos")]
const SECURITY_ITEM_NOT_FOUND: i32 = 44;

/// One Keychain item's data, read through Apple's own `security` tool.
///
/// The item is Claude Code's, written through that same tool, so its access
/// list already names `security` and the read is silent. Read from this
/// process directly, every rebuild of the unsigned development binary was a
/// new program as far as the Keychain was concerned, and it asked again each
/// time — "Always Allow" never stuck. `None` means the item is not there.
#[cfg(target_os = "macos")]
fn keychain_password(account: &str) -> Result<Option<Vec<u8>>, CredentialStoreError> {
    let output = Command::new("/usr/bin/security")
        .args([
            "find-generic-password",
            "-s",
            KEYCHAIN_SERVICE,
            "-a",
            account,
            "-w",
        ])
        .stdin(Stdio::null())
        .output()
        .map_err(|_| {
            CredentialStoreError::missing("generic-password item data could not be accessed")
        })?;
    if output.status.code() == Some(SECURITY_ITEM_NOT_FOUND) {
        return Ok(None);
    }
    if !output.status.success() {
        return Err(CredentialStoreError::missing(
            "generic-password item data could not be accessed",
        ));
    }
    let mut password = output.stdout;
    if password.last() == Some(&b'\n') {
        password.pop();
    }
    Ok(Some(password))
}

#[cfg(target_os = "macos")]
impl CredentialStore for KeychainStore {
    fn label(&self) -> &'static str {
        "Claude Keychain"
    }

    /// The Keychain item is Claude Code's. It renews the token itself as it
    /// works, and holds the refresh token in memory while it runs — so a
    /// renewal from here would rotate that token away underneath it.
    fn renews_here(&self) -> bool {
        false
    }

    fn read(&self) -> Result<Vec<StoredCredential>, CredentialStoreError> {
        let search = ItemSearchOptions::new()
            .class(ItemClass::generic_password())
            .service(KEYCHAIN_SERVICE)
            .load_attributes(true)
            .limit(Limit::All)
            .search();
        let results = match search {
            Ok(results) => results,
            Err(error) if error.code() == KEYCHAIN_ITEM_NOT_FOUND => {
                return Err(CredentialStoreError::missing(
                    "generic-password item is missing",
                ));
            }
            Err(_) => {
                return Err(CredentialStoreError::missing(
                    "generic-password item could not be accessed",
                ));
            }
        };

        let accounts = results
            .into_iter()
            .filter_map(|result| result.simplify_dict())
            .filter_map(|mut attributes| attributes.remove("acct"))
            .filter(|account| !account.is_empty())
            .collect::<Vec<_>>();
        if accounts.is_empty() {
            return Err(CredentialStoreError::missing(
                "generic-password item has no usable account attribute",
            ));
        }

        let mut records = Vec::new();
        for account in accounts {
            let Some(password) = keychain_password(&account)? else {
                continue;
            };
            records.push(StoredCredential {
                record_id: format!("keychain-item-{}", records.len()),
                document: password,
            });
        }
        if records.is_empty() {
            return Err(CredentialStoreError::missing(
                "generic-password item is unavailable",
            ));
        }
        Ok(records)
    }

    /// Never reached: `renews_here` is false, so nothing read from here is
    /// renewed, and renewing is the only thing that writes.
    fn write(&self, _record_id: &str, _document: &[u8]) -> Result<(), ClaudeRequestError> {
        Err(ClaudeRequestError::CredentialWrite)
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ClaudeCredentialFields {
    access_token: String,
    refresh_token: Option<String>,
    #[serde(deserialize_with = "deserialize_optional_timestamp")]
    expires_at: Option<u64>,
    #[serde(default)]
    scopes: Vec<String>,
    subscription_type: Option<String>,
    rate_limit_tier: Option<String>,
}

fn deserialize_optional_timestamp<'de, D>(deserializer: D) -> Result<Option<u64>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    match value {
        None => Ok(None),
        Some(Value::Number(number)) => timestamp_number(number.as_f64()),
        Some(Value::String(text)) => timestamp_number(text.trim().parse::<f64>().ok()),
        Some(_) => Err(serde::de::Error::custom(
            "timestamp must be a number or string",
        )),
    }
}

fn timestamp_number<E>(value: Option<f64>) -> Result<Option<u64>, E>
where
    E: serde::de::Error,
{
    let Some(value) = value else {
        return Err(E::custom("timestamp must be finite"));
    };
    if !value.is_finite() || value < 0.0 {
        return Err(E::custom("timestamp must be finite and non-negative"));
    }
    Ok(Some(value.floor().min(u64::MAX as f64) as u64))
}

#[derive(Debug, Clone, PartialEq)]
pub struct ClaudeQuotaWindow {
    pub label: String,
    pub used_percent: f64,
    pub resets_at: Option<String>,
    pub window_minutes: Option<u64>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ClaudeQuotaSnapshot {
    pub account: Option<String>,
    pub windows: Vec<ClaudeQuotaWindow>,
    pub captured_at: u128,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ClaudeCredentialError {
    Unavailable(String),
    Invalid(String),
}

impl ClaudeCredentialError {
    pub fn reason(&self) -> &str {
        match self {
            Self::Unavailable(reason) | Self::Invalid(reason) => reason.as_str(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ClaudeRequestError {
    Http {
        status: u16,
        retry_after: Option<String>,
    },
    Transport,
    Decode,
    CredentialWrite,
}

impl ClaudeRequestError {
    pub fn status(&self) -> Option<u16> {
        match self {
            Self::Http { status, .. } => Some(*status),
            Self::Transport | Self::Decode | Self::CredentialWrite => None,
        }
    }

    pub fn retry_after(&self) -> Option<&str> {
        match self {
            Self::Http { retry_after, .. } => retry_after.as_deref(),
            Self::Transport | Self::Decode | Self::CredentialWrite => None,
        }
    }
}

pub struct ClaudeQuotaClient {
    client: reqwest::Client,
    usage_url: String,
    token_url: String,
}

impl ClaudeQuotaClient {
    pub fn new() -> Result<Self, ClaudeRequestError> {
        let client = reqwest::Client::builder()
            .timeout(std::time::Duration::from_secs(15))
            .build()
            .map_err(|_| ClaudeRequestError::Transport)?;
        Ok(Self {
            client,
            usage_url: USAGE_URL.to_string(),
            token_url: TOKEN_URL.to_string(),
        })
    }

    #[cfg(test)]
    pub(crate) fn with_endpoints(
        usage_url: String,
        token_url: String,
    ) -> Result<Self, ClaudeRequestError> {
        let mut client = Self::new()?;
        client.usage_url = usage_url;
        client.token_url = token_url;
        Ok(client)
    }

    pub async fn fetch_usage(
        &self,
        credentials: &ClaudeCredentials,
    ) -> Result<ClaudeQuotaSnapshot, ClaudeRequestError> {
        let user_agent = claude_code_user_agent();
        let response = self
            .client
            .get(&self.usage_url)
            .header(
                AUTHORIZATION,
                format!("Bearer {}", credentials.access_token),
            )
            .header(ACCEPT, "application/json")
            .header(CONTENT_TYPE, "application/json")
            .header("anthropic-beta", USAGE_SOURCE_VERSION)
            .header(USER_AGENT, user_agent)
            .send()
            .await
            .map_err(|_| ClaudeRequestError::Transport)?;
        let status = response.status();
        if !status.is_success() {
            return Err(ClaudeRequestError::Http {
                status: status.as_u16(),
                retry_after: response
                    .headers()
                    .get(RETRY_AFTER)
                    .and_then(|value| value.to_str().ok())
                    .map(ToString::to_string),
            });
        }
        let usage = response
            .json::<ClaudeUsageResponse>()
            .await
            .map_err(|_| ClaudeRequestError::Decode)?;
        Ok(usage.into_snapshot(credentials.plan_label(), now_ms()))
    }

    pub async fn refresh_credentials(
        &self,
        credentials: &mut ClaudeCredentials,
    ) -> Result<(), ClaudeRequestError> {
        let refresh_token = credentials
            .refresh_token
            .as_deref()
            .filter(|token| !token.trim().is_empty())
            .ok_or(ClaudeRequestError::Http {
                status: 401,
                retry_after: None,
            })?;
        let response = self
            .client
            .post(&self.token_url)
            .form(&[
                ("grant_type", "refresh_token"),
                ("refresh_token", refresh_token),
                ("client_id", OAUTH_CLIENT_ID),
            ])
            .send()
            .await
            .map_err(|_| ClaudeRequestError::Transport)?;
        let status = response.status();
        if !status.is_success() {
            return Err(ClaudeRequestError::Http {
                status: status.as_u16(),
                retry_after: None,
            });
        }
        let refreshed = response
            .json::<ClaudeTokenResponse>()
            .await
            .map_err(|_| ClaudeRequestError::Decode)?;
        credentials.persist_refresh(refreshed)
    }
}

fn claude_code_user_agent() -> String {
    let version = std::env::var_os("HOME")
        .map(PathBuf::from)
        .and_then(|home| {
            latest_installed_claude_version(&home.join(".local/share/claude/versions"))
        });
    version
        .map(|version| format!("{CLAUDE_USER_AGENT}/{version}"))
        .unwrap_or_else(|| CLAUDE_USER_AGENT.to_string())
}

fn latest_installed_claude_version(versions_dir: &Path) -> Option<String> {
    fs::read_dir(versions_dir)
        .ok()?
        .flatten()
        .filter_map(|entry| {
            let version = entry.file_name().into_string().ok()?;
            let mut parts = version.split('.');
            let parsed = (
                parts.next()?.parse::<u64>().ok()?,
                parts.next()?.parse::<u64>().ok()?,
                parts.next()?.parse::<u64>().ok()?,
            );
            parts.next().is_none().then_some((parsed, version))
        })
        .max_by_key(|(parsed, _)| *parsed)
        .map(|(_, version)| version)
}

/// The sign-in Claude Code holds, from wherever it is freshest.
///
/// Claude Code keeps its token in the macOS Keychain and renews it there as it
/// works; the credentials file beside it is written less and less. Reading the
/// file first, and only the file, is what left this app renewing with a token
/// Claude Code had already rotated away — every open of the usage popup was a
/// 400 from the token endpoint. Both are read now and the one that expires
/// last wins. The first read of the Keychain item asks the reader once.
pub fn read_credentials() -> Result<ClaudeCredentials, ClaudeCredentialError> {
    let file_store: Arc<dyn CredentialStore> = match credential_path().ok() {
        Some(path) => Arc::new(FileStore::new(path)),
        None => Arc::new(FileStore::unavailable()),
    };
    #[cfg(target_os = "macos")]
    let stores = vec![
        file_store,
        Arc::new(KeychainStore) as Arc<dyn CredentialStore>,
    ];
    #[cfg(not(target_os = "macos"))]
    let stores = vec![file_store];
    select_credentials(stores)
}

fn credential_path() -> Result<PathBuf, ClaudeCredentialError> {
    credential_path_from_roots(
        std::env::var_os("CLAUDE_SECURESTORAGE_CONFIG_DIR").map(PathBuf::from),
        std::env::var_os("CLAUDE_CONFIG_DIR").map(PathBuf::from),
        std::env::var_os("HOME").map(PathBuf::from),
    )
}

fn credential_path_from_roots(
    secure_storage_root: Option<PathBuf>,
    config_root: Option<PathBuf>,
    home: Option<PathBuf>,
) -> Result<PathBuf, ClaudeCredentialError> {
    secure_storage_root
        .filter(|path| !path.as_os_str().is_empty())
        .or_else(|| config_root.filter(|path| !path.as_os_str().is_empty()))
        .map(|root| root.join(".credentials.json"))
        .or_else(|| home.map(|root| root.join(".claude/.credentials.json")))
        .ok_or_else(|| {
            ClaudeCredentialError::Unavailable(
                "Claude credentials are unavailable because no configuration directory could be resolved"
                    .to_string(),
            )
        })
}

struct ParsedCredential {
    document: Value,
    access_token: String,
    refresh_token: Option<String>,
    expires_at: u64,
    subscription_type: Option<String>,
    rate_limit_tier: Option<String>,
}

fn parse_credential(document: &[u8]) -> Result<ParsedCredential, &'static str> {
    let document =
        serde_json::from_slice::<Value>(document).map_err(|_| "credential is not valid JSON")?;
    let oauth = document
        .get("claudeAiOauth")
        .cloned()
        .ok_or("credential does not contain a Claude subscription credential")?;
    let fields = serde_json::from_value::<ClaudeCredentialFields>(oauth)
        .map_err(|_| "Claude subscription credential is incomplete")?;
    if fields.access_token.trim().is_empty() {
        return Err("Claude subscription credential has no access token");
    }
    if !fields
        .scopes
        .iter()
        .any(|scope| scope == USER_PROFILE_SCOPE)
    {
        return Err("Claude subscription credential is missing the user:profile scope");
    }
    let expires_at = fields
        .expires_at
        .ok_or("Claude subscription credential has no expiresAt value")?;
    Ok(ParsedCredential {
        document,
        access_token: fields.access_token,
        refresh_token: fields.refresh_token,
        expires_at,
        subscription_type: fields.subscription_type,
        rate_limit_tier: fields.rate_limit_tier,
    })
}

fn select_credentials(
    stores: Vec<Arc<dyn CredentialStore>>,
) -> Result<ClaudeCredentials, ClaudeCredentialError> {
    let mut skipped = Vec::new();
    let mut freshest: Option<ClaudeCredentials> = None;
    for store in stores {
        let records = match store.read() {
            Ok(records) => records,
            Err(error) if error.fatal => {
                return Err(ClaudeCredentialError::Unavailable(format!(
                    "{} {}",
                    store.label(),
                    error.reason
                )));
            }
            Err(error) => {
                skipped.push(format!("{}: {}", store.label(), error.reason));
                continue;
            }
        };
        for record in records {
            let parsed = match parse_credential(&record.document) {
                Ok(parsed) => parsed,
                Err(reason) => {
                    skipped.push(format!("{}: {reason}", store.label()));
                    continue;
                }
            };
            let candidate = ClaudeCredentials {
                owner: CredentialOwner {
                    store: Arc::clone(&store),
                    record_id: record.record_id,
                },
                document: parsed.document,
                access_token: parsed.access_token,
                refresh_token: parsed.refresh_token,
                expires_at: parsed.expires_at,
                subscription_type: parsed.subscription_type,
                rate_limit_tier: parsed.rate_limit_tier,
            };
            // The one that expires last is the one renewed most recently.
            if freshest
                .as_ref()
                .is_none_or(|current| candidate.expires_at > current.expires_at)
            {
                freshest = Some(candidate);
            }
        }
    }
    if let Some(credentials) = freshest {
        return Ok(credentials);
    }
    Err({
        let reason = if skipped.is_empty() {
            "no credential sources returned a candidate".to_string()
        } else {
            skipped.join("; ")
        };
        ClaudeCredentialError::Invalid(format!(
            "No usable Claude subscription credential was found ({reason})"
        ))
    })
}

#[cfg(test)]
pub(crate) fn read_credentials_at(
    path: PathBuf,
) -> Result<ClaudeCredentials, ClaudeCredentialError> {
    select_credentials(vec![Arc::new(FileStore::new(path))])
}

impl ClaudeCredentials {
    pub fn is_expired(&self, current_time_ms: u64) -> bool {
        self.expires_at <= current_time_ms.saturating_add(30_000)
    }

    /// Whether this app may renew the sign-in itself. A sign-in read from the
    /// Keychain is Claude Code's to renew.
    pub fn can_renew(&self) -> bool {
        self.refresh_token.is_some() && self.owner.store.renews_here()
    }

    pub fn plan_label(&self) -> Option<String> {
        self.subscription_type
            .as_deref()
            .filter(|value| !value.trim().is_empty())
            .or_else(|| {
                self.rate_limit_tier
                    .as_deref()
                    .filter(|value| !value.trim().is_empty())
            })
            .map(derive_plan_label)
    }

    fn persist_refresh(
        &mut self,
        refreshed: ClaudeTokenResponse,
    ) -> Result<(), ClaudeRequestError> {
        let expires_at = now_ms_u64().saturating_add(refreshed.expires_in.saturating_mul(1_000));
        let mut document = self.document.clone();
        let oauth = document
            .get_mut("claudeAiOauth")
            .and_then(Value::as_object_mut)
            .ok_or(ClaudeRequestError::CredentialWrite)?;
        oauth.insert(
            "accessToken".to_string(),
            Value::String(refreshed.access_token.clone()),
        );
        oauth.insert(
            "refreshToken".to_string(),
            Value::String(refreshed.refresh_token.clone()),
        );
        oauth.insert("expiresAt".to_string(), Value::from(expires_at));
        let bytes = serde_json::to_vec_pretty(&document)
            .map_err(|_| ClaudeRequestError::CredentialWrite)?;
        self.owner.store.write(&self.owner.record_id, &bytes)?;
        self.document = document;
        self.access_token = refreshed.access_token;
        self.refresh_token = Some(refreshed.refresh_token);
        self.expires_at = expires_at;
        Ok(())
    }
}

fn atomic_write_private_json(path: &Path, bytes: &[u8]) -> Result<(), ClaudeRequestError> {
    let parent = path.parent().ok_or(ClaudeRequestError::CredentialWrite)?;
    let original_mode = match fs::metadata(path) {
        Ok(metadata) => metadata.permissions().mode() & 0o777,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => 0o600,
        Err(_) => return Err(ClaudeRequestError::CredentialWrite),
    };
    let temp_name = format!(
        ".credentials.json.tmp-{}-{}",
        std::process::id(),
        now_ms_u64()
    );
    let temp_path = parent.join(temp_name);
    let result = (|| {
        let mut temp = OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(0o600)
            .open(&temp_path)
            .map_err(|_| ClaudeRequestError::CredentialWrite)?;
        temp.write_all(bytes)
            .map_err(|_| ClaudeRequestError::CredentialWrite)?;
        temp.sync_all()
            .map_err(|_| ClaudeRequestError::CredentialWrite)?;
        fs::set_permissions(&temp_path, fs::Permissions::from_mode(original_mode))
            .map_err(|_| ClaudeRequestError::CredentialWrite)?;
        fs::rename(&temp_path, path).map_err(|_| ClaudeRequestError::CredentialWrite)?;
        if let Ok(directory) = fs::File::open(parent) {
            let _ = directory.sync_all();
        }
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temp_path);
    }
    result
}

#[derive(Deserialize)]
struct ClaudeTokenResponse {
    access_token: String,
    refresh_token: String,
    expires_in: u64,
    #[serde(rename = "token_type")]
    _token_type: String,
}

#[derive(Debug, Default, Deserialize)]
struct ClaudeUsageResponse {
    five_hour: Option<ClaudeUsageWireWindow>,
    seven_day: Option<ClaudeUsageWireWindow>,
    seven_day_opus: Option<ClaudeUsageWireWindow>,
    seven_day_sonnet: Option<ClaudeUsageWireWindow>,
    #[serde(default)]
    limits: Vec<ClaudeScopedLimit>,
    #[serde(default, rename = "seven_day_oauth_apps")]
    _seven_day_oauth_apps: Option<Value>,
    #[serde(default, rename = "extra_usage")]
    _extra_usage: Option<Value>,
}

#[derive(Debug, Clone, Deserialize)]
struct ClaudeUsageWireWindow {
    utilization: Option<f64>,
    resets_at: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ClaudeScopedLimit {
    kind: Option<String>,
    percent: Option<f64>,
    resets_at: Option<String>,
    scope: Option<ClaudeLimitScope>,
    #[serde(default, rename = "is_active")]
    _is_active: Option<bool>,
}

#[derive(Debug, Deserialize)]
struct ClaudeLimitScope {
    model: Option<ClaudeLimitModel>,
}

#[derive(Debug, Deserialize)]
struct ClaudeLimitModel {
    #[serde(default, rename = "id")]
    _id: Option<String>,
    display_name: Option<String>,
}

impl ClaudeUsageResponse {
    fn into_snapshot(self, account: Option<String>, captured_at: u128) -> ClaudeQuotaSnapshot {
        let mut windows = Vec::new();
        push_window(&mut windows, "Session", 300, self.five_hour);
        push_window(&mut windows, "Weekly", 10_080, self.seven_day);
        push_window(&mut windows, "Opus weekly", 10_080, self.seven_day_opus);
        push_window(&mut windows, "Sonnet weekly", 10_080, self.seven_day_sonnet);
        for limit in self.limits {
            let Some(percent) = limit.percent else {
                continue;
            };
            let Some(display_name) = limit
                .scope
                .and_then(|scope| scope.model)
                .and_then(|model| model.display_name)
                .filter(|name| !name.trim().is_empty())
            else {
                continue;
            };
            if duplicates_legacy_model_window(&windows, &display_name, limit.resets_at.as_deref()) {
                continue;
            }
            windows.push(ClaudeQuotaWindow {
                label: format!("Weekly ({})", display_name.trim()),
                used_percent: percent.clamp(0.0, 100.0),
                resets_at: limit.resets_at,
                window_minutes: (limit.kind.as_deref() == Some("weekly_scoped")).then_some(10_080),
            });
        }
        ClaudeQuotaSnapshot {
            account,
            windows,
            captured_at,
        }
    }
}

fn push_window(
    windows: &mut Vec<ClaudeQuotaWindow>,
    label: &str,
    window_minutes: u64,
    window: Option<ClaudeUsageWireWindow>,
) {
    let Some(window) = window else {
        return;
    };
    let Some(utilization) = window.utilization else {
        return;
    };
    windows.push(ClaudeQuotaWindow {
        label: label.to_string(),
        used_percent: utilization.clamp(0.0, 100.0),
        resets_at: window.resets_at,
        window_minutes: Some(window_minutes),
    });
}

fn duplicates_legacy_model_window(
    windows: &[ClaudeQuotaWindow],
    display_name: &str,
    resets_at: Option<&str>,
) -> bool {
    let normalized = display_name.to_ascii_lowercase();
    let legacy_label = if normalized.contains("opus") {
        Some("Opus weekly")
    } else if normalized.contains("sonnet") {
        Some("Sonnet weekly")
    } else {
        None
    };
    legacy_label.is_some_and(|label| {
        windows
            .iter()
            .any(|window| window.label == label && window.resets_at.as_deref() == resets_at)
    })
}

pub fn derive_plan_label(raw: &str) -> String {
    let normalized = raw.trim().to_ascii_lowercase();
    let tokens = normalized
        .split(|character: char| !character.is_ascii_alphanumeric())
        .filter(|token| !token.is_empty())
        .collect::<Vec<_>>();
    if let Some(index) = tokens.iter().position(|token| *token == "max") {
        if let Some(multiplier) = tokens.get(index + 1).filter(|token| is_multiplier(token)) {
            return format!("Max {multiplier}");
        }
        return "Max".to_string();
    }
    if let Some(multiplier) = normalized
        .strip_prefix("max")
        .filter(|token| is_multiplier(token))
    {
        return format!("Max {multiplier}");
    }
    for (token, label) in [
        ("pro", "Pro"),
        ("team", "Team"),
        ("enterprise", "Enterprise"),
        ("ultra", "Ultra"),
    ] {
        if tokens.contains(&token) {
            return label.to_string();
        }
    }
    raw.trim().to_string()
}

fn is_multiplier(value: &&str) -> bool {
    value.strip_suffix('x').is_some_and(|number| {
        !number.is_empty() && number.chars().all(|character| character.is_ascii_digit())
    })
}

pub fn now_ms_u64() -> u64 {
    now_ms().min(u128::from(u64::MAX)) as u64
}

fn now_ms() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::sync::Mutex;

    #[cfg(target_os = "macos")]
    #[test]
    #[ignore = "requires the logged-in Claude Keychain credential"]
    fn live_keychain_item_reads_without_asking() {
        let records = KeychainStore
            .read()
            .expect("Claude Keychain credential should be readable through `security`");
        assert!(!records.is_empty());
        assert!(records
            .iter()
            .all(|record| parse_credential(&record.document).is_ok()));
    }

    #[cfg(target_os = "macos")]
    #[test]
    #[ignore = "requires the logged-in Claude Keychain credential and network access"]
    fn live_corrected_version_header_returns_status() {
        let credentials = select_credentials(vec![Arc::new(KeychainStore::default())])
            .expect("Claude Keychain credential should be readable");
        let client = ClaudeQuotaClient::new().expect("Claude usage client should build");
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("test runtime should build");
        match runtime.block_on(client.fetch_usage(&credentials)) {
            Ok(_) => println!("Observed Claude usage HTTP status: 200"),
            Err(ClaudeRequestError::Http { status, .. }) => {
                panic!("Observed Claude usage HTTP status: {status}")
            }
            Err(error) => panic!("Claude usage request failed before an HTTP status: {error:?}"),
        }
    }

    struct FakeStore {
        label: &'static str,
        records: Vec<StoredCredential>,
        writes: Arc<Mutex<Vec<String>>>,
        failure: Option<CredentialStoreError>,
    }

    impl FakeStore {
        fn new(label: &'static str, record_id: &str, document: Value) -> Self {
            Self {
                label,
                records: vec![StoredCredential {
                    record_id: record_id.to_string(),
                    document: serde_json::to_vec(&document)
                        .expect("credential fixture should encode"),
                }],
                writes: Arc::new(Mutex::new(Vec::new())),
                failure: None,
            }
        }

        /// A store that cannot be read — a Keychain the reader said no to.
        fn failing(label: &'static str, failure: CredentialStoreError) -> Self {
            Self {
                label,
                records: Vec::new(),
                writes: Arc::new(Mutex::new(Vec::new())),
                failure: Some(failure),
            }
        }
    }

    impl CredentialStore for FakeStore {
        fn label(&self) -> &'static str {
            self.label
        }

        fn read(&self) -> Result<Vec<StoredCredential>, CredentialStoreError> {
            match self.failure {
                Some(failure) => Err(failure),
                None => Ok(self.records.clone()),
            }
        }

        fn write(&self, record_id: &str, _document: &[u8]) -> Result<(), ClaudeRequestError> {
            self.writes
                .lock()
                .map_err(|_| ClaudeRequestError::CredentialWrite)?
                .push(record_id.to_string());
            Ok(())
        }
    }

    fn credential_fixture(access_token: &str, expires_at: u64, scopes: &[&str]) -> Value {
        json!({
            "claudeAiOauth": {
                "accessToken": access_token,
                "refreshToken": "fixture-refresh",
                "expiresAt": expires_at,
                "scopes": scopes,
                "subscriptionType": "max"
            }
        })
    }

    fn fixture_credentials(document: Value) -> ClaudeCredentials {
        select_credentials(vec![Arc::new(FakeStore::new(
            "Fixture store",
            "fixture",
            document,
        ))])
        .expect("fixture credential should be selected")
    }

    fn nullable_window_fixture(mask: u8) -> Value {
        let window = |percent: f64| {
            json!({
                "utilization": percent,
                "resets_at": "2026-08-14T16:00:00Z"
            })
        };
        json!({
            "five_hour": (mask & 1 != 0).then(|| window(10.0)),
            "seven_day": (mask & 2 != 0).then(|| window(20.0)),
            "seven_day_opus": (mask & 4 != 0).then(|| window(30.0)),
            "seven_day_sonnet": (mask & 8 != 0).then(|| window(40.0)),
            "extra_usage": {
                "is_enabled": true,
                "utilization": 75.0,
                "unknown_nested_field": "ignored"
            },
            "unknown_top_level_field": { "ignored": true }
        })
    }

    #[test]
    fn claude_quota_decodes_every_nullable_window_combination() {
        for mask in 0_u8..16 {
            let decoded =
                serde_json::from_value::<ClaudeUsageResponse>(nullable_window_fixture(mask))
                    .expect("fixture should decode");
            let snapshot = decoded.into_snapshot(Some("Max".to_string()), 1);
            assert_eq!(snapshot.windows.len(), mask.count_ones() as usize);
            assert!(snapshot
                .windows
                .iter()
                .all(|window| window.label != "Extra usage"));
        }
    }

    #[test]
    fn claude_quota_decodes_scoped_limits_and_keeps_inactive_entries() {
        let decoded = serde_json::from_value::<ClaudeUsageResponse>(json!({
            "five_hour": null,
            "seven_day": null,
            "limits": [
                {
                    "kind": "weekly_scoped",
                    "percent": 9.0,
                    "resets_at": "2026-08-14T16:00:00Z",
                    "scope": { "model": { "id": "model-redacted", "display_name": "Research" } },
                    "is_active": false
                },
                {
                    "kind": "future_scope",
                    "percent": 11.0,
                    "resets_at": null,
                    "scope": { "model": { "display_name": "Preview" } }
                }
            ]
        }))
        .expect("fixture should decode");
        let snapshot = decoded.into_snapshot(None, 1);
        assert_eq!(snapshot.windows[0].label, "Weekly (Research)");
        assert_eq!(snapshot.windows[0].window_minutes, Some(10_080));
        assert_eq!(snapshot.windows[1].label, "Weekly (Preview)");
        assert_eq!(snapshot.windows[1].window_minutes, None);
    }

    #[test]
    fn claude_quota_tolerates_unknown_fields_and_nullable_window_members() {
        let decoded = serde_json::from_value::<ClaudeUsageResponse>(json!({
            "five_hour": { "utilization": null, "resets_at": null, "future": true },
            "seven_day": { "utilization": 42.0, "resets_at": null },
            "future_window": { "utilization": 99.0 }
        }))
        .expect("unknown fields should be ignored");
        let snapshot = decoded.into_snapshot(None, 1);
        assert_eq!(snapshot.windows.len(), 1);
        assert_eq!(snapshot.windows[0].label, "Weekly");
        assert_eq!(snapshot.windows[0].resets_at, None);
    }

    #[test]
    fn claude_quota_derives_plan_labels_from_subscription_or_tier_values() {
        assert_eq!(derive_plan_label("max"), "Max");
        assert_eq!(derive_plan_label("max_20x"), "Max 20x");
        assert_eq!(derive_plan_label("default_claude_max_20x"), "Max 20x");
        assert_eq!(derive_plan_label("pro"), "Pro");
        let mut credentials = fixture_credentials(credential_fixture(
            "fixture-access",
            now_ms_u64() + 3_600_000,
            &["user:profile"],
        ));
        credentials.rate_limit_tier = Some("default_claude_max_20x".to_string());
        assert_eq!(credentials.plan_label().as_deref(), Some("Max"));
        credentials.subscription_type = None;
        assert_eq!(credentials.plan_label().as_deref(), Some("Max 20x"));
    }

    #[test]
    fn claude_quota_credential_resolution_honors_overrides() {
        let secure = credential_path_from_roots(
            Some(PathBuf::from("/secure")),
            Some(PathBuf::from("/config")),
            Some(PathBuf::from("/home")),
        )
        .expect("secure override should resolve");
        assert_eq!(secure, PathBuf::from("/secure/.credentials.json"));
        let config = credential_path_from_roots(
            None,
            Some(PathBuf::from("/config")),
            Some(PathBuf::from("/home")),
        )
        .expect("config override should resolve");
        assert_eq!(config, PathBuf::from("/config/.credentials.json"));
        let fallback = credential_path_from_roots(None, None, Some(PathBuf::from("/home")))
            .expect("home fallback should resolve");
        assert_eq!(fallback, PathBuf::from("/home/.claude/.credentials.json"));
    }

    /// The file used to win by being first. That is how a five-day-old file
    /// beat a Keychain item renewed this morning, and every usage read became
    /// a renewal with a token Claude Code had already rotated away.
    #[test]
    fn quota_prefers_the_freshest_credential_whichever_store_holds_it() {
        let keychain: Arc<dyn CredentialStore> = Arc::new(FakeStore::new(
            "Keychain fake",
            "keychain-record",
            credential_fixture("fixture-keychain-access", 300, &["user:profile"]),
        ));
        let file: Arc<dyn CredentialStore> = Arc::new(FakeStore::new(
            "File fake",
            "file-record",
            credential_fixture("fixture-file-access", 100, &["user:profile"]),
        ));
        let selected = select_credentials(vec![file, keychain])
            .expect("one valid credential should be selected");
        assert_eq!(selected.owner.store.label(), "Keychain fake");
        assert_eq!(selected.access_token, "fixture-keychain-access");
    }

    #[test]
    fn quota_falls_back_to_the_file_when_the_keychain_cannot_be_read() {
        let file: Arc<dyn CredentialStore> = Arc::new(FakeStore::new(
            "File fake",
            "file-record",
            credential_fixture("fixture-file-access", 100, &["user:profile"]),
        ));
        let keychain: Arc<dyn CredentialStore> = Arc::new(FakeStore::failing(
            "Keychain fake",
            CredentialStoreError::missing("generic-password item could not be accessed"),
        ));
        let selected =
            select_credentials(vec![file, keychain]).expect("the file should still answer");
        assert_eq!(selected.owner.store.label(), "File fake");
    }

    #[test]
    fn quota_accepts_fractional_expiry_values() {
        let expires_at = now_ms_u64() + 3_600_000;
        for raw_expiry in [
            serde_json::json!(expires_at as f64 + 0.441),
            serde_json::json!(format!("{expires_at}.441")),
        ] {
            let document = serde_json::json!({
                "claudeAiOauth": {
                    "accessToken": "fixture-access",
                    "refreshToken": "fixture-refresh",
                    "expiresAt": raw_expiry,
                    "scopes": ["user:profile"]
                }
            });
            let credentials = fixture_credentials(document);
            assert_eq!(credentials.expires_at, expires_at);
        }
    }

    #[test]
    fn claude_quota_skips_bad_shape_without_exposing_credential_content() {
        let bad_shape: Arc<dyn CredentialStore> = Arc::new(FakeStore::new(
            "Keychain fake",
            "keychain-record",
            credential_fixture("fixture-keychain-access", 300, &["user:inference"]),
        ));
        let valid: Arc<dyn CredentialStore> = Arc::new(FakeStore::new(
            "File fake",
            "file-record",
            credential_fixture("fixture-file-access", 200, &["user:profile"]),
        ));
        let selected = select_credentials(vec![bad_shape, valid])
            .expect("valid fallback should survive a bad-shape source");
        assert_eq!(selected.owner.store.label(), "File fake");

        let only_bad: Arc<dyn CredentialStore> = Arc::new(FakeStore::new(
            "Keychain fake",
            "keychain-record",
            credential_fixture("fixture-keychain-access", 300, &["user:inference"]),
        ));
        let error = select_credentials(vec![only_bad])
            .err()
            .expect("bad-shape source should be rejected");
        assert!(error.reason().contains("missing the user:profile scope"));
        assert!(!error.reason().contains("fixture-keychain-access"));
    }

    /// A renewed token goes back to the store the sign-in was read from — never
    /// to a store that did not hold it, which would leave two copies of a
    /// rotating token and each renewal invalidating the other's.
    #[test]
    fn claude_quota_writes_rotation_back_to_the_store_it_came_from() {
        for (keychain_expiry, file_expiry) in [(200, 100), (100, 200)] {
            let keychain_holds_it = keychain_expiry > file_expiry;
            let keychain = Arc::new(FakeStore::new(
                "Keychain fake",
                "keychain-record",
                credential_fixture(
                    "fixture-keychain-access",
                    keychain_expiry,
                    &["user:profile"],
                ),
            ));
            let file = Arc::new(FakeStore::new(
                "File fake",
                "file-record",
                credential_fixture("fixture-file-access", file_expiry, &["user:profile"]),
            ));
            let keychain_writes = Arc::clone(&keychain.writes);
            let file_writes = Arc::clone(&file.writes);
            let stores: Vec<Arc<dyn CredentialStore>> = vec![file, keychain];
            let mut selected =
                select_credentials(stores).expect("one valid credential should be selected");
            selected
                .persist_refresh(ClaudeTokenResponse {
                    access_token: "fixture-rotated-access".to_string(),
                    refresh_token: "fixture-rotated-refresh".to_string(),
                    expires_in: 3_600,
                    _token_type: "bearer".to_string(),
                })
                .expect("rotation should persist to its owner");
            assert_eq!(
                keychain_writes
                    .lock()
                    .expect("keychain writes should lock")
                    .len(),
                usize::from(keychain_holds_it)
            );
            assert_eq!(
                file_writes.lock().expect("file writes should lock").len(),
                usize::from(!keychain_holds_it)
            );
        }
    }

    /// The real Keychain item is Claude Code's, and this app does not renew
    /// what it did not sign in to.
    #[cfg(target_os = "macos")]
    #[test]
    fn claude_quota_does_not_renew_a_keychain_sign_in() {
        let store: Arc<dyn CredentialStore> = Arc::new(KeychainStore::default());
        assert!(!store.renews_here());
        let file: Arc<dyn CredentialStore> = Arc::new(FakeStore::new(
            "File fake",
            "file-record",
            credential_fixture("fixture-file-access", 100, &["user:profile"]),
        ));
        let selected = select_credentials(vec![file]).expect("the file should answer");
        assert!(selected.can_renew());
    }

    #[test]
    fn claude_quota_persists_rotated_refresh_token_atomically_with_mode() {
        let directory = std::env::temp_dir().join(format!(
            "mcb-claude-quota-{}-{}",
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
                "subscriptionType": "max"
            },
            "preserved": true
        });
        fs::write(
            &path,
            serde_json::to_vec_pretty(&document).expect("fixture should encode"),
        )
        .expect("fixture should be written");
        fs::set_permissions(&path, fs::Permissions::from_mode(0o600))
            .expect("fixture mode should be set");
        let mut credentials =
            read_credentials_at(path.clone()).expect("file credential fixture should be selected");
        credentials
            .persist_refresh(ClaudeTokenResponse {
                access_token: "redacted-new-access".to_string(),
                refresh_token: "redacted-new-refresh".to_string(),
                expires_in: 3600,
                _token_type: "bearer".to_string(),
            })
            .expect("refresh should persist");
        let persisted = serde_json::from_slice::<Value>(
            &fs::read(&path).expect("persisted fixture should be readable"),
        )
        .expect("persisted fixture should decode");
        assert_eq!(
            persisted.pointer("/claudeAiOauth/refreshToken"),
            Some(&Value::String("redacted-new-refresh".to_string()))
        );
        assert_eq!(persisted.get("preserved"), Some(&Value::Bool(true)));
        assert_eq!(
            fs::metadata(&path)
                .expect("persisted fixture should have metadata")
                .permissions()
                .mode()
                & 0o777,
            0o600
        );
        fs::remove_file(&path).expect("temporary credential should be removed");
        fs::remove_dir(&directory).expect("temporary directory should be removed");
    }
    #[test]
    fn claude_quota_test_client_accepts_local_endpoints() {
        let client = ClaudeQuotaClient::with_endpoints(
            "http://127.0.0.1:1/usage".to_string(),
            "http://127.0.0.1:1/token".to_string(),
        )
        .expect("test client should build");
        assert_eq!(client.usage_url, "http://127.0.0.1:1/usage");
    }
}
