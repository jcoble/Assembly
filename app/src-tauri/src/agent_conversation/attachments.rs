use image::{ImageFormat, ImageReader};
use mcb_core::session_store::{AttachmentRow, SessionStore};
use serde::{Deserialize, Serialize};
use std::fs;
use std::io::{Cursor, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
#[cfg(not(test))]
use tauri::Manager;

pub const MAX_ATTACHMENT_BYTES: usize = 20 * 1024 * 1024;
const MAX_IMAGE_EDGE: u32 = 12_000;
const MAX_IMAGE_PIXELS: u64 = 50_000_000;
const THUMBNAIL_MAX_EDGE: u32 = 360;
const THUMBNAIL_MIME_TYPE: &str = "image/webp";

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SavedConversationAttachment {
    pub id: String,
    pub name: String,
    pub mime_type: String,
    pub path: String,
    pub byte_length: usize,
    pub thumbnail_mime_type: Option<String>,
    pub thumbnail_path: Option<String>,
    pub thumbnail_byte_length: Option<usize>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeleteConversationAttachmentRequest {
    pub owned_id: String,
    pub attachment_id: String,
    pub path: String,
}

pub fn save<R: tauri::Runtime>(
    app: &tauri::AppHandle<R>,
    store: &SessionStore,
    owned_id: &str,
    mime_type: &str,
    bytes: &[u8],
) -> Result<SavedConversationAttachment, String> {
    save_at(&vault_root(app)?, store, owned_id, mime_type, bytes)
}

pub fn save_at(
    vault: &Path,
    store: &SessionStore,
    owned_id: &str,
    mime_type: &str,
    bytes: &[u8],
) -> Result<SavedConversationAttachment, String> {
    let extension = validate_image(mime_type, bytes)?;
    let owned_id = safe_segment(owned_id, "Owned session id")?;
    let id = uuid::Uuid::new_v4().to_string();
    let name = format!("screenshot-{id}.{extension}");
    let thumbnail_name = format!("screenshot-{id}-thumb.webp");
    let thumbnail_bytes = thumbnail(bytes)?;
    let root = vault.join(owned_id);
    fs::create_dir_all(&root)
        .map_err(|error| format!("Could not create attachment folder: {error}"))?;
    let path = root.join(&name);
    let thumbnail_path = root.join(&thumbnail_name);
    fs::write(&path, bytes).map_err(|error| format!("Could not save screenshot: {error}"))?;
    if let Err(error) = fs::write(&thumbnail_path, &thumbnail_bytes) {
        let _ = fs::remove_file(&path);
        return Err(format!("Could not save screenshot thumbnail: {error}"));
    }
    // The canonical form only proves the file landed inside the managed folder.
    // The reported path is the one the row rebuilds, so a save and a later read
    // name the same file the same way.
    let canonical_root = canonical_root(&root)?;
    if !path
        .canonicalize()
        .map_err(|error| format!("Could not verify screenshot path: {error}"))?
        .starts_with(&canonical_root)
        || !thumbnail_path
            .canonicalize()
            .map_err(|error| format!("Could not verify screenshot thumbnail path: {error}"))?
            .starts_with(&canonical_root)
    {
        let _ = fs::remove_file(&path);
        let _ = fs::remove_file(&thumbnail_path);
        return Err("Saved screenshot escaped the managed attachment folder".to_string());
    }
    let byte_length =
        i64::try_from(bytes.len()).map_err(|_| "Attachment is too large to record".to_string())?;
    let thumbnail_byte_length = i64::try_from(thumbnail_bytes.len())
        .map_err(|_| "Attachment thumbnail is too large to record".to_string())?;
    if let Err(error) = store.add_attachment(&AttachmentRow {
        id: id.clone(),
        owned_id: owned_id.to_string(),
        file_name: name.clone(),
        mime_type: mime_type.to_string(),
        byte_length,
        relative_path: format!("{owned_id}/{name}"),
        thumbnail_mime_type: Some(THUMBNAIL_MIME_TYPE.to_string()),
        thumbnail_byte_length: Some(thumbnail_byte_length),
        thumbnail_relative_path: Some(format!("{owned_id}/{thumbnail_name}")),
        created_at_ms: now_ms(),
    }) {
        // A file with no row is invisible to every later read, so it leaves with the failure.
        let _ = fs::remove_file(&path);
        let _ = fs::remove_file(&thumbnail_path);
        return Err(error.to_string());
    }
    Ok(SavedConversationAttachment {
        id,
        name,
        mime_type: mime_type.to_string(),
        path: path.display().to_string(),
        byte_length: bytes.len(),
        thumbnail_mime_type: Some(THUMBNAIL_MIME_TYPE.to_string()),
        thumbnail_path: Some(thumbnail_path.display().to_string()),
        thumbnail_byte_length: Some(thumbnail_bytes.len()),
    })
}

pub fn read<R: tauri::Runtime>(
    app: &tauri::AppHandle<R>,
    store: &SessionStore,
    owned_id: &str,
) -> Result<Vec<SavedConversationAttachment>, String> {
    read_at(&vault_root(app)?, store, owned_id)
}

pub fn read_at(
    root: &Path,
    store: &SessionStore,
    owned_id: &str,
) -> Result<Vec<SavedConversationAttachment>, String> {
    let rows = store
        .list_attachments(owned_id)
        .map_err(|error| error.to_string())?;
    describe_rows(root, store, rows)
}

pub fn read_selected<R: tauri::Runtime>(
    app: &tauri::AppHandle<R>,
    store: &SessionStore,
    owned_id: &str,
    attachment_ids: &[String],
) -> Result<Vec<SavedConversationAttachment>, String> {
    read_selected_at(&vault_root(app)?, store, owned_id, attachment_ids)
}

pub fn read_selected_at(
    root: &Path,
    store: &SessionStore,
    owned_id: &str,
    attachment_ids: &[String],
) -> Result<Vec<SavedConversationAttachment>, String> {
    let rows = store
        .get_attachments(owned_id, attachment_ids)
        .map_err(|error| error.to_string())?;
    describe_rows(root, store, rows)
}

fn describe_rows(
    root: &Path,
    store: &SessionStore,
    rows: Vec<AttachmentRow>,
) -> Result<Vec<SavedConversationAttachment>, String> {
    rows
        .into_iter()
        .map(|row| {
            let fallback = row.clone();
            let row = ensure_thumbnail(root, store, row).unwrap_or_else(|_| AttachmentRow {
                thumbnail_mime_type: None,
                thumbnail_byte_length: None,
                thumbnail_relative_path: None,
                ..fallback
            });
            Ok(SavedConversationAttachment {
                path: root.join(&row.relative_path).display().to_string(),
                id: row.id,
                name: row.file_name,
                mime_type: row.mime_type,
                byte_length: usize::try_from(row.byte_length)
                    .map_err(|_| "Attachment is too large to describe".to_string())?,
                thumbnail_mime_type: row.thumbnail_mime_type,
                thumbnail_path: row
                    .thumbnail_relative_path
                    .map(|path| root.join(path).display().to_string()),
                thumbnail_byte_length: row
                    .thumbnail_byte_length
                    .map(usize::try_from)
                    .transpose()
                    .map_err(|_| {
                    "Attachment thumbnail is too large to describe".to_string()
                })?,
            })
        })
        .collect()
}

pub fn delete<R: tauri::Runtime>(
    app: &tauri::AppHandle<R>,
    store: &SessionStore,
    request: DeleteConversationAttachmentRequest,
) -> Result<(), String> {
    delete_at(&vault_root(app)?, store, request)
}

pub fn delete_at(
    vault: &Path,
    store: &SessionStore,
    request: DeleteConversationAttachmentRequest,
) -> Result<(), String> {
    let attachment_id = uuid::Uuid::parse_str(request.attachment_id.trim())
        .map_err(|_| "Attachment id is invalid".to_string())?;
    let owned_id = safe_segment(&request.owned_id, "Owned session id")?.to_string();
    let root = attachment_root(vault, &owned_id)?;
    let root = canonical_root(&root)?;
    let id = attachment_id.to_string();
    let row = store
        .get_attachments(&owned_id, &[id.clone()])
        .map_err(|error| error.to_string())?
        .into_iter()
        .next()
        .ok_or_else(|| "Attachment row was not found".to_string())?;
    let thumbnail = row
        .thumbnail_relative_path
        .as_ref()
        .map(|relative_path| vault.join(relative_path))
        .map(|path| validate_optional_managed_file(&root, &path))
        .transpose()?
        .flatten();
    let target = validate_delete_target(&root, attachment_id, Path::new(&request.path))?;
    validate_delete_row(&owned_id, &row, attachment_id, &target)?;
    store
        .delete_attachment(&id)
        .map_err(|error| error.to_string())?;
    if let Some(thumbnail) = thumbnail {
        if let Err(error) = fs::remove_file(thumbnail) {
            if error.kind() != std::io::ErrorKind::NotFound {
                crate::debug_log::stderr_log!(
                    "Deleted attachment row {id}, but could not remove its managed thumbnail: {error}"
                );
                return Err(format!(
                    "Attachment row was deleted, but the managed screenshot thumbnail could not be removed: {error}"
                ));
            }
        }
    }
    fs::remove_file(&target).map_err(|error| {
        crate::debug_log::stderr_log!(
            "Deleted attachment row {id}, but could not remove managed file {}: {error}",
            target.display()
        );
        format!("Attachment row was deleted, but the managed screenshot file could not be removed: {error}")
    })
}

/// Read only bytes belonging to an attachment row in this session.
pub fn read_chunk_at(
    vault: &Path,
    store: &SessionStore,
    owned_id: &str,
    attachment_id: &str,
    thumbnail: bool,
    offset: u64,
    max_bytes: usize,
) -> Result<Vec<u8>, String> {
    let owned_id = safe_segment(owned_id, "Owned session id")?;
    let id = uuid::Uuid::parse_str(attachment_id)
        .map_err(|_| "Attachment id is invalid".to_string())?
        .to_string();
    let row = store.get_attachments(owned_id, &[id]).map_err(|error| error.to_string())?
        .into_iter().next()
        .ok_or_else(|| "Attachment row was not found".to_string())?;
    let relative = if thumbnail {
        row.thumbnail_relative_path.as_deref().ok_or("Attachment thumbnail is unavailable")?
    } else {
        &row.relative_path
    };
    let root = canonical_root(&vault.join(owned_id))?;
    let path = validate_existing_managed_file(&root, &vault.join(relative), "attachment")?;
    let mut file = fs::File::open(path).map_err(|error| error.to_string())?;
    let length = file.metadata().map_err(|error| error.to_string())?.len();
    if length > MAX_ATTACHMENT_BYTES as u64 || offset > length {
        return Err("Attachment read is outside the 20 MB limit".to_string());
    }
    file.seek(SeekFrom::Start(offset)).map_err(|error| error.to_string())?;
    let mut bytes = vec![0; max_bytes.min((length - offset) as usize)];
    file.read_exact(&mut bytes).map_err(|error| error.to_string())?;
    Ok(bytes)
}

pub fn read_original_at(
    vault: &Path,
    row: &AttachmentRow,
) -> Result<Vec<u8>, String> {
    let owned_id = safe_segment(&row.owned_id, "Owned session id")?;
    let root = canonical_root(&vault.join(owned_id))?;
    let path = validate_existing_managed_file(&root, &vault.join(&row.relative_path), "attachment")?;
    let file = fs::File::open(path).map_err(|error| error.to_string())?;
    let mut bytes = Vec::new();
    file.take(MAX_ATTACHMENT_BYTES as u64 + 1).read_to_end(&mut bytes)
        .map_err(|error| error.to_string())?;
    if bytes.is_empty() || bytes.len() > MAX_ATTACHMENT_BYTES {
        return Err("Attachment exceeds 20 MB".into());
    }
    Ok(bytes)
}

/// The folder that holds every session's attachments, and the root that stored
/// relative paths are measured against.
pub(super) fn vault_root<R: tauri::Runtime>(app: &tauri::AppHandle<R>) -> Result<PathBuf, String> {
    #[cfg(test)]
    {
        let _ = app;
        Ok(std::env::temp_dir().join(format!(
            "mcb-conversation-attachments-test-{}",
            std::process::id()
        )))
    }
    #[cfg(not(test))]
    {
        Ok(app
            .path()
            .app_data_dir()
            .map_err(|error| format!("Application data directory is unavailable: {error}"))?
            .join("conversation-attachments"))
    }
}

fn attachment_root(vault: &Path, owned_id: &str) -> Result<PathBuf, String> {
    let owned_id = safe_segment(owned_id, "Owned session id")?;
    Ok(vault.join(owned_id))
}

fn validate_image<'a>(mime_type: &str, bytes: &'a [u8]) -> Result<&'static str, String> {
    let (extension, expected_magic): (&str, &[u8]) = match mime_type {
        "image/png" => ("png", b"\x89PNG\r\n\x1a\n"),
        "image/jpeg" => ("jpg", b"\xff\xd8\xff"),
        "image/gif" => ("gif", b"GIF8"),
        "image/webp" => ("webp", b"RIFF"),
        _ => return Err("Only PNG, JPEG, GIF, and WebP images are supported".to_string()),
    };
    if bytes.is_empty() || bytes.len() > MAX_ATTACHMENT_BYTES {
        return Err("Image must be between 1 byte and 20 MB".to_string());
    }
    if !bytes.starts_with(expected_magic)
        || (mime_type == "image/webp" && bytes.get(8..12) != Some(b"WEBP"))
    {
        return Err("Clipboard image data does not match its declared type".to_string());
    }
    Ok(extension)
}

