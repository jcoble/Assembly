//! The changed-file tree beside a stacked diff, one implementation for Changes and for a pull request. Rows are built
//! when the files or the filter change, not on each frame, and only the rows in view are drawn. Replaces the tree half
//! of MultiFileDiff.svelte and diffRows.ts changedFileTree.
use super::file_icon::file_icon;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use std::collections::{BTreeMap, HashMap, HashSet};
use std::rc::Rc;

pub const TREE_WIDTH: Pixels = px(280.);

pub fn matches_filter(path: &str, filter: &str) -> bool {
    let filter = filter.trim().to_lowercase();
    filter.is_empty() || path.to_lowercase().contains(&filter)
}
#[derive(Clone, Debug, PartialEq)]
pub struct TreeRow {
    pub depth: usize,
    pub label: String,
    /// The file this row opens; a folder has none.
    pub path: Option<String>,
    /// The folder's whole path, which is what opens and closes it; a file has none.
    pub folder: Option<String>,
    pub closed: bool,
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
    push_rows(&root, 0, "", &mut rows);
    rows
}
fn push_rows(folder: &Folder, depth: usize, parent: &str, rows: &mut Vec<TreeRow>) {
    for (name, child) in &folder.folders {
        let mut label = (*name).to_string();
        let mut child = child;
        while child.files.is_empty() && child.folders.len() == 1 {
            let (name, only) = child.folders.iter().next().expect("one folder");
            label = format!("{label}/{name}");
            child = only;
        }
        let whole = if parent.is_empty() {
            label.clone()
        } else {
            format!("{parent}/{label}")
        };
        rows.push(TreeRow {
            depth,
            label,
            path: None,
            folder: Some(whole.clone()),
            closed: false,
        });
        push_rows(child, depth + 1, &whole, rows);
    }
    for (name, path) in &folder.files {
        rows.push(TreeRow {
            depth,
            label: (*name).to_string(),
            path: Some((*path).to_string()),
            folder: None,
            closed: false,
        });
    }
}
/// The rows left once the closed folders hide what's under them.
pub fn open_rows(rows: Vec<TreeRow>, closed: &HashSet<String>) -> Vec<TreeRow> {
    let mut shown = Vec::with_capacity(rows.len());
    let mut hiding: Option<usize> = None;
    for mut row in rows {
        if let Some(depth) = hiding {
            if row.depth > depth {
                continue;
            }
            hiding = None;
        }
        if row
            .folder
            .as_ref()
            .is_some_and(|folder| closed.contains(folder))
        {
            hiding = Some(row.depth);
            row.closed = true;
        }
        shown.push(row);
    }
    shown
}
/// The paths in the order the tree lists them, which is the order their diffs stack in.
pub fn tree_order<'a>(paths: impl IntoIterator<Item = &'a str>) -> Vec<String> {
    changed_tree(paths)
        .into_iter()
        .filter_map(|row| row.path)
        .collect()
}
#[derive(Clone, Debug, Default, PartialEq)]
pub struct TreeFile {
    pub path: String,
    pub additions: usize,
    pub deletions: usize,
}
pub type TreeHandler = Rc<dyn Fn(&str, &mut Window, &mut App)>;

