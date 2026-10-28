//! `Attachment` is an image attached to the draft: its name and size for the chip, and where its upload is.
//! G0 makes no thumbnails; a sent message's lines may still name one, from the old app. A sent message carries each as an "Attached image: <path> (<name>) thumbnail: <path>" line after the
//! text, which the history reads back. Only png, jpeg, gif and webp up to 20 MB attach. Replaces attachments.ts.

use gpui_kit::*;

pub struct Attachment {
    /// Which chip it is while the draft holds it, so a finished upload finds its own.
    pub id: u64,
    pub name: SharedString,
    pub size: u64,
    pub state: AttachmentState,
}

pub enum AttachmentState {
    Uploading,
    /// Saved where the session's agent runs, at this path.
    Saved {
        path: String,
    },
    Failed(SharedString),
}

impl Attachment {
    /// Why a file can't attach: "not a supported image" or "larger than 20 MB"; `None` when it can.
    pub fn rejection(mime: &str, size: u64) -> Option<&'static str> {
        if !IMAGE_TYPES.contains(&mime) {
            return Some("not a supported image");
        }
        (size > MAX_BYTES).then_some("larger than 20 MB")
    }

    /// The image type a file's name says, by its extension; `None` for anything else.
    pub fn mime_of(name: &str) -> Option<&'static str> {
        let (_, extension) = name.rsplit_once('.')?;
        match extension.to_ascii_lowercase().as_str() {
            "png" => Some("image/png"),
            "jpg" | "jpeg" => Some("image/jpeg"),
            "gif" => Some("image/gif"),
            "webp" => Some("image/webp"),
            _ => None,
        }
    }

    /// The draft with one reference line per saved attachment after it.
    pub fn with_lines(text: &str, attachments: &[Attachment]) -> String {
        let lines: Vec<String> = attachments
            .iter()
            .filter_map(|attachment| match &attachment.state {
                AttachmentState::Saved { path } => {
                    Some(format!("{PREFIX}{path} ({})", one_line(&attachment.name)))
                }
                _ => None,
            })
            .collect();
        [text.to_string(), lines.join("\n")]
            .into_iter()
            .filter(|part| !part.is_empty())
            .collect::<Vec<_>>()
            .join("\n\n")
    }

    /// "512 B", "2.0 KB", "3.5 MB", for the chip.
    pub fn size_text(size: u64) -> String {
        const KB: f64 = 1024.0;
        match size as f64 {
            bytes if bytes < KB => format!("{size} B"),
            bytes if bytes < KB * KB => format!("{:.1} KB", bytes / KB),
            bytes => format!("{:.1} MB", bytes / (KB * KB)),
        }
    }

    /// Dropped files and folders as draft text: their paths, separated by spaces, a path with a space in quotes.
    pub fn paths_text(paths: &[String]) -> String {
        let quoted = |path: &String| {
            if path.contains(char::is_whitespace) {
                format!("\"{path}\"")
            } else {
                path.clone()
            }
        };
        paths.iter().map(quoted).collect::<Vec<_>>().join(" ")
    }

    /// `text` put in `draft` at the byte `caret`, with a space either side unless the draft has one there; the new
    /// draft and caret.
    pub fn insert_at(draft: &str, caret: usize, text: &str) -> (String, usize) {
        let caret = draft.floor_char_boundary(caret.min(draft.len()));
        let (before, after) = draft.split_at(caret);
        let space_before = before.chars().last().is_some_and(|c| !c.is_whitespace());
        let space_after = after.chars().next().is_some_and(|c| !c.is_whitespace());
        let inserted = format!(
            "{}{text}{}",
            if space_before { " " } else { "" },
            if space_after { " " } else { "" }
        );
        (format!("{before}{inserted}{after}"), caret + inserted.len())
    }

    /// A sent message's text and the attachments its trailing lines name, as (path, name, thumbnail).
    pub fn split_lines(text: &str) -> (String, Vec<(String, String, Option<String>)>) {
        if !text.contains(PREFIX) {
            return (text.to_string(), Vec::new());
        }
        let lines: Vec<&str> = text.split('\n').collect();
        let mut attachments = Vec::new();
        let mut end = lines.len();
        while end > 0
            && let Some(attachment) = reference(lines[end - 1])
        {
            attachments.insert(0, attachment);
            end -= 1;
        }
        if attachments.is_empty() {
            return (text.to_string(), attachments);
        }
        (lines[..end].join("\n").trim_end().to_string(), attachments)
    }
}

const PREFIX: &str = "Attached image: ";
/// The backend checks the same; checking here turns a file away when it's attached.
const IMAGE_TYPES: [&str; 4] = ["image/png", "image/jpeg", "image/gif", "image/webp"];
const MAX_BYTES: u64 = 20 * 1024 * 1024;

/// The name with each run of line breaks as one space, so it can't break its line.
fn one_line(name: &str) -> String {
    let mut out = String::new();
    let mut in_break = false;
    for c in name.chars() {
        let line_break = c == '\r' || c == '\n';
        if !line_break {
            out.push(c);
        } else if !in_break {
            out.push(' ');
        }
        in_break = line_break;
    }
    out
}

/// One "Attached image: /path (name)" line, with " thumbnail: /path" after it when the old app wrote one.
fn reference(line: &str) -> Option<(String, String, Option<String>)> {
    let (path, rest) = line.strip_prefix(PREFIX)?.split_once(' ')?;
    let rest = rest.strip_prefix('(')?;
    let absolute = |path: &str| path.starts_with('/') && !path.contains(char::is_whitespace);
    if !absolute(path) {
        return None;
    }
    if let Some(name) = rest.strip_suffix(')') {
        return Some((path.to_string(), name.to_string(), None));
    }
    let (name, thumbnail) = rest.rsplit_once(") thumbnail: ")?;
    absolute(thumbnail).then(|| {
        (
            path.to_string(),
            name.to_string(),
            Some(thumbnail.to_string()),
        )
    })
}