fn validate_image_dimensions(bytes: &[u8]) -> Result<(), String> {
    let reader = ImageReader::new(Cursor::new(bytes))
        .with_guessed_format()
        .map_err(|error| format!("Could not read screenshot image format: {error}"))?;
    let format = reader
        .format()
        .ok_or_else(|| "Could not read screenshot image format".to_string())?;
    if !matches!(
        format,
        ImageFormat::Png | ImageFormat::Jpeg | ImageFormat::Gif | ImageFormat::WebP
    ) {
        return Err("Only PNG, JPEG, GIF, and WebP images are supported".to_string());
    }
    let (width, height) = reader
        .into_dimensions()
        .map_err(|error| format!("Could not read screenshot image dimensions: {error}"))?;
    if width == 0 || height == 0 || width > MAX_IMAGE_EDGE || height > MAX_IMAGE_EDGE {
        return Err(format!(
            "Image dimensions must be between 1x1 and {MAX_IMAGE_EDGE}x{MAX_IMAGE_EDGE}"
        ));
    }
    let pixels = u64::from(width) * u64::from(height);
    if pixels > MAX_IMAGE_PIXELS {
        return Err("Image is too large to thumbnail safely".to_string());
    }
    Ok(())
}

fn thumbnail(bytes: &[u8]) -> Result<Vec<u8>, String> {
    validate_image_dimensions(bytes)?;
    let image = image::load_from_memory(bytes)
        .map_err(|error| format!("Could not read screenshot image: {error}"))?;
    let thumbnail = image.thumbnail(THUMBNAIL_MAX_EDGE, THUMBNAIL_MAX_EDGE);
    let mut encoded = Cursor::new(Vec::new());
    thumbnail
        .write_to(&mut encoded, image::ImageFormat::WebP)
        .map_err(|error| format!("Could not encode screenshot thumbnail: {error}"))?;
    Ok(encoded.into_inner())
}

