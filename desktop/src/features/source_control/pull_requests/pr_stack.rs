//! The Changes page's single scrolling diff and its file tree, as plain data. GitHub lists each file's hunks without a
//! header, so each gets the header the diff parser needs; a file with nothing to compare becomes a note instead.
use super::pull_request::PullRequestFile;
use gpui_kit::component::diff::DiffSide;
use std::collections::BTreeMap;

/// The whole patch for one file, or nothing when GitHub sent no lines for it.
pub fn file_patch(file: &PullRequestFile) -> Option<String> {
    let hunks = file
        .patch
        .as_deref()
        .filter(|hunks| !hunks.trim().is_empty())?;
    let old = file.previous_path.as_deref().unwrap_or(&file.path);
    let new = &file.path;
    let (from, to) = match file.status.as_str() {
        "added" => ("/dev/null".to_string(), format!("b/{new}")),
        "removed" => (format!("a/{old}"), "/dev/null".to_string()),
        _ => (format!("a/{old}"), format!("b/{new}")),
    };
    let moved = if old == new {
        String::new()
    } else {
        format!("rename from {old}\nrename to {new}\n")
    };
    Some(format!(
        "diff --git a/{old} b/{new}\n{moved}--- {from}\n+++ {to}\n{}\n",
        hunks.trim_end_matches('\n')
    ))
}
/// Why a file with no lines shows none. An omitted file can still be read whole on request.
/// The last line a patch shows, and the side it's on: where a file's comments gather, under its code.
pub fn patch_end(patch: &str) -> Option<(DiffSide, usize)> {
    let mut lines = None;
    let mut end = None;
    for text in patch.lines() {
        if let Some(hunk) = text.strip_prefix("@@ -") {
            let (old, rest) = hunk.split_once(" +")?;
            let new = rest.split_once(" @@")?.0;
            let start = |range: &str| range.split(',').next()?.parse::<usize>().ok();
            lines = Some((start(old)?, start(new)?));
            continue;
        }
        let Some((old, new)) = lines.as_mut() else {
            continue;
        };
        match text.as_bytes().first() {
            Some(b'-') => {
                end = Some((DiffSide::Original, *old));
                *old += 1;
            }
            Some(b'+') => {
                end = Some((DiffSide::Modified, *new));
                *new += 1;
            }
            Some(b'\\') => {}
            _ => {
                end = Some((DiffSide::Modified, *new));
                *old += 1;
                *new += 1;
            }
        }
    }
    end
}
pub fn no_lines_note(file: &PullRequestFile) -> &'static str {
    if file.patch_omitted {
        return "GitHub left this file's changes out because they're large.";
    }
    let changed = file.additions + file.deletions > 0;
    let moved_only = matches!(file.status.as_str(), "renamed" | "unchanged");
    if changed || !moved_only {
        "Binary file, so there is no line-by-line comparison."
    } else {
        "No line changes to show."
    }
}
pub fn matches_filter(path: &str, filter: &str) -> bool {
    let filter = filter.trim().to_lowercase();
    filter.is_empty() || path.to_lowercase().contains(&filter)
}
#[derive(Debug, PartialEq)]
pub struct TreeRow {
    pub depth: usize,
    pub label: String,
    /// The file this row opens; a folder has none.
    pub path: Option<String>,
}
#[derive(Default)]
struct Folder<'a> {
    folders: BTreeMap<&'a str, Folder<'a>>,
    files: BTreeMap<&'a str, &'a str>,
}
/// Folders first, then files, each sorted; a folder holding only one folder shares its row, as `a/b`.
pub fn changed_tree<'a>(paths: impl IntoIterator<Item = &'a str>) -> Vec<TreeRow> {
    let mut root = Folder::default();
    for path in paths {
        let mut folder = &mut root;
        let mut parts = path.split('/').peekable();
        while let Some(part) = parts.next() {
            if parts.peek().is_some() {
                folder = folder.folders.entry(part).or_default();
            } else {
                folder.files.insert(part, path);
            }
        }
    }
    let mut rows = Vec::new();
    push_rows(&root, 0, &mut rows);
    rows
}
fn push_rows(folder: &Folder, depth: usize, rows: &mut Vec<TreeRow>) {
    for (name, child) in &folder.folders {
        let mut label = (*name).to_string();
        let mut child = child;
        while child.files.is_empty() && child.folders.len() == 1 {
            let (name, only) = child.folders.iter().next().expect("one folder");
            label = format!("{label}/{name}");
            child = only;
        }
        rows.push(TreeRow {
            depth,
            label,
            path: None,
        });
        push_rows(child, depth + 1, rows);
    }
    for (name, path) in &folder.files {
        rows.push(TreeRow {
            depth,
            label: (*name).to_string(),
            path: Some((*path).to_string()),
        });
    }
}

#[cfg(test)]
#[path = "pr_stack_tests.rs"]
mod tests;