pub struct ChangedTree {
    files: Vec<TreeFile>,
    filter: String,
    closed: HashSet<String>,
    rows: Rc<Vec<TreeRow>>,
    counts: Rc<HashMap<String, (usize, usize)>>,
    list: UniformListScrollHandle,
}
impl ChangedTree {
    pub fn new() -> Self {
        Self {
            files: Vec::new(),
            filter: String::new(),
            closed: HashSet::new(),
            rows: Rc::new(Vec::new()),
            counts: Rc::new(HashMap::new()),
            list: UniformListScrollHandle::new(),
        }
    }
    /// A path listed twice, for a file's staged and working parts, is one row with their counts added up.
    pub fn set_files(&mut self, files: impl IntoIterator<Item = TreeFile>) {
        let mut merged: BTreeMap<String, TreeFile> = BTreeMap::new();
        for file in files {
            let row = merged.entry(file.path.clone()).or_insert_with(|| TreeFile {
                path: file.path,
                ..TreeFile::default()
            });
            row.additions += file.additions;
            row.deletions += file.deletions;
        }
        self.files = merged.into_values().collect();
        self.rebuild();
    }
    pub fn set_filter(&mut self, filter: &str) {
        if self.filter != filter {
            self.filter = filter.to_string();
            self.rebuild();
        }
    }
    pub fn toggle_folder(&mut self, folder: &str) {
        if !self.closed.remove(folder) {
            self.closed.insert(folder.to_string());
        }
        self.rebuild();
    }
    pub fn rows(&self) -> &[TreeRow] {
        &self.rows
    }
    fn rebuild(&mut self) {
        let rows = changed_tree(
            self.files
                .iter()
                .map(|file| file.path.as_str())
                .filter(|path| matches_filter(path, &self.filter)),
        );
        self.rows = Rc::new(open_rows(rows, &self.closed));
        self.counts = Rc::new(
            self.files
                .iter()
                .map(|file| (file.path.clone(), (file.additions, file.deletions)))
                .collect(),
        );
    }
    /// The list itself. A viewed file's row is dimmed; the active one is highlighted.
    pub fn list(
        &self,
        id: &'static str,
        viewed: Rc<HashSet<String>>,
        active: Option<String>,
        on_pick: TreeHandler,
        on_fold: TreeHandler,
    ) -> UniformList {
        let (rows, counts) = (self.rows.clone(), self.counts.clone());
        uniform_list(id, rows.len(), move |range, _, cx| {
            rows[range.clone()]
                .iter()
                .enumerate()
                .map(|(offset, row)| {
                    tree_row(
                        range.start + offset,
                        row,
                        &counts,
                        &viewed,
                        active.as_deref(),
                        &on_pick,
                        &on_fold,
                        cx,
                    )
                })
                .collect::<Vec<_>>()
        })
        .size_full()
        .track_scroll(&self.list)
    }
}
#[allow(clippy::too_many_arguments)]
fn tree_row(
    ix: usize,
    row: &TreeRow,
    counts: &HashMap<String, (usize, usize)>,
    viewed: &HashSet<String>,
    active: Option<&str>,
    on_pick: &TreeHandler,
    on_fold: &TreeHandler,
    cx: &App,
) -> AnyElement {
    let theme = cx.theme();
    let (muted, hover, secondary) = (theme.muted_foreground, theme.accent, theme.secondary);
    let line = h_flex()
        .id(ix)
        .w_full()
        .min_w_0()
        .h(px(26.))
        .gap_1()
        .pl(px(8. + row.depth as f32 * 12.))
        .pr_2()
        .text_role(TextRole::Body, cx)
        .cursor_pointer();
    if let Some(folder) = row.folder.clone() {
        let toggle = on_fold.clone();
        return line
            .text_color(muted)
            .hover(|line| line.bg(hover))
            .child(
                Icon::new(if row.closed {
                    IconName::ChevronRight
                } else {
                    IconName::ChevronDown
                })
                .size(px(12.))
                .text_color(muted),
            )
            .child(div().min_w_0().truncate().child(row.label.clone()))
            .on_click(move |_, window, cx| toggle(&folder, window, cx))
            .into_any_element();
    }
    let path = row.path.clone().unwrap_or_default();
    let (additions, deletions) = counts.get(&path).copied().unwrap_or_default();
    let selected = active == Some(path.as_str());
    let (tip, pick) = (path.clone(), on_pick.clone());
    let picked = path.clone();
    line.when(selected, |line| line.bg(secondary))
        .when(!selected, |line| line.hover(|line| line.bg(hover)))
        .when(viewed.contains(&path), |line| line.opacity(0.5))
        .child(file_icon(format!("tree-icon:{ix}"), &path, cx))
        .child(div().flex_1().min_w_0().truncate().child(row.label.clone()))
        .when(additions + deletions > 0, |line| {
            line.child(
                div()
                    .flex_none()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .child(format!("+{additions} -{deletions}")),
            )
        })
        .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
        .on_click(move |_, window, cx| pick(&picked, window, cx))
        .into_any_element()
}

#[cfg(test)]
#[path = "changed_tree_tests.rs"]
mod tests;