fn ensure_thumbnail(
    vault: &Path,
    store: &SessionStore,
    mut row: AttachmentRow,
) -> Result<AttachmentRow, String> {
    let root = attachment_root(vault, &row.owned_id)?;
    let root = canonical_root(&root)?;
    if row.thumbnail_mime_type.is_some()
        && row.thumbnail_byte_length.is_some()
        && row.thumbnail_relative_path.is_some()
    {
        let thumbnail_path = vault.join(
            row.thumbnail_relative_path
                .as_deref()
                .expect("thumbnail path was checked"),
        );
        if validate_optional_managed_file(&root, &thumbnail_path)?.is_some() {
            return Ok(row);
        }
    }
    let attachment_id =
        uuid::Uuid::parse_str(row.id.trim()).map_err(|_| "Attachment id is invalid".to_string())?;
    let original = vault.join(&row.relative_path);
    let original = validate_existing_managed_file(&root, &original, "screenshot")?;
    let bytes = fs::read(&original)
        .map_err(|error| format!("Could not read screenshot for thumbnail: {error}"))?;
    if bytes.is_empty() || bytes.len() > MAX_ATTACHMENT_BYTES {
        return Err("Image must be between 1 byte and 20 MB".to_string());
    }
    let thumbnail_bytes = thumbnail(&bytes)?;
    let thumbnail_name = format!("screenshot-{attachment_id}-thumb.webp");
    let thumbnail_path = root.join(&thumbnail_name);
    fs::write(&thumbnail_path, &thumbnail_bytes)
        .map_err(|error| format!("Could not save screenshot thumbnail: {error}"))?;
    if !thumbnail_path
        .canonicalize()
        .map_err(|error| format!("Could not verify screenshot thumbnail path: {error}"))?
        .starts_with(&root)
    {
        let _ = fs::remove_file(&thumbnail_path);
        return Err(
            "Attachment thumbnail path is outside the managed attachment folder".to_string(),
        );
    }
    let relative_path = format!("{}/{}", row.owned_id, thumbnail_name);
    let byte_length = i64::try_from(thumbnail_bytes.len())
        .map_err(|_| "Attachment thumbnail is too large to record".to_string())?;
    if let Err(error) =
        store.update_attachment_thumbnail(&row.id, THUMBNAIL_MIME_TYPE, byte_length, &relative_path)
    {
        let _ = fs::remove_file(&thumbnail_path);
        return Err(error.to_string());
    }
    row.thumbnail_mime_type = Some(THUMBNAIL_MIME_TYPE.to_string());
    row.thumbnail_byte_length = Some(byte_length);
    row.thumbnail_relative_path = Some(relative_path);
    Ok(row)
}

