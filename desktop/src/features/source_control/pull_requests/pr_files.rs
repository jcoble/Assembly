//! The Changes page: every changed file stacked in one diff, with a filter and file tree beside it. Comments stay on
//! the review draft, and a comment started here waits for "Add to review" before the Summary page counts on it.
use super::{
    pr_stack::{file_patch, matches_filter, no_lines_note},
    pull_request::{PullRequestComment, PullRequestDetail, PullRequestFile},
    review_draft::{DraftComment, ReviewDraft, ReviewIdentity},
};
use crate::core::{
    socket::{client::SocketClient, outcome::Outcome},
    ui::diff::parse_patch,
};
use gpui_kit::component::diff::{
    DiffAnnotation, DiffFile, DiffLinePosition, DiffMode, DiffSide, DiffState,
};
use gpui_kit::component::input::{InputEvent, InputState, TextareaState};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;
use std::collections::{HashMap, HashSet};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct FileContext {
    path: String,
    patch: String,
    binary: bool,
    truncated: bool,
}
/// What reading a file GitHub left out of its list found.
enum FullFile {
    Lines { patch: String, truncated: bool },
    Binary,
    Empty,
}
struct CommentInput {
    input: Entity<TextareaState>,
    _heard: Subscription,
}
struct FileNote {
    path: String,
    note: &'static str,
    can_read: bool,
}
#[derive(Clone)]
enum AnnotationContent {
    Adding(String, DraftComment, Entity<TextareaState>),
    Added(String, DraftComment),
    Posted(PullRequestComment),
}
pub struct PrFiles {
    session: String,
    identity: ReviewIdentity,
    open: bool,
    files: Vec<PullRequestFile>,
    posted: Vec<PullRequestComment>,
    draft: Entity<ReviewDraft>,
    state: Entity<DiffState>,
    parsed: Vec<DiffFile>,
    notes: Vec<FileNote>,
    full: HashMap<String, FullFile>,
    viewed: HashSet<String>,
    filter: String,
    filter_input: Option<(Entity<InputState>, Subscription)>,
    reading: Option<String>,
    failure: Option<String>,
    active: bool,
    resume_read: Option<String>,
    revision: u64,
    read: Option<Task<()>>,
    inputs: HashMap<String, CommentInput>,
    /// Comments still being written; the rest are on the review.
    adding: HashSet<String>,
    _draft: Subscription,
}
fn identity_of(detail: &PullRequestDetail) -> ReviewIdentity {
    ReviewIdentity {
        repository: detail.repository.clone(),
        number: detail.number,
        head: detail.head_sha.clone(),
        base: detail.base_sha.clone(),
    }
}
impl PrFiles {
    pub fn new(
        session: String,
        detail: &PullRequestDetail,
        draft: Entity<ReviewDraft>,
        cx: &mut Context<Self>,
    ) -> Self {
        let state = cx.new(|cx| DiffState::new([], cx));
        let heard = cx.observe(&draft, |_, _, cx| cx.notify());
        let mut files = Self {
            session,
            identity: identity_of(detail),
            open: detail.state == "OPEN",
            files: detail.files.clone(),
            posted: detail.comments.clone(),
            draft,
            state,
            parsed: Vec::new(),
            notes: Vec::new(),
            full: HashMap::new(),
            viewed: HashSet::new(),
            filter: String::new(),
            filter_input: None,
            reading: None,
            failure: None,
            active: true,
            resume_read: None,
            revision: 0,
            read: None,
            inputs: HashMap::new(),
            adding: HashSet::new(),
            _draft: heard,
        };
        files.rebuild(cx);
        files
    }
    pub fn replace_detail(&mut self, detail: &PullRequestDetail, cx: &mut Context<Self>) {
        let identity = identity_of(detail);
        if identity != self.identity {
            self.read = None;
            self.revision += 1;
            self.reading = None;
            self.resume_read = None;
            self.full.clear();
        }
        self.identity = identity;
        self.open = detail.state == "OPEN";
        self.files = detail.files.clone();
        self.posted = detail.comments.clone();
        self.rebuild(cx);
        cx.notify();
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        if self.active == active {
            return;
        }
        self.active = active;
        if !active {
            self.resume_read = self.reading.take();
            self.read = None;
            self.revision += 1;
        } else if let Some(path) = self.resume_read.take() {
            self.read_full(path, cx);
        }
        cx.notify();
    }
    pub fn set_mode(&mut self, mode: DiffMode, cx: &mut Context<Self>) {
        self.state.update(cx, |state, cx| state.set_mode(mode, cx));
    }
    pub fn mode(&self, cx: &App) -> DiffMode {
        self.state.read(cx).mode()
    }
    /// Parses every file on its own, so one bad patch costs a note rather than the page.
    fn rebuild(&mut self, cx: &mut Context<Self>) {
        self.parsed.clear();
        self.notes.clear();
        for file in &self.files {
            let (patch, truncated) = match self.full.get(&file.path) {
                Some(FullFile::Lines { patch, truncated }) => (Some(patch.clone()), *truncated),
                Some(FullFile::Binary) => {
                    self.notes.push(FileNote {
                        path: file.path.clone(),
                        note: "Binary file, so there is no line-by-line comparison.",
                        can_read: false,
                    });
                    continue;
                }
                Some(FullFile::Empty) => {
                    self.notes.push(FileNote {
                        path: file.path.clone(),
                        note: "No line changes to show.",
                        can_read: false,
                    });
                    continue;
                }
                None => (file_patch(file), false),
            };
            let Some(patch) = patch else {
                self.notes.push(FileNote {
                    path: file.path.clone(),
                    note: no_lines_note(file),
                    can_read: file.patch_omitted,
                });
                continue;
            };
            match parse_patch(&patch, truncated) {
                Ok(parsed) => self.parsed.extend(parsed),
                Err(_) => self.notes.push(FileNote {
                    path: file.path.clone(),
                    note: "This file's changes couldn't be read. Open it on GitHub.",
                    can_read: false,
                }),
            }
        }
        self.show_filtered(cx);
    }
    fn show_filtered(&mut self, cx: &mut Context<Self>) {
        let shown: Vec<DiffFile> = self
            .parsed
            .iter()
            .filter(|file| matches_filter(&file.path().to_string(), &self.filter))
            .cloned()
            .collect();
        let viewed = self.viewed.clone();
        self.state.update(cx, |state, cx| {
            state.set_files(shown, cx);
            for path in &viewed {
                state.set_file_collapsed(path, true, cx);
            }
        });
        cx.notify();
    }
    /// Reads a file GitHub left out of its list, at the exact revisions this page shows.
    pub fn read_full(&mut self, path: String, cx: &mut Context<Self>) {
        if !self.active || !self.files.iter().any(|file| file.path == path) {
            return;
        }
        self.read = None;
        self.revision += 1;
        let revision = self.revision;
        self.reading = Some(path.clone());
        self.failure = None;
        let Some(socket) = SocketClient::try_bulk(cx) else {
            self.reading = None;
            self.failure = Some("The bulk connection isn't running.".into());
            cx.notify();
            return;
        };
        let call = socket.read(cx).call::<FileContext>(
            "git",
            "prFile",
            json!({"session":self.session,"number":self.identity.number,"path":path,"baseSha":self.identity.base,"headSha":self.identity.head}),
            None,
        );
        self.read = Some(cx.spawn(async move |viewer, cx| {
            let answer = call.await;
            _ = viewer.update(cx, |viewer, cx| {
                if viewer.revision != revision {
                    return;
                }
                viewer.read = None;
                viewer.reading = None;
                match answer {
                    Outcome::Ok(file) if file.path != path => {
                        viewer.failure =
                            Some("The file response did not match this selection.".into());
                    }
                    Outcome::Ok(file) => {
                        let full = if file.binary {
                            FullFile::Binary
                        } else if file.patch.trim().is_empty() {
                            FullFile::Empty
                        } else {
                            FullFile::Lines {
                                patch: file.patch,
                                truncated: file.truncated,
                            }
                        };
                        viewer.full.insert(path, full);
                        viewer.rebuild(cx);
                    }
                    Outcome::Failed(error) => viewer.failure = Some(error.message),
                    Outcome::Aborted => {
                        viewer.failure = Some("The file read was cancelled.".into())
                    }
                }
                cx.notify();
            });
        }));
        cx.notify();
    }
    fn comment(&mut self, position: &DiffLinePosition, cx: &mut Context<Self>) {
        if !self.open {
            return;
        }
        if self.draft.read(cx).identity != self.identity {
            self.failure =
                Some("The review is for an earlier head. Refresh before posting.".into());
            cx.notify();
            return;
        }
        let result = self.draft.update(cx, |draft, cx| {
            let result = draft.start_comment(position);
            cx.notify();
            result
        });
        match result {
            Ok(id) => {
                self.adding.insert(id);
            }
            Err(error) => self.failure = Some(error),
        }
        cx.notify();
    }
    fn remove_comment(&mut self, id: &str, cx: &mut Context<Self>) {
        self.adding.remove(id);
        self.draft.update(cx, |draft, cx| {
            draft.remove_comment(id);
            cx.notify();
        });
    }
    fn toggle_viewed(&mut self, path: String, cx: &mut Context<Self>) {
        let viewed = !self.viewed.remove(&path);
        if viewed {
            self.viewed.insert(path.clone());
        }
        self.state
            .update(cx, |state, cx| state.set_file_collapsed(&path, viewed, cx));
        cx.notify();
    }
    fn sync_inputs(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let comments = self.draft.read(cx).comments.clone();
        self.inputs
            .retain(|id, _| comments.iter().any(|comment| comment.id == *id));
        self.adding
            .retain(|id| comments.iter().any(|comment| comment.id == *id));
        for comment in comments {
            if self.inputs.contains_key(&comment.id) {
                continue;
            }
            let input = cx.new(|cx| {
                TextareaState::new(window, cx)
                    .placeholder("Write a comment for this line")
                    .default_value(comment.target.body)
                    .auto_grow(3, 8)
            });
            let id = comment.id.clone();
            let heard = cx.subscribe(&input, move |viewer, input, event: &InputEvent, cx| {
                if matches!(event, InputEvent::Change) {
                    let body = input.read(cx).value().to_string();
                    viewer.draft.update(cx, |draft, cx| {
                        draft.update_comment(&id, body);
                        cx.notify();
                    });
                }
            });
            self.inputs.insert(
                comment.id,
                CommentInput {
                    input,
                    _heard: heard,
                },
            );
        }
    }
    fn sync_filter(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.filter_input.is_some() {
            return;
        }
        let input = cx.new(|cx| InputState::new(window, cx).placeholder("Filter files..."));
        let heard = cx.subscribe(
            &input,
            |viewer: &mut Self, input, event: &InputEvent, cx| {
                if matches!(event, InputEvent::Change) {
                    viewer.filter = input.read(cx).value().to_string();
                    viewer.show_filtered(cx);
                }
            },
        );
        self.filter_input = Some((input, heard));
    }
    fn annotations(&self, cx: &App) -> (Vec<DiffAnnotation>, Vec<(ElementId, AnnotationContent)>) {
        let mut annotations = Vec::new();
        let mut contents = Vec::new();
        let draft = self.draft.read(cx);
        let current = draft.identity == self.identity;
        for comment in draft.comments.iter().filter(|_| current) {
            let key = format!("draft:{}", comment.id);
            let id: ElementId = SharedString::from(key.clone()).into();
            let position = DiffLinePosition::new(
                comment.target.path.clone(),
                if comment.target.side == "LEFT" {
                    DiffSide::Original
                } else {
                    DiffSide::Modified
                },
                comment.target.line,
            );
            annotations.push(DiffAnnotation::line(id.clone(), position));
            let content = match self.inputs.get(&comment.id) {
                Some(input) if self.adding.contains(&comment.id) => {
                    AnnotationContent::Adding(key, comment.clone(), input.input.clone())
                }
                _ => AnnotationContent::Added(key, comment.clone()),
            };
            contents.push((id, content));
        }
        for comment in &self.posted {
            if let (Some(path), Some(line), Some(side)) =
                (&comment.path, comment.line, &comment.side)
            {
                if line == 0 || !matches!(side.as_str(), "LEFT" | "RIGHT") {
                    continue;
                }
                let id: ElementId = SharedString::from(format!("posted:{}", comment.id)).into();
                annotations.push(DiffAnnotation::line(
                    id.clone(),
                    DiffLinePosition::new(
                        path.clone(),
                        if side == "LEFT" {
                            DiffSide::Original
                        } else {
                            DiffSide::Modified
                        },
                        line,
                    ),
                ));
                contents.push((id, AnnotationContent::Posted(comment.clone())));
            }
        }
        (annotations, contents)
    }
}

#[cfg(test)]
#[path = "pr_files_tests.rs"]
mod tests;
#[path = "pr_view/files_view.rs"]
mod view;
