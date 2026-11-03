use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::folder_browser::{FolderBrowser, FolderChosen};
use super::new_project::{Kind, NewProjectInput, NewProjectResult, ProjectRecord};
use crate::core::{
    commands::command_registry::CommandRegistry,
    intents::open_new_project::OpenNewProject,
    socket::{client::SocketClient, outcome::Outcome},
    state::machines::Machines,
};
use crate::features::source_control::git_operation::GitOperation;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;

pub struct Created(pub ProjectRecord);
impl EventEmitter<Created> for NewProjectForm {}
#[derive(Deserialize)]
struct RecentParent {
    path: Option<String>,
}
struct NewProjectForm {
    machine: String,
    kind: Kind,
    name: Entity<InputState>,
    parent: Entity<InputState>,
    source: Entity<InputState>,
    operation: Entity<GitOperation>,
    error: Option<String>,
    parent_revision: u64,
    emitted_version: u64,
    unconfirmed: bool,
    browser: Option<Entity<FolderBrowser>>,
    _browser_heard: Option<Subscription>,
    recent: Option<Task<()>>,
    _inputs: Vec<Subscription>,
    _operation: Subscription,
    _machines: Option<Subscription>,
    _created: Option<Subscription>,
}
pub fn register(cx: &mut App) {
    CommandRegistry::add(
        "New project",
        Box::new(OpenNewProject { machine: None }),
        cx,
    );
    cx.on_action(|intent: &OpenNewProject, cx| {
        let machine = intent.machine.clone().unwrap_or_else(|| "local".into());
        if let Some(window) = cx
            .active_window()
            .or_else(|| cx.windows().into_iter().next())
        {
            cx.defer(move |cx| {
                _ = window.update(cx, |_, window, cx| open(machine, window, cx));
            });
        }
    });
}
fn open(machine: String, window: &mut Window, cx: &mut App) {
    let form = cx.new(|cx| NewProjectForm::new(machine, window, cx));
    let handle = window.window_handle();
    let created = cx.subscribe(&form, move |_, event: &Created, cx| {
        // Registration has committed; normal Projects revision pushes own the visible registry refresh.
        let _ = &event.0;
        _ = handle.update(cx, |_, window, cx| window.close_dialog(cx));
    });
    form.update(cx, |form, _| form._created = Some(created));
    window.open_dialog(cx, move |dialog, _, cx| {
        let busy = form.read(cx).operation.read(cx).busy;
        dialog
            .title("New project")
            .close_button(!busy)
            .overlay_closable(!busy)
            .keyboard(!busy)
            .footer(Empty)
            .child(form.clone())
    });
}
impl NewProjectForm {
    fn new(machine: String, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let name = cx.new(|cx| InputState::new(window, cx).placeholder("Name"));
        let parent = cx.new(|cx| InputState::new(window, cx).placeholder("Choose a parent folder"));
        let source = cx.new(|cx| InputState::new(window, cx).placeholder("Repository URL or path"));
        let operation = cx.new(|_| GitOperation::default());
        let operation_heard=cx.observe(&operation,|form:&mut Self,_,cx|{
            if form.operation.read(cx).unconfirmed{form.unconfirmed=true;}
            let status=form.operation.read(cx).status.clone();
            if let Some(status)=status {
                if status.state=="succeeded"&&status.version>form.emitted_version {
                    form.emitted_version=status.version;
                    let result=status.result.and_then(|value|serde_json::from_value::<NewProjectResult>(value).ok());
                    match result {
                        Some(result)if result.project.machine==form.machine&&result.project.root_path==result.inspection.root_path&&result.project.removed_at.is_none()=>cx.emit(Created(result.project)),
                        _=>{form.unconfirmed=true;form.error=Some("The creation result was not confirmed. Refresh Projects before trying again.".into());},
                    }
                }
            }cx.notify();
        });
        let inputs = vec![
            cx.subscribe(&name, |_: &mut Self, _, _: &InputEvent, cx| cx.notify()),
            cx.subscribe(&source, |_: &mut Self, _, _: &InputEvent, cx| cx.notify()),
            cx.subscribe(&parent, |form: &mut Self, _, event: &InputEvent, cx| {
                if matches!(event, InputEvent::Change) {
                    form.parent_revision += 1;
                    cx.notify();
                }
            }),
        ];
        let machines = Machines::try_global(cx)
            .map(|machines| cx.observe(&machines, |_: &mut Self, _, cx| cx.notify()));
        let mut form = Self {
            machine,
            kind: Kind::Git,
            name,
            parent,
            source,
            operation,
            error: None,
            parent_revision: 0,
            emitted_version: 0,
            unconfirmed: false,
            browser: None,
            _browser_heard: None,
            recent: None,
            _inputs: inputs,
            _operation: operation_heard,
            _machines: machines,
            _created: None,
        };
        form.load_recent(window, cx);
        form
    }
    fn input(&self, cx: &App) -> NewProjectInput {
        NewProjectInput {
            machine: self.machine.clone(),
            parent: self.parent.read(cx).value().to_string(),
            name: self.name.read(cx).value().to_string(),
            kind: self.kind,
            source: self.source.read(cx).value().to_string(),
        }
    }
    fn pick_machine(&mut self, machine: String, window: &mut Window, cx: &mut Context<Self>) {
        if self.operation.read(cx).busy || self.machine == machine {
            return;
        }
        self.browser = None;
        self._browser_heard = None;
        self.machine = machine;
        self.parent_revision += 1;
        self.parent
            .update(cx, |input, cx| input.set_value("", window, cx));
        self.error = None;
        self.load_recent(window, cx);
        cx.notify();
    }
    fn browse(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.operation.read(cx).busy {
            return;
        }
        if self.browser.is_some() {
            self.browser = None;
            self._browser_heard = None;
            cx.notify();
            return;
        }
        let machine = self.machine.clone();
        let path = self.parent.read(cx).value().to_string();
        // Empty path is resolved by the existing owning-host folder producer, never by this window.
        let browser = cx.new(|cx| FolderBrowser::new(machine, path, cx));
        let handle = window.window_handle();
        self._browser_heard = Some(cx.subscribe(
            &browser,
            move |form, _, event: &FolderChosen, cx| {
                if event.machine != form.machine {
                    return;
                }
                let machine = event.machine.clone();
                let path = event.path.clone();
                let form = cx.entity().downgrade();
                cx.defer(move |cx| {
                    _ = handle.update(cx, |_, window, cx| {
                        form.update(cx, |form, cx| {
                            if form.machine != machine || form.operation.read(cx).busy {
                                return;
                            }
                            form.parent_revision += 1;
                            form.parent
                                .update(cx, |input, cx| input.set_value(path, window, cx));
                            form.browser = None;
                            form._browser_heard = None;
                            cx.notify();
                        })
                    });
                });
            },
        ));
        self.browser = Some(browser);
        cx.notify();
    }
    fn load_recent(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.recent = None;
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let machine = self.machine.clone();
        let revision = self.parent_revision;
        let handle = window.window_handle();
        let call = socket.read(cx).call::<RecentParent>(
            "projects",
            "recentParent",
            json!({"machine":machine}),
            Some("new-project/recent-parent"),
        );
        self.recent = Some(cx.spawn(async move |form, cx| {
            let outcome = call.await;
            _ = handle.update(cx, |_, window, cx| {
                form.update(cx, |form, cx| {
                    form.recent = None;
                    if form.machine != machine || form.parent_revision != revision {
                        return;
                    }
                    match outcome {
                        Outcome::Ok(recent) => {
                            if let Some(path) = recent.path {
                                form.parent
                                    .update(cx, |input, cx| input.set_value(path, window, cx));
                            }
                        }
                        Outcome::Failed(error) => form.error = Some(error.message),
                        Outcome::Aborted => {
                            form.error = Some("The recent parent folder did not answer.".into())
                        }
                    }
                    cx.notify();
                })
            });
        }));
    }
    fn create(&mut self, cx: &mut Context<Self>) {
        if self.operation.read(cx).busy || self.unconfirmed {
            return;
        }
        match self.input(cx).args() {
            Ok(args) => {
                self.error = None;
                self.operation.update(cx, |operation, cx| {
                    operation.start("createProject", None, args, cx)
                });
            }
            Err(error) => self.error = Some(error),
        }
        cx.notify();
    }
}
impl Render for NewProjectForm {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let busy = self.operation.read(cx).busy;
        let danger = cx.theme().danger;
        let machines = Machines::try_global(cx)
            .map(|machines| {
                machines
                    .read(cx)
                    .machines()
                    .filter_map(|machine| machine.name.clone())
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default();
        let mut places = h_flex().gap_1().flex_wrap();
        for machine in std::iter::once("local".to_string()).chain(machines) {
            let chosen = machine == self.machine;
            let label = if machine == "local" {
                "This computer".to_string()
            } else {
                machine.clone()
            };
            places = places.child(
                Button::new(SharedString::from(format!("new-project-machine:{machine}")))
                    .compact()
                    .label(label)
                    .when(chosen, |button| button.primary())
                    .when(!chosen, |button| button.ghost())
                    .disabled(busy)
                    .on_click(cx.listener(move |form, _, window, cx| {
                        form.pick_machine(machine.clone(), window, cx)
                    })),
            );
        }
        let mut kinds = h_flex().gap_1().flex_wrap();
        for kind in Kind::ALL {
            kinds = kinds.child(
                Button::new(kind.label())
                    .compact()
                    .label(kind.label())
                    .when(kind == self.kind, |button| button.primary())
                    .when(kind != self.kind, |button| button.ghost())
                    .disabled(busy)
                    .on_click(cx.listener(move |form, _, _, cx| {
                        form.kind = kind;
                        cx.notify();
                    })),
            );
        }
        let can_create = !busy && !self.unconfirmed && self.input(cx).args().is_ok();
        v_flex()
            .gap_2()
            .child("Makes a new folder and adds it as a project.")
            .child("Machine")
            .child(places)
            .child("Name")
            .child(Input::new(&self.name).disabled(busy))
            .child("Put it in")
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        div()
                            .flex_1()
                            .child(Input::new(&self.parent).disabled(busy)),
                    )
                    .child(
                        Button::new("new-project-browse")
                            .ghost()
                            .label(if self.browser.is_some() {
                                "Close folders"
                            } else {
                                "Browse…"
                            })
                            .disabled(busy)
                            .on_click(cx.listener(|form, _, window, cx| form.browse(window, cx))),
                    ),
            )
            .children(self.browser.clone())
            .child("Start with")
            .child(kinds)
            .when(self.kind == Kind::Clone, |form| {
                form.child(Input::new(&self.source).disabled(busy))
            })
            .children(
                self.error
                    .clone()
                    .map(|error| div().text_role(TextRole::Body, cx).text_color(danger).child(error)),
            )
            .when(self.operation.read(cx).unconfirmed, |form| {
                form.child(
                    Button::new("new-project-reconcile")
                        .ghost()
                        .label("Refresh operation status")
                        .on_click(cx.listener(|form, _, _, cx| {
                            form.operation
                                .update(cx, |operation, cx| operation.reconcile(cx))
                        })),
                )
            })
            .children(
                self.operation
                    .read(cx)
                    .error
                    .clone()
                    .map(|error| div().text_role(TextRole::Body, cx).text_color(danger).child(error)),
            )
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        Button::new("new-project-cancel")
                            .ghost()
                            .label("Cancel")
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    )
                    .when(busy && self.kind == Kind::Clone, |buttons| {
                        buttons.child(
                            Button::new("new-project-stop")
                                .ghost()
                                .label("Stop copying")
                                .on_click(cx.listener(|form, _, _, cx| {
                                    form.operation
                                        .update(cx, |operation, cx| operation.cancel(cx))
                                })),
                        )
                    })
                    .child(
                        Button::new("new-project-create")
                            .primary()
                            .label(if busy {
                                if self.kind == Kind::Clone {
                                    "Copying…"
                                } else {
                                    "Creating…"
                                }
                            } else {
                                "Create"
                            })
                            .disabled(!can_create)
                            .on_click(cx.listener(|form, _, _, cx| form.create(cx))),
                    ),
            )
    }
}