fn canonical_root(path: &Path) -> Result<PathBuf, String> {
    path.canonicalize()
        .map_err(|error| format!("Could not verify attachment folder: {error}"))
}

fn validate_delete_target(
    root: &Path,
    attachment_id: uuid::Uuid,
    path: &Path,
) -> Result<PathBuf, String> {
    let target = path
        .canonicalize()
        .map_err(|error| format!("Could not verify screenshot path: {error}"))?;
    if !target.starts_with(root) || target.parent() != Some(root) {
        return Err("Attachment path is outside the managed attachment folder".to_string());
    }
    let expected_prefix = format!("screenshot-{attachment_id}.");
    let file_name = target
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| "Attachment path has no valid file name".to_string())?;
    if !file_name.starts_with(&expected_prefix) {
        return Err("Attachment id does not match the managed screenshot path".to_string());
    }
    Ok(target)
}

fn validate_existing_managed_file(
    root: &Path,
    path: &Path,
    label: &str,
) -> Result<PathBuf, String> {
    let target = path
        .canonicalize()
        .map_err(|error| format!("Could not verify {label} path: {error}"))?;
    if !target.starts_with(root) || target.parent() != Some(root) {
        return Err(format!(
            "Attachment {label} path is outside the managed attachment folder"
        ));
    }
    Ok(target)
}

