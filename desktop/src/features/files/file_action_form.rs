//! Native confirmation/name dialog. One owned socket attempt; confirmed filesystem changes precede tab metadata.
use super::document_store::{DocumentKey, DocumentStore};
use super::entry::FileOwnership;
use super::file_actions::{ActionReply, ActionRun, ActionState, named_target};
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::ui::center_panes::CenterPanes;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

pub(super) struct Finished {
    pub session: String,
    pub moved: Option<DocumentKey>,
}
pub(super) struct FileActionForm {
    session: String,
    kind: &'static str,
    source: String,
    destination: Option<String>,
    _name_event: Option<Subscription>,
    expected_owner: Option<DocumentKey>,
    name: Option<Entity<InputState>>,
    run: ActionRun,
    task: Option<Task<()>>,
}
impl EventEmitter<Finished> for FileActionForm {}
impl FileActionForm {
    pub fn new(
        session: String,
        kind: &'static str,
        source: String,
        destination: Option<String>,
        expected_owner: Option<DocumentKey>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let name = matches!(kind, "rename" | "createFile" | "createFolder").then(|| {
            cx.new(|cx| {
                let mut input = InputState::new(window, cx).placeholder("Name");
                if kind == "rename" {
                    input.set_value(source.rsplit('/').next().unwrap_or_default(), window, cx);
                }
                input
            })
        });
        let name_event = name.as_ref().map(|name| {
            cx.subscribe_in(
                name,
                window,
                |form: &mut Self, _, event: &InputEvent, window, cx| {
                    if matches!(event, InputEvent::PressEnter { .. }) {
                        form.run(window, cx);
                    }
                },
            )
        });
        Self {
            session,
            kind,
            source,
            destination,
            expected_owner,
            _name_event: name_event,
            name,
            run: ActionRun::default(),
            task: None,
        }
    }
    pub fn busy(&self) -> bool {
        self.run.state == ActionState::Running
    }
    pub fn title(&self) -> &'static str {
        match self.kind {
            "createFile" => "New file",
            "createFolder" => "New folder",
            "rename" => "Rename",
            "copy" => "Copy",
            "move" => "Move",
            _ => "Move to Trash",
        }
    }
    fn paths(&self, cx: &App) -> Result<(String, Option<String>), String> {
        if let Some(name) = &self.name {
            let name = name.read(cx).value().to_string();
            let folder = if self.kind == "rename" {
                self.source
                    .rsplit_once('/')
                    .map_or("", |(parent, _)| parent)
            } else {
                &self.source
            };
            let target = named_target(folder, &name).map_err(str::to_string)?;
            if self.kind == "rename" {
                Ok((self.source.clone(), Some(target)))
            } else {
                Ok((target, None))
            }
        } else {
            Ok((self.source.clone(), self.destination.clone()))
        }
    }
    fn run(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let (source, destination) = match self.paths(cx) {
            Ok(paths) => paths,
            Err(error) => {
                self.run.error = Some(error);
                cx.notify();
                return;
            }
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            self.run.error = Some("The backend isn't connected.".into());
            cx.notify();
            return;
        };
        if !self.run.begin() {
            return;
        }
        let session = self.session.clone();
        let kind = self.kind;
        let handle = window.window_handle();
        let owner = socket.read(cx).call::<FileOwnership>(
            "files",
            "owner",
            json!({"session":session}),
            None,
        );
        self.task = Some(cx.spawn(async move |form, cx| {
            let owner = match owner.await {
                Outcome::Ok(owner) => owner,
                outcome => { let _ = form.update(cx, |form, cx| {
                    form.task = None; form.run.state = ActionState::Failed;
                    form.run.error = Some(match outcome { Outcome::Failed(error) => error.message, _ => "The owning checkout could not be read.".into() }); cx.notify();
                }); return; }
            };
            let same_owner = form.update(cx, |form, cx| {
                if form.expected_owner.as_ref().is_some_and(|expected| expected.machine != owner.machine || expected.checkout_root != owner.checkout_root) {
                    form.task = None; form.run.state = ActionState::Failed;
                    form.run.error = Some("Paste is limited to the same owning checkout. The copied item was not touched.".into()); cx.notify(); false
                } else { true }
            }).unwrap_or(false);
            if !same_owner { return; }
            let mut versions: Vec<(DocumentKey, u64)> = Vec::new();
            let prepared = form.update(cx, |form, cx| {
                if matches!(kind, "rename" | "move") {
                    let Some(target) = destination.as_deref() else { return false; };
                    if CenterPanes::can_rename_file_aliases(&owner.machine, &owner.checkout_root, &source, target, cx).is_err() {
                        form.run.state = ActionState::Failed;
                        form.run.error = Some("An open document is busy or the destination already has a draft. Nothing was moved.".into());
                        form.task = None; cx.notify(); return false;
                    }
                    if let Some(docs) = cx.try_global::<DocumentStore>() { versions = docs.versions_under(&owner.machine, &owner.checkout_root, &source); }
                }
                true
            }).unwrap_or(false);
            if !prepared { return; }
            let expected = destination.clone().unwrap_or_else(|| source.clone());
            let Ok(call) = form.update(cx, |_, cx| socket.read(cx).call::<ActionReply>("files", "action",
                json!({"session":session,"kind":kind,"path":source,"destination":destination}), None)) else { return; };
            let answer = call.await;
            let _ = handle.update(cx, |_, window, cx| form.update(cx, |form, cx| {
                form.task = None;
                let applied = match answer { Outcome::Ok(reply) => form.run.complete(&expected, reply), _ => { form.run.unknown(); false } };
                if applied && matches!(kind, "rename" | "move") {
                    let unchanged = cx.try_global::<DocumentStore>().map(|docs| docs.versions_under(&owner.machine, &owner.checkout_root, &source)).unwrap_or_default() == versions;
                    if !unchanged || CenterPanes::rename_file_aliases(&owner.machine, &owner.checkout_root, &source, &expected, window, cx).is_err() {
                        form.run.error = Some("The file was moved on disk, but open drafts changed meanwhile. All drafts are preserved at their previous paths; inspect both paths before continuing.".into());
                    }
                }
                cx.emit(Finished { session: session.clone(), moved: (applied && kind == "move").then(|| form.expected_owner.clone()).flatten() });
                if applied && form.run.error.is_none() { window.close_dialog(cx); }
                cx.notify();
            }));
        }));
        cx.notify();
    }
}
impl Render for FileActionForm {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let busy = self.busy();
        v_flex()
            .gap_2()
            .child(self.source.clone())
            .when(self.kind == "trash", |view| {
                view.child("Move this item to the host's Trash?")
            })
            .children(
                self.name
                    .as_ref()
                    .map(|name| Input::new(name).disabled(busy)),
            )
            .children(
                self.run
                    .error
                    .clone()
                    .map(|error| div().text_sm().text_color(cx.theme().danger).child(error)),
            )
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        Button::new("file-action-cancel")
                            .ghost()
                            .label(
                                if matches!(
                                    self.run.state,
                                    ActionState::Applied | ActionState::Unknown
                                ) {
                                    "Close"
                                } else {
                                    "Cancel"
                                },
                            )
                            .disabled(busy)
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    )
                    .child(
                        Button::new("file-action-apply")
                            .primary()
                            .label(if busy { "Working…" } else { self.title() })
                            .disabled(!matches!(
                                self.run.state,
                                ActionState::Idle | ActionState::Failed
                            ))
                            .on_click(cx.listener(|form, _, window, cx| form.run(window, cx))),
                    ),
            )
    }
}
