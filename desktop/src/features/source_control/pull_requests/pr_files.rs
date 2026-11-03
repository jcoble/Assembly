use super::{
    pull_request::{PullRequestComment, PullRequestDetail, PullRequestFile},
    review_draft::{ReviewDraft, ReviewIdentity},
};
use crate::core::{
    socket::{client::SocketClient, outcome::Outcome},
    ui::{diff::parse_patch, markdown::view::MarkdownView},
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::diff::{
    Diff, DiffAnnotation, DiffLinePosition, DiffMode, DiffSide, DiffState,
};
use gpui_kit::component::input::{InputEvent, Textarea, TextareaState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;
use std::collections::HashMap;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct FileContext {
    path: String,
    patch: String,
    binary: bool,
    truncated: bool,
}
struct CommentInput {
    input: Entity<TextareaState>,
    _heard: Subscription,
}
enum AnnotationContent {
    Draft(Entity<TextareaState>),
    Posted(PullRequestComment),
}
pub struct PrFiles {
    session: String,
    identity: ReviewIdentity,
    files: Vec<PullRequestFile>,
    posted: Vec<PullRequestComment>,
    draft: Entity<ReviewDraft>,
    selected: Option<String>,
    state: Entity<DiffState>,
    mode: DiffMode,
    loading: bool,
    context: bool,
    note: Option<String>,
    failure: Option<String>,
    active: bool,
    resume_read: bool,
    revision: u64,
    read: Option<Task<()>>,
    inputs: HashMap<String, CommentInput>,
    _draft: Subscription,
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
        Self {
            session,
            identity: ReviewIdentity {
                repository: detail.repository.clone(),
                number: detail.number,
                head: detail.head_sha.clone(),
                base: detail.base_sha.clone(),
            },
            files: detail.files.clone(),
            posted: detail.comments.clone(),
            draft,
            selected: None,
            state,
            mode: DiffMode::Unified,
            loading: false,
            context: false,
            note: None,
            failure: None,
            active: true,
            resume_read: false,
            revision: 0,
            read: None,
            inputs: HashMap::new(),
            _draft: heard,
        }
    }
    pub fn replace_detail(&mut self, detail: &PullRequestDetail, cx: &mut Context<Self>) {
        let identity = ReviewIdentity {
            repository: detail.repository.clone(),
            number: detail.number,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        };
        let changed = identity != self.identity;
        self.identity = identity;
        self.files = detail.files.clone();
        self.posted = detail.comments.clone();
        if self
            .selected
            .as_ref()
            .is_some_and(|path| !self.files.iter().any(|file| file.path == *path))
        {
            self.read = None;
            self.revision += 1;
            self.selected = None;
            self.loading = false;
            self.context = false;
        } else if changed {
            self.read = None;
            self.revision += 1;
            self.loading = false;
            self.context = false;
            if self.active {
                if let Some(path) = self.selected.clone() {
                    self.select(path, cx);
                }
            } else {
                self.resume_read = self.selected.is_some();
            }
        }
        cx.notify();
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        if self.active == active {
            return;
        }
        self.active = active;
        if !active {
            self.resume_read = self.loading;
            self.read = None;
            self.loading = false;
            self.revision += 1;
        } else if self.resume_read {
            self.resume_read = false;
            if let Some(path) = self.selected.clone() {
                self.select(path, cx);
            }
        }
        cx.notify();
    }
    pub fn select(&mut self, path: String, cx: &mut Context<Self>) {
        if !self.active || !self.files.iter().any(|file| file.path == path) {
            return;
        }
        self.read = None;
        self.revision += 1;
        let revision = self.revision;
        self.selected = Some(path.clone());
        self.loading = true;
        self.context = false;
        self.note = None;
        self.failure = None;
        let Some(socket) = SocketClient::try_bulk(cx) else {
            self.loading = false;
            self.failure = Some("The bulk connection isn't running.".into());
            cx.notify();
            return;
        };
        let call=socket.read(cx).call::<FileContext>("git","prFile",json!({"session":self.session,"number":self.identity.number,"path":path,"baseSha":self.identity.base,"headSha":self.identity.head}),None);
        self.read=Some(cx.spawn(async move|viewer,cx|{
            let answer=call.await;
            _=viewer.update(cx,|viewer,cx|{
                if viewer.revision!=revision||viewer.selected.as_deref()!=Some(path.as_str()){return;}
                viewer.read=None;viewer.loading=false;
                match answer {
                    Outcome::Ok(file)=>{
                        if file.path!=path{viewer.failure=Some("The file response did not match this selection.".into());}
                        else if file.binary{viewer.note=Some("Binary file. No patch available.".into());}
                        else if file.patch.trim().is_empty(){viewer.note=Some("No changes.".into());}
                        else {match parse_patch(&file.patch,file.truncated){
                            Ok(files)=>{viewer.state.update(cx,|state,cx|state.set_files(files,cx));viewer.context=true;
                                if file.truncated{viewer.note=Some("This diff is too long to show in full; the rest is cut.".into());}},
                            Err(error)=>viewer.failure=Some(error),
                        }}
                    },
                    Outcome::Failed(error)=>viewer.failure=Some(error.message),
                    Outcome::Aborted=>viewer.failure=Some("The file read was cancelled.".into()),
                }cx.notify();
            });
        }));
        cx.notify();
    }
    fn comment(&mut self, position: &DiffLinePosition, cx: &mut Context<Self>) {
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
        if let Err(error) = result {
            self.failure = Some(error);
        }
        cx.notify();
    }
    fn sync_inputs(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let comments = self.draft.read(cx).comments.clone();
        self.inputs
            .retain(|id, _| comments.iter().any(|comment| comment.id == *id));
        for comment in comments {
            if self.inputs.contains_key(&comment.id) {
                continue;
            }
            let input = cx.new(|cx| {
                TextareaState::new(window, cx)
                    .placeholder("Line comment")
                    .default_value(comment.target.body)
                    .auto_grow(2, 6)
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
}
impl Render for PrFiles {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.sync_inputs(window, cx);
        let danger = cx.theme().danger;
        let target = cx.entity().downgrade();
        let mut annotations = Vec::new();
        let mut contents: Vec<(ElementId, AnnotationContent)> = Vec::new();
        let draft = self.draft.read(cx);
        let current = draft.identity == self.identity;
        for comment in draft.comments.iter().filter(|_| current) {
            let id: ElementId = SharedString::from(format!("draft:{}", comment.id)).into();
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
            if let Some(input) = self.inputs.get(&comment.id) {
                contents.push((id, AnnotationContent::Draft(input.input.clone())));
            }
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
        let diff = Diff::new(&self.state)
            .annotations(annotations)
            .on_line_click(move |position, _, _, cx| {
                _ = target.update(cx, |viewer, cx| viewer.comment(position, cx));
            })
            .render_annotation(move |annotation, _, _| {
                match contents
                    .iter()
                    .find(|(id, _)| id == annotation.id())
                    .map(|(_, content)| content)
                {
                    Some(AnnotationContent::Draft(input)) => {
                        div().p_2().child(Textarea::new(input)).into_any_element()
                    }
                    Some(AnnotationContent::Posted(comment)) => v_flex()
                        .p_2()
                        .child(comment.author.clone())
                        .child(MarkdownView {
                            id: SharedString::from(format!("pr-comment:{}", comment.id)),
                            blocks: comment.body_blocks.clone(),
                            session: None,
                            expected_source: None,
                        })
                        .into_any_element(),
                    None => Empty.into_any_element(),
                }
            });
        let mut modes = h_flex().gap_1();
        for (label, mode) in [("Unified", DiffMode::Unified), ("Split", DiffMode::Split)] {
            modes = modes.child(
                Button::new(label)
                    .ghost()
                    .compact()
                    .label(label)
                    .disabled(self.mode == mode)
                    .on_click(cx.listener(move |viewer, _, _, cx| {
                        viewer.mode = mode;
                        viewer
                            .state
                            .update(cx, |state, cx| state.set_mode(mode, cx));
                        cx.notify();
                    })),
            );
        }
        let files = self.files.clone();
        v_flex().size_full().min_h_0().gap_1().child(modes)
            .child(h_flex().gap_1().flex_wrap().children(files.into_iter().map(|file|{
                Button::new(SharedString::from(format!("pr-file:{}",file.path))).ghost().compact().label(format!("{} +{} −{}",file.path,file.additions,file.deletions))
                    .on_click(cx.listener(move|viewer,_,_,cx|viewer.select(file.path.clone(),cx)))
            })))
            .children((!current).then(||div().px_2().text_sm().child("This review draft is for an earlier head. Its comments are retained; refresh before posting.")))
            .children(self.note.clone().map(|note|div().px_2().text_sm().child(note)))
            .children(self.failure.clone().map(|error|div().px_2().text_sm().text_color(danger).child(error)))
            .child(if self.loading{div().px_2().child("Reading file versions…").into_any_element()}
                else if self.context{div().flex_1().min_h_0().child(diff).into_any_element()}
                else if self.selected.is_none(){div().px_2().child("Choose a file to see its changes.").into_any_element()}
                else{Empty.into_any_element()})
    }
}

#[cfg(test)]
#[path = "pr_files_tests.rs"]
mod tests;