fn validate_optional_managed_file(root: &Path, path: &Path) -> Result<Option<PathBuf>, String> {
    let target = match path.canonicalize() {
        Ok(target) => target,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => {
            return Err(format!(
                "Could not verify screenshot thumbnail path: {error}"
            ))
        }
    };
    if !target.starts_with(root) || target.parent() != Some(root) {
        return Err(
            "Attachment thumbnail path is outside the managed attachment folder".to_string(),
        );
    }
    Ok(Some(target))
}

fn validate_delete_row(
    owned_id: &str,
    row: &AttachmentRow,
    attachment_id: uuid::Uuid,
    target: &Path,
) -> Result<(), String> {
    if row.owned_id != owned_id {
        return Err("Attachment row belongs to a different session".to_string());
    }
    if row.id != attachment_id.to_string() {
        return Err("Attachment row id does not match the request".to_string());
    }
    let file_name = target
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| "Attachment path has no valid file name".to_string())?;
    if row.file_name != file_name || row.relative_path != format!("{owned_id}/{file_name}") {
        return Err("Attachment path does not match the stored attachment row".to_string());
    }
    Ok(())
}

pub(super) fn safe_segment<'a>(value: &'a str, label: &str) -> Result<&'a str, String> {
    let value = value.trim();
    if value.is_empty()
        || value.contains('/')
        || value.contains('\\')
        || !value
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ch == '-' || ch == '_')
    {
        return Err(format!("{label} is invalid"));
    }
    Ok(value)
}

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|since_epoch| i64::try_from(since_epoch.as_millis()).ok())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::{
        attachment_root, delete, delete_at, read, read_chunk_at, read_original_at, safe_segment, save, save_at,
        validate_delete_target, validate_image, vault_root,
        DeleteConversationAttachmentRequest, MAX_ATTACHMENT_BYTES,
    };
    use mcb_core::session_store::{SessionRow, SessionStore};
    use std::fs;

    const PNG_1X1: &[u8] = &[
        0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, b'I', b'H', b'D',
        b'R', 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1f,
        0x15, 0xc4, 0x89, 0x00, 0x00, 0x00, 0x0a, b'I', b'D', b'A', b'T', 0x78, 0x9c, 0x63, 0x00,
        0x01, 0x00, 0x00, 0x05, 0x00, 0x01, 0x0d, 0x0a, 0x2d, 0xb4, 0x00, 0x00, 0x00, 0x00, b'I',
        b'E', b'N', b'D', 0xae, 0x42, 0x60, 0x82,
    ];

    const GIF_1X1: &[u8] = &[
        b'G', b'I', b'F', b'8', b'9', b'a', 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xff, 0xff, 0xff, 0x21, 0xf9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2c, 0x00, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3b,
    ];

    /// Attachment rows point at their session, so every test owns a stored session.
    fn store_with_session(owned_id: &str) -> SessionStore {
        let store = SessionStore::open_in_memory().expect("open in-memory session store");
        store
            .upsert_session(&SessionRow {
                owned_id: owned_id.to_owned(),
                native_session_id: None,
                provider: "provider-a".to_owned(),
                model: None,
                effort: None,
                cwd: "/work/project".to_owned(),
                worktree: None,
                branch: None,
                title: None,
                title_source: None,
                project: None,
                project_id: None,
                state: "idle".to_owned(),
                suspended: false,
                created_at_ms: 1_000,
                last_activity_at_ms: 2_000,
                extra_json: "{}".to_owned(),
            })
            .expect("insert owning session");
        store
    }

    #[test]
    fn saved_attachments_read_back_and_reflect_deletion() {
        let app = tauri::test::mock_app();
        let owned_id = format!("owned-{}", uuid::Uuid::new_v4());
        let store = store_with_session(&owned_id);
        let first = save(app.handle(), &store, &owned_id, "image/png", PNG_1X1).unwrap();
        let second = save(app.handle(), &store, &owned_id, "image/gif", GIF_1X1).unwrap();
        let mut expected = vec![first.clone(), second.clone()];
        expected.sort_by(|left, right| left.name.cmp(&right.name));

        assert_eq!(read(app.handle(), &store, &owned_id).unwrap(), expected);

        delete(
            app.handle(),
            &store,
            DeleteConversationAttachmentRequest {
                owned_id: owned_id.clone(),
                attachment_id: first.id,
                path: first.path,
            },
        )
        .unwrap();
        assert_eq!(read(app.handle(), &store, &owned_id).unwrap(), vec![second]);

        let root = attachment_root(&vault_root(app.handle()).unwrap(), &owned_id).unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn remote_vault_reads_and_deletes_only_the_owning_session() {
        let vault = std::env::temp_dir().join(format!("mcb-remote-attachment-{}", uuid::Uuid::new_v4()));
        let owned_id = format!("owned-{}", uuid::Uuid::new_v4());
        let store = store_with_session(&owned_id);
        let saved = save_at(&vault, &store, &owned_id, "image/png", PNG_1X1).unwrap();
        let other = save_at(&vault, &store, &owned_id, "image/gif", GIF_1X1).unwrap();
        assert_eq!(read_chunk_at(&vault, &store, &owned_id, &saved.id, false, 0, 128),
            Ok(PNG_1X1.to_vec()));
        assert!(read_chunk_at(&vault, &store, "other-session", &saved.id, false, 0, 128).is_err());
        let selected = store.get_attachments(&owned_id, &[saved.id.clone()]).unwrap();
        assert_eq!(selected.len(), 1);
        assert_eq!(read_original_at(&vault, &selected[0]), Ok(PNG_1X1.to_vec()));
        assert!(store.get_attachments("other-session", &[saved.id.clone()]).unwrap().is_empty());
        assert!(delete_at(&vault, &store, DeleteConversationAttachmentRequest {
            owned_id: "other-session".into(), attachment_id: saved.id.clone(), path: saved.path.clone(),
        }).is_err());
        assert!(std::path::Path::new(&saved.path).exists());
        delete_at(&vault, &store, DeleteConversationAttachmentRequest {
            owned_id: owned_id.clone(), attachment_id: saved.id, path: saved.path,
        }).unwrap();
        delete_at(&vault, &store, DeleteConversationAttachmentRequest {
            owned_id, attachment_id: other.id, path: other.path,
        }).unwrap();
        fs::remove_dir_all(vault).unwrap();
    }

    #[test]
    fn attachment_session_folder_rejects_path_traversal() {
        assert!(safe_segment("owned-good_1", "Owned session id").is_ok());
        assert!(safe_segment("../outside", "Owned session id").is_err());
    }

    #[test]
    fn attachment_validation_accepts_supported_image_signatures() {
        assert_eq!(validate_image("image/png", PNG_1X1).unwrap(), "png");
        assert_eq!(
            validate_image("image/jpeg", b"\xff\xd8\xffrest").unwrap(),
            "jpg"
        );
        assert_eq!(validate_image("image/gif", b"GIF89a").unwrap(), "gif");
        assert_eq!(
            validate_image("image/webp", b"RIFF0000WEBPrest").unwrap(),
            "webp"
        );
    }

    #[test]
    fn attachment_validation_rejects_spoofed_unsupported_and_oversized_images() {
        assert!(validate_image("image/png", b"not-a-png").is_err());
        assert!(validate_image("image/svg+xml", b"<svg></svg>").is_err());
        assert!(validate_image("image/png", &vec![0; MAX_ATTACHMENT_BYTES + 1]).is_err());
    }

    #[test]
    fn attachment_delete_target_stays_inside_the_owned_folder() {
        let root =
            std::env::temp_dir().join(format!("mcb-attachment-test-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&root).unwrap();
        let root = root.canonicalize().unwrap();
        let id = uuid::Uuid::new_v4();
        let target = root.join(format!("screenshot-{id}.png"));
        fs::write(&target, b"png").unwrap();
        assert!(validate_delete_target(&root, id, &target).is_ok());
        assert!(validate_delete_target(&root, id, &root.join("../outside.png")).is_err());
        fs::remove_dir_all(root).unwrap();
    }
}
