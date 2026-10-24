use std::fs;
use std::io::Cursor;
use std::path::{Component, Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use image::{ImageFormat, ImageReader};
use mcb_core::session_store::{EvidenceArtifact, EvidenceArtifactQuery, EvidenceDiskUsage};
use serde::{Deserialize, Serialize};
use tauri::Manager;

use crate::agent_conversation::manager::AgentRuntimeManager;

const MAX_IMAGE_BYTES: u64 = 20 * 1024 * 1024;
const MAX_IMAGE_EDGE: u32 = 12_000;
const MAX_IMAGE_PIXELS: u64 = 50_000_000;
const THUMBNAIL_MAX_EDGE: u32 = 360;
const MAX_GALLERY_PAGE_SIZE: u32 = 200;
const MAX_DELETE_SELECTION_SIZE: usize = 200;
const MAX_RETENTION_BATCH_SIZE: u32 = 200;
const DEFAULT_RETENTION_MS: i64 = 30 * 24 * 60 * 60 * 1_000;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct CopyEvidenceOriginalRequest {
    pub id: String,
    pub source_path: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ManagedEvidenceOriginal {
    pub original_ref: String,
    pub byte_size: u64,
    pub thumbnail_ref: Option<String>,
    pub thumbnail_byte_size: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ListEvidenceGalleryRequest {
    pub before_captured_at_ms: Option<i64>,
    pub before_id: Option<String>,
    pub limit: u32,
    pub task_id: Option<String>,
    pub commit_hash: Option<String>,
    pub orchestration_run_id: Option<String>,
    pub agent: Option<String>,
    pub scenario: Option<String>,
    pub captured_from_ms: Option<i64>,
    pub captured_to_ms: Option<i64>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct EvidenceGalleryItem {
    pub id: String,
    pub orchestration_run_id: String,
    pub task_id: Option<String>,
    pub agent: String,
    pub provider: String,
    pub scenario: String,
    pub commit_hash: String,
    pub branch: String,
    pub worktree: String,
    pub captured_at_ms: i64,
    pub kind: String,
    pub status: String,
    pub byte_size: i64,
    pub thumbnail_ref: Option<String>,
    pub pinned: bool,
    pub expires_at_ms: Option<i64>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct EvidenceGalleryPage {
    pub items: Vec<EvidenceGalleryItem>,
    pub has_more: bool,
    pub next_cursor: Option<EvidenceGalleryCursor>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct EvidenceGalleryCursor {
    pub captured_at_ms: i64,
    pub id: String,
}

#[derive(Debug, Deserialize)]
#[serde(tag = "scope", rename_all = "camelCase")]
pub(crate) enum SetEvidencePinnedRequest {
    Artifact {
        artifact_id: String,
        pinned: bool,
    },
    Run {
        orchestration_run_id: String,
        pinned: bool,
    },
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct EvidencePinReceipt {
    pub updated: usize,
}

#[derive(Debug, Deserialize)]
#[serde(tag = "scope", rename_all = "camelCase")]
pub(crate) enum DeleteEvidenceRequest {
    Selected { artifact_ids: Vec<String> },
    Run { orchestration_run_id: String },
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct EvidenceDeleteReceipt {
    pub deleted: usize,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct EvidenceOriginalFile {
    pub path: String,
    pub byte_size: i64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct EvidenceDiskUsageResponse {
    pub total_bytes: i64,
    pub runs: Vec<EvidenceRunDiskUsageResponse>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct EvidenceRunDiskUsageResponse {
    pub orchestration_run_id: String,
    pub byte_size: i64,
}

#[tauri::command]
pub(crate) async fn copy_evidence_original(
    app: tauri::AppHandle,
    request: CopyEvidenceOriginalRequest,
) -> Result<ManagedEvidenceOriginal, String> {
    let evidence_root = app
        .path()
        .app_data_dir()
        .map_err(|error| format!("Application data directory is unavailable: {error}"))?
        .join("evidence");
    tauri::async_runtime::spawn_blocking(move || {
        copy_original_into(Path::new(&request.source_path), &evidence_root, &request.id)
    })
    .await
    .map_err(|error| format!("Evidence copy task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn list_evidence_gallery(
    app: tauri::AppHandle,
    manager: tauri::State<'_, AgentRuntimeManager>,
    request: ListEvidenceGalleryRequest,
) -> Result<EvidenceGalleryPage, String> {
    let store = manager.store_handle();
    let limit = request.limit.clamp(1, MAX_GALLERY_PAGE_SIZE);
    if request.before_captured_at_ms.is_some() != request.before_id.is_some() {
        return Err("Evidence gallery cursor requires both capturedAtMs and id".to_string());
    }
    let evidence_root = app
        .path()
        .app_data_dir()
        .map_err(|error| format!("Application data directory is unavailable: {error}"))?
        .join("evidence");
    tauri::async_runtime::spawn_blocking(move || {
        prune_expired_evidence(&store, &evidence_root, current_time_ms()?)?;
        let mut artifacts = store
            .list_evidence_artifacts(&EvidenceArtifactQuery {
                before_captured_at_ms: request.before_captured_at_ms,
                before_id: request.before_id,
                limit: limit + 1,
                task_id: request.task_id,
                commit_hash: request.commit_hash,
                orchestration_run_id: request.orchestration_run_id,
                agent: request.agent,
                scenario: request.scenario,
                captured_from_ms: request.captured_from_ms,
                captured_to_ms: request.captured_to_ms,
            })
            .map_err(|error| error.to_string())?;
        let has_more = artifacts.len() > limit as usize;
        artifacts.truncate(limit as usize);
        let next_cursor = has_more.then(|| {
            let last = artifacts
                .last()
                .expect("a full evidence page has a last item");
            EvidenceGalleryCursor {
                captured_at_ms: last.captured_at_ms,
                id: last.id.clone(),
            }
        });
        Ok(EvidenceGalleryPage {
            items: artifacts
                .into_iter()
                .map(EvidenceGalleryItem::from)
                .collect(),
            has_more,
            next_cursor,
        })
    })
    .await
    .map_err(|error| format!("Evidence gallery task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn set_evidence_pinned(
    manager: tauri::State<'_, AgentRuntimeManager>,
    request: SetEvidencePinnedRequest,
) -> Result<EvidencePinReceipt, String> {
    let store = manager.store_handle();
    tauri::async_runtime::spawn_blocking(move || {
        let updated = match request {
            SetEvidencePinnedRequest::Artifact {
                artifact_id,
                pinned,
            } => usize::from(
                store
                    .set_evidence_artifact_pinned(&artifact_id, pinned)
                    .map_err(|error| error.to_string())?,
            ),
            SetEvidencePinnedRequest::Run {
                orchestration_run_id,
                pinned,
            } => store
                .set_evidence_run_pinned(&orchestration_run_id, pinned)
                .map_err(|error| error.to_string())?,
        };
        Ok(EvidencePinReceipt { updated })
    })
    .await
    .map_err(|error| format!("Evidence pin task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn resolve_evidence_original(
    app: tauri::AppHandle,
    manager: tauri::State<'_, AgentRuntimeManager>,
    artifact_id: String,
) -> Result<EvidenceOriginalFile, String> {
    let store = manager.store_handle();
    let evidence_root = app
        .path()
        .app_data_dir()
        .map_err(|error| format!("Application data directory is unavailable: {error}"))?
        .join("evidence");
    tauri::async_runtime::spawn_blocking(move || {
        let artifact = store
            .evidence_artifact(&artifact_id)
            .map_err(|error| error.to_string())?
            .ok_or_else(|| format!("Evidence artifact {artifact_id} was not found"))?;
        let path = resolve_managed_original(&evidence_root, &artifact.original_ref)?;
        Ok(EvidenceOriginalFile {
            path: path
                .into_os_string()
                .into_string()
                .map_err(|_| "Managed evidence path is not valid UTF-8".to_string())?,
            byte_size: artifact.byte_size,
        })
    })
    .await
    .map_err(|error| format!("Evidence original task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn delete_evidence(
    app: tauri::AppHandle,
    manager: tauri::State<'_, AgentRuntimeManager>,
    request: DeleteEvidenceRequest,
) -> Result<EvidenceDeleteReceipt, String> {
    let store = manager.store_handle();
    let evidence_root = app
        .path()
        .app_data_dir()
        .map_err(|error| format!("Application data directory is unavailable: {error}"))?
        .join("evidence");
    tauri::async_runtime::spawn_blocking(move || {
        delete_managed_evidence(&store, &evidence_root, request)
    })
    .await
    .map_err(|error| format!("Evidence delete task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_evidence_disk_usage(
    manager: tauri::State<'_, AgentRuntimeManager>,
) -> Result<EvidenceDiskUsageResponse, String> {
    let store = manager.store_handle();
    tauri::async_runtime::spawn_blocking(move || {
        store
            .evidence_disk_usage()
            .map(EvidenceDiskUsageResponse::from)
            .map_err(|error| error.to_string())
    })
    .await
    .map_err(|error| format!("Evidence disk usage task failed: {error}"))?
}

fn current_time_ms() -> Result<i64, String> {
    let milliseconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| format!("System clock is before the Unix epoch: {error}"))?
        .as_millis();
    i64::try_from(milliseconds).map_err(|_| "System time is too large to represent".to_string())
}

fn prune_expired_evidence(
    store: &mcb_core::session_store::SessionStore,
    evidence_root: &Path,
    now_ms: i64,
) -> Result<usize, String> {
    let artifacts = store
        .delete_expired_unpinned_evidence_artifacts(
            now_ms,
            DEFAULT_RETENTION_MS,
            MAX_RETENTION_BATCH_SIZE,
        )
        .map_err(|error| error.to_string())?;
    remove_managed_evidence_files(evidence_root, &artifacts, "Expired evidence")?;
    Ok(artifacts.len())
}

fn delete_managed_evidence(
    store: &mcb_core::session_store::SessionStore,
    evidence_root: &Path,
    request: DeleteEvidenceRequest,
) -> Result<EvidenceDeleteReceipt, String> {
    let artifacts = match request {
        DeleteEvidenceRequest::Selected { artifact_ids } => {
            if artifact_ids.len() > MAX_DELETE_SELECTION_SIZE {
                return Err(format!(
                    "At most {MAX_DELETE_SELECTION_SIZE} evidence artifacts can be deleted at once"
                ));
            }
            store
                .delete_evidence_artifacts(&artifact_ids)
                .map_err(|error| error.to_string())?
        }
        DeleteEvidenceRequest::Run {
            orchestration_run_id,
        } => store
            .delete_evidence_run(&orchestration_run_id)
            .map_err(|error| error.to_string())?,
    };
    remove_managed_evidence_files(evidence_root, &artifacts, "Evidence")?;
    Ok(EvidenceDeleteReceipt {
        deleted: artifacts.len(),
    })
}

fn remove_managed_evidence_files(
    evidence_root: &Path,
    artifacts: &[EvidenceArtifact],
    operation: &str,
) -> Result<(), String> {
    let mut first_error = None;
    for artifact in artifacts {
        for reference in [
            Some(artifact.original_ref.as_str()),
            artifact.thumbnail_ref.as_deref(),
        ]
        .into_iter()
        .flatten()
        {
            if let Err(error) = remove_managed_evidence_file(evidence_root, reference) {
                first_error.get_or_insert(error);
            }
        }
    }
    if let Some(error) = first_error {
        return Err(format!(
            "{operation} rows were removed, but managed file cleanup failed: {error}"
        ));
    }
    Ok(())
}

fn remove_managed_evidence_file(evidence_root: &Path, reference: &str) -> Result<(), String> {
    let path = managed_evidence_path(evidence_root, reference, None)?;
    match fs::remove_file(&path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(format!("Could not remove {}: {error}", path.display())),
    }
}

fn resolve_managed_original(evidence_root: &Path, reference: &str) -> Result<PathBuf, String> {
    let path = managed_evidence_path(evidence_root, reference, Some("originals"))?;
    let originals_root = evidence_root
        .join("originals")
        .canonicalize()
        .map_err(|error| format!("Managed evidence originals are unavailable: {error}"))?;
    let resolved = path
        .canonicalize()
        .map_err(|error| format!("Managed evidence original is unavailable: {error}"))?;
    if !resolved.starts_with(&originals_root) || !resolved.is_file() {
        return Err("Managed evidence original is outside the evidence vault".to_string());
    }
    Ok(resolved)
}

fn managed_evidence_path(
    evidence_root: &Path,
    reference: &str,
    expected_folder: Option<&str>,
) -> Result<PathBuf, String> {
    let relative = Path::new(reference)
        .strip_prefix("evidence")
        .map_err(|_| "Managed evidence reference is outside the evidence vault".to_string())?;
    let mut components = relative.components();
    let folder = match components.next() {
        Some(Component::Normal(folder)) => folder,
        _ => return Err("Managed evidence reference is invalid".to_string()),
    };
    if (folder != "originals" && folder != "thumbnails")
        || expected_folder.is_some_and(|expected| folder != expected)
        || !matches!(components.next(), Some(Component::Normal(_)))
        || components.next().is_some()
    {
        return Err("Managed evidence reference is invalid".to_string());
    }
    Ok(evidence_root.join(relative))
}

fn copy_original_into(
    source: &Path,
    evidence_root: &Path,
    artifact_id: &str,
) -> Result<ManagedEvidenceOriginal, String> {
    let artifact_id = safe_segment(artifact_id)?;
    let source = source.canonicalize().map_err(|error| {
        format!(
            "Could not find evidence source {}: {error}",
            source.display()
        )
    })?;
    if !source.is_file() {
        return Err("Evidence source must be a regular file".to_string());
    }
    let extension = source
        .extension()
        .and_then(|value| value.to_str())
        .map(safe_extension)
        .transpose()?;
    let file_name = extension
        .map(|extension| format!("{artifact_id}.{extension}"))
        .unwrap_or_else(|| artifact_id.to_string());
    let originals_root = evidence_root.join("originals");
    fs::create_dir_all(&originals_root).map_err(|error| {
        format!(
            "Could not create managed evidence folder {}: {error}",
            originals_root.display()
        )
    })?;
    let destination = originals_root.join(&file_name);
    let temporary =
        originals_root.join(format!(".{artifact_id}-{}.importing", uuid::Uuid::new_v4()));
    let byte_size = match fs::copy(&source, &temporary) {
        Ok(byte_size) => byte_size,
        Err(error) => {
            let _ = fs::remove_file(&temporary);
            return Err(format!(
                "Could not copy evidence source {} into managed storage: {error}",
                source.display()
            ));
        }
    };
    if let Err(error) = fs::rename(&temporary, &destination) {
        let _ = fs::remove_file(&temporary);
        return Err(format!(
            "Could not finish managed evidence copy {}: {error}",
            destination.display()
        ));
    }
    let thumbnail = match write_thumbnail(&destination, evidence_root, artifact_id) {
        Ok(thumbnail) => thumbnail,
        Err(error) => {
            let _ = fs::remove_file(&destination);
            return Err(error);
        }
    };
    Ok(ManagedEvidenceOriginal {
        original_ref: format!("evidence/originals/{file_name}"),
        byte_size,
        thumbnail_ref: thumbnail.as_ref().map(|(reference, _)| reference.clone()),
        thumbnail_byte_size: thumbnail.map(|(_, byte_size)| byte_size),
    })
}

fn write_thumbnail(
    source: &Path,
    evidence_root: &Path,
    artifact_id: &str,
) -> Result<Option<(String, u64)>, String> {
    let reader = ImageReader::open(source)
        .map_err(|error| format!("Could not inspect evidence image: {error}"))?
        .with_guessed_format()
        .map_err(|error| format!("Could not inspect evidence image format: {error}"))?;
    let Some(format) = reader.format().filter(|format| {
        matches!(
            format,
            ImageFormat::Png | ImageFormat::Jpeg | ImageFormat::Gif | ImageFormat::WebP
        )
    }) else {
        return Ok(None);
    };
    let byte_size = fs::metadata(source)
        .map_err(|error| format!("Could not inspect evidence image size: {error}"))?
        .len();
    if byte_size > MAX_IMAGE_BYTES {
        return Err("Evidence image is too large to thumbnail safely".to_string());
    }
    let (width, height) = reader
        .into_dimensions()
        .map_err(|error| format!("Could not read evidence image dimensions: {error}"))?;
    if width > MAX_IMAGE_EDGE
        || height > MAX_IMAGE_EDGE
        || u64::from(width) * u64::from(height) > MAX_IMAGE_PIXELS
    {
        return Err("Evidence image is too large to thumbnail safely".to_string());
    }
    let image = image::load_from_memory_with_format(
        &fs::read(source)
            .map_err(|error| format!("Could not read evidence image for thumbnail: {error}"))?,
        format,
    )
    .map_err(|error| format!("Could not decode evidence image for thumbnail: {error}"))?;
    let thumbnail = image.thumbnail(
        width.min(THUMBNAIL_MAX_EDGE),
        height.min(THUMBNAIL_MAX_EDGE),
    );
    let mut bytes = Vec::new();
    thumbnail
        .write_to(&mut Cursor::new(&mut bytes), ImageFormat::WebP)
        .map_err(|error| format!("Could not encode evidence thumbnail: {error}"))?;

    let thumbnails_root = evidence_root.join("thumbnails");
    fs::create_dir_all(&thumbnails_root).map_err(|error| {
        format!(
            "Could not create managed evidence thumbnail folder {}: {error}",
            thumbnails_root.display()
        )
    })?;
    let file_name = format!("{artifact_id}.webp");
    let destination = thumbnails_root.join(&file_name);
    let temporary =
        thumbnails_root.join(format!(".{artifact_id}-{}.importing", uuid::Uuid::new_v4()));
    if let Err(error) = fs::write(&temporary, &bytes) {
        let _ = fs::remove_file(&temporary);
        return Err(format!("Could not write evidence thumbnail: {error}"));
    }
    if let Err(error) = fs::rename(&temporary, &destination) {
        let _ = fs::remove_file(&temporary);
        return Err(format!("Could not finish evidence thumbnail: {error}"));
    }
    Ok(Some((
        format!("evidence/thumbnails/{file_name}"),
        bytes.len() as u64,
    )))
}

impl From<EvidenceArtifact> for EvidenceGalleryItem {
    fn from(artifact: EvidenceArtifact) -> Self {
        Self {
            id: artifact.id,
            orchestration_run_id: artifact.orchestration_run_id,
            task_id: artifact.task_id,
            agent: artifact.agent,
            provider: artifact.provider,
            scenario: artifact.scenario,
            commit_hash: artifact.commit_hash,
            branch: artifact.branch,
            worktree: artifact.worktree,
            captured_at_ms: artifact.captured_at_ms,
            kind: artifact.kind,
            status: artifact.status,
            byte_size: artifact.byte_size,
            thumbnail_ref: artifact.thumbnail_ref,
            pinned: artifact.pinned,
            expires_at_ms: artifact.expires_at_ms,
        }
    }
}

impl From<EvidenceDiskUsage> for EvidenceDiskUsageResponse {
    fn from(usage: EvidenceDiskUsage) -> Self {
        Self {
            total_bytes: usage.total_bytes,
            runs: usage
                .runs
                .into_iter()
                .map(|run| EvidenceRunDiskUsageResponse {
                    orchestration_run_id: run.orchestration_run_id,
                    byte_size: run.byte_size,
                })
                .collect(),
        }
    }
}

fn safe_segment(value: &str) -> Result<&str, String> {
    if !value.is_empty()
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        Ok(value)
    } else {
        Err("Evidence artifact id must contain only letters, numbers, '-' or '_'".to_string())
    }
}

fn safe_extension(value: &str) -> Result<String, String> {
    if !value.is_empty() && value.bytes().all(|byte| byte.is_ascii_alphanumeric()) {
        Ok(value.to_ascii_lowercase())
    } else {
        Err("Evidence source extension must contain only letters or numbers".to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::GenericImageView;
    use mcb_core::session_store::SessionStore;
    use std::path::PathBuf;

    struct TestDirectory(PathBuf);

    impl TestDirectory {
        fn new(label: &str) -> Self {
            let path =
                std::env::temp_dir().join(format!("mcb-evidence-{label}-{}", uuid::Uuid::new_v4()));
            fs::create_dir(&path).unwrap();
            Self(path)
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn copied_original_survives_source_removal() {
        let source_directory = TestDirectory::new("source");
        let managed_directory = TestDirectory::new("managed");
        let source = source_directory.0.join("proof.txt");
        fs::write(&source, b"proof bytes").unwrap();

        let copied = copy_original_into(&source, &managed_directory.0, "artifact-1").unwrap();
        drop(source_directory);

        assert_eq!(copied.original_ref, "evidence/originals/artifact-1.txt");
        assert_eq!(copied.byte_size, 11);
        assert_eq!(copied.thumbnail_ref, None);
        assert_eq!(copied.thumbnail_byte_size, None);
        assert_eq!(
            fs::read(managed_directory.0.join("originals/artifact-1.txt")).unwrap(),
            b"proof bytes"
        );
    }

    #[test]
    fn image_original_creates_a_bounded_webp_thumbnail() {
        let source_directory = TestDirectory::new("image-source");
        let managed_directory = TestDirectory::new("image-managed");
        let source = source_directory.0.join("proof.png");
        image::RgbaImage::new(800, 400)
            .save_with_format(&source, ImageFormat::Png)
            .unwrap();

        let copied = copy_original_into(&source, &managed_directory.0, "artifact-1").unwrap();
        let thumbnail_path = managed_directory.0.join("thumbnails/artifact-1.webp");
        let thumbnail = image::open(&thumbnail_path).unwrap();

        assert_eq!(
            copied.thumbnail_ref.as_deref(),
            Some("evidence/thumbnails/artifact-1.webp")
        );
        assert_eq!(
            copied.thumbnail_byte_size,
            Some(fs::metadata(thumbnail_path).unwrap().len())
        );
        assert_eq!(thumbnail.dimensions(), (360, 180));
    }

    #[test]
    fn gallery_item_serialization_omits_the_original_reference() {
        let item = EvidenceGalleryItem::from(EvidenceArtifact {
            id: "artifact-1".to_string(),
            orchestration_run_id: "run-1".to_string(),
            task_id: Some("TSK-1".to_string()),
            agent: "tester".to_string(),
            provider: "codex".to_string(),
            scenario: "checkout".to_string(),
            commit_hash: "abc123".to_string(),
            branch: "main".to_string(),
            worktree: "/repo".to_string(),
            captured_at_ms: 1,
            kind: "screenshot".to_string(),
            status: "passed".to_string(),
            byte_size: 42,
            original_ref: "evidence/originals/artifact-1.png".to_string(),
            thumbnail_ref: Some("evidence/thumbnails/artifact-1.webp".to_string()),
            thumbnail_byte_size: 12,
            pinned: false,
            expires_at_ms: None,
        });

        let value = serde_json::to_value(item).unwrap();
        assert_eq!(
            value.get("thumbnailRef").and_then(|value| value.as_str()),
            Some("evidence/thumbnails/artifact-1.webp")
        );
        assert!(value.get("originalRef").is_none());
    }

    #[test]
    fn thumbnail_and_gallery_contract_survive_store_reopen() {
        let source_directory = TestDirectory::new("restart-source");
        let managed_directory = TestDirectory::new("restart-managed");
        let source = source_directory.0.join("proof.png");
        image::RgbaImage::new(40, 20)
            .save_with_format(&source, ImageFormat::Png)
            .unwrap();
        let copied = copy_original_into(&source, &managed_directory.0, "artifact-1").unwrap();
        let database = managed_directory.0.join("sessions.db");
        let store = SessionStore::open(&database).unwrap();
        store
            .upsert_evidence_artifact(&EvidenceArtifact {
                id: "artifact-1".to_string(),
                orchestration_run_id: "run-1".to_string(),
                task_id: None,
                agent: "tester".to_string(),
                provider: "codex".to_string(),
                scenario: "restart".to_string(),
                commit_hash: "abc123".to_string(),
                branch: "main".to_string(),
                worktree: "/deleted-worktree".to_string(),
                captured_at_ms: 1,
                kind: "screenshot".to_string(),
                status: "passed".to_string(),
                byte_size: copied.byte_size as i64,
                original_ref: copied.original_ref,
                thumbnail_ref: copied.thumbnail_ref,
                thumbnail_byte_size: copied.thumbnail_byte_size.unwrap_or(0) as i64,
                pinned: false,
                expires_at_ms: None,
            })
            .unwrap();
        drop(store);
        drop(source_directory);

        let reopened = SessionStore::open(&database).unwrap();
        let artifact = reopened
            .list_evidence_artifacts(&EvidenceArtifactQuery {
                limit: 1,
                ..Default::default()
            })
            .unwrap()
            .pop()
            .unwrap();
        let gallery_value = serde_json::to_value(EvidenceGalleryItem::from(artifact)).unwrap();
        let thumbnail_ref = gallery_value
            .get("thumbnailRef")
            .and_then(|value| value.as_str())
            .unwrap();

        assert_eq!(thumbnail_ref, "evidence/thumbnails/artifact-1.webp");
        assert_eq!(
            image::open(managed_directory.0.join("thumbnails/artifact-1.webp"))
                .unwrap()
                .dimensions(),
            (40, 20)
        );
        assert!(gallery_value.get("originalRef").is_none());
    }

    #[test]
    fn retention_removes_expired_files_and_rows_but_keeps_pinned_proof() {
        let managed_directory = TestDirectory::new("retention");
        fs::create_dir_all(managed_directory.0.join("originals")).unwrap();
        fs::create_dir_all(managed_directory.0.join("thumbnails")).unwrap();
        let store = SessionStore::open(&managed_directory.0.join("sessions.db")).unwrap();
        for (id, pinned) in [("expired", false), ("pinned", true)] {
            fs::write(
                managed_directory.0.join(format!("originals/{id}.png")),
                b"original",
            )
            .unwrap();
            fs::write(
                managed_directory.0.join(format!("thumbnails/{id}.webp")),
                b"thumbnail",
            )
            .unwrap();
            store
                .upsert_evidence_artifact(&EvidenceArtifact {
                    id: id.to_string(),
                    orchestration_run_id: "run-1".to_string(),
                    task_id: None,
                    agent: "tester".to_string(),
                    provider: "codex".to_string(),
                    scenario: "retention".to_string(),
                    commit_hash: "abc123".to_string(),
                    branch: "main".to_string(),
                    worktree: "/deleted-worktree".to_string(),
                    captured_at_ms: 1,
                    kind: "screenshot".to_string(),
                    status: "passed".to_string(),
                    byte_size: 8,
                    original_ref: format!("evidence/originals/{id}.png"),
                    thumbnail_ref: Some(format!("evidence/thumbnails/{id}.webp")),
                    thumbnail_byte_size: 9,
                    pinned,
                    expires_at_ms: Some(2),
                })
                .unwrap();
        }

        assert_eq!(
            prune_expired_evidence(&store, &managed_directory.0, 3).unwrap(),
            1
        );
        assert!(!managed_directory.0.join("originals/expired.png").exists());
        assert!(!managed_directory.0.join("thumbnails/expired.webp").exists());
        assert!(managed_directory.0.join("originals/pinned.png").exists());
        assert!(managed_directory.0.join("thumbnails/pinned.webp").exists());
        assert_eq!(
            store
                .list_evidence_artifacts(&EvidenceArtifactQuery {
                    limit: 10,
                    ..Default::default()
                })
                .unwrap()
                .iter()
                .map(|artifact| artifact.id.as_str())
                .collect::<Vec<_>>(),
            ["pinned"]
        );
    }

    #[test]
    fn original_resolution_is_explicit_and_stays_inside_the_vault() {
        let managed_directory = TestDirectory::new("resolve-original");
        fs::create_dir_all(managed_directory.0.join("originals")).unwrap();
        fs::create_dir_all(managed_directory.0.join("thumbnails")).unwrap();
        let original = managed_directory.0.join("originals/artifact-1.png");
        fs::write(&original, b"original").unwrap();
        fs::write(
            managed_directory.0.join("thumbnails/artifact-1.webp"),
            b"thumbnail",
        )
        .unwrap();

        assert_eq!(
            resolve_managed_original(&managed_directory.0, "evidence/originals/artifact-1.png")
                .unwrap(),
            original.canonicalize().unwrap()
        );
        assert!(resolve_managed_original(
            &managed_directory.0,
            "evidence/thumbnails/artifact-1.webp"
        )
        .is_err());
        assert!(resolve_managed_original(&managed_directory.0, "../outside.png").is_err());
    }

    #[test]
    fn selected_and_run_deletes_remove_rows_and_managed_files() {
        let managed_directory = TestDirectory::new("delete");
        fs::create_dir_all(managed_directory.0.join("originals")).unwrap();
        fs::create_dir_all(managed_directory.0.join("thumbnails")).unwrap();
        let store = SessionStore::open(&managed_directory.0.join("sessions.db")).unwrap();
        for (id, run_id) in [
            ("selected", "run-1"),
            ("same-run", "run-1"),
            ("other-run", "run-2"),
        ] {
            fs::write(
                managed_directory.0.join(format!("originals/{id}.png")),
                b"original",
            )
            .unwrap();
            fs::write(
                managed_directory.0.join(format!("thumbnails/{id}.webp")),
                b"thumbnail",
            )
            .unwrap();
            store
                .upsert_evidence_artifact(&EvidenceArtifact {
                    id: id.to_string(),
                    orchestration_run_id: run_id.to_string(),
                    task_id: None,
                    agent: "tester".to_string(),
                    provider: "codex".to_string(),
                    scenario: "delete".to_string(),
                    commit_hash: "abc123".to_string(),
                    branch: "main".to_string(),
                    worktree: "/deleted-worktree".to_string(),
                    captured_at_ms: 1,
                    kind: "screenshot".to_string(),
                    status: "passed".to_string(),
                    byte_size: 8,
                    original_ref: format!("evidence/originals/{id}.png"),
                    thumbnail_ref: Some(format!("evidence/thumbnails/{id}.webp")),
                    thumbnail_byte_size: 9,
                    pinned: false,
                    expires_at_ms: None,
                })
                .unwrap();
        }

        let selected = delete_managed_evidence(
            &store,
            &managed_directory.0,
            DeleteEvidenceRequest::Selected {
                artifact_ids: vec!["selected".to_string()],
            },
        )
        .unwrap();
        assert_eq!(selected.deleted, 1);
        assert!(!managed_directory.0.join("originals/selected.png").exists());
        assert!(!managed_directory
            .0
            .join("thumbnails/selected.webp")
            .exists());

        let run = delete_managed_evidence(
            &store,
            &managed_directory.0,
            DeleteEvidenceRequest::Run {
                orchestration_run_id: "run-1".to_string(),
            },
        )
        .unwrap();
        assert_eq!(run.deleted, 1);
        assert!(!managed_directory.0.join("originals/same-run.png").exists());
        assert!(managed_directory.0.join("originals/other-run.png").exists());
        assert_eq!(
            store
                .list_evidence_artifacts(&EvidenceArtifactQuery {
                    limit: 10,
                    ..Default::default()
                })
                .unwrap()
                .into_iter()
                .map(|artifact| artifact.id)
                .collect::<Vec<_>>(),
            ["other-run"]
        );
    }

    #[test]
    fn artifact_id_cannot_escape_managed_storage() {
        let source_directory = TestDirectory::new("source");
        let managed_directory = TestDirectory::new("managed");
        let source = source_directory.0.join("proof.png");
        fs::write(&source, b"proof").unwrap();

        assert!(copy_original_into(&source, &managed_directory.0, "../outside").is_err());
        assert!(!managed_directory.0.join("originals/outside.png").exists());
    }
}
