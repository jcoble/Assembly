use super::project_dialog::{DialogMode, ProjectDialog};
use super::sample::{Machine, SampleProject, SampleProjects};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::command::{Command, CommandItem, CommandState};
use gpui_kit::component::input::{Input, InputEvent, InputState, Textarea, TextareaState};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{
    ActiveTheme as _, Disableable as _, Icon, Sizable as _, WindowExt as _, h_flex, v_flex,
};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use std::cell::Cell;

/// Navigation requests for the host; this view never creates a second Settings implementation.
#[derive(Clone, Copy, Debug)]
pub enum ProjectsEvent {
    ManageConnections { machine: Option<Machine> },
}

pub struct ProjectsSampleScreen {
    pub(super) sample: SampleProjects,
    filter: Entity<InputState>,
    draft: Entity<TextareaState>,
    machine: Option<Machine>,
    session: bool,
    pub(super) notice: String,
    _heard: Vec<Subscription>,
}
impl EventEmitter<ProjectsEvent> for ProjectsSampleScreen {}
impl ProjectsSampleScreen {
    pub(super) fn registration_completed(
        &mut self,
        id: usize,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.sample.registration_completed(id, self.session).is_ok() && self.session {
            self.draft.update(cx, |input, cx| input.focus(window, cx));
        }
    }

    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let filter = cx.new(|cx| InputState::new(window, cx).placeholder("Search projects…"));
        let draft = cx.new(|cx| {
            TextareaState::new(window, cx)
                .auto_grow(2, 5)
                .placeholder("Describe what you want to build…")
        });
        let heard = vec![
            cx.subscribe_in(&filter, window, |_, _, event, _, cx| {
                if matches!(event, InputEvent::Change) {
                    cx.notify();
                }
            }),
            cx.subscribe_in(&draft, window, |screen, input, event, _, cx| {
                if matches!(event, InputEvent::Change) {
                    screen.sample.draft = input.read(cx).value().to_string();
                }
            }),
        ];
        Self {
            sample: SampleProjects::default(),
            filter,
            draft,
            machine: None,
            session: false,
            notice: String::new(),
            _heard: heard,
        }
    }
    fn open_dialog(&mut self, mode: DialogMode, window: &mut Window, cx: &mut Context<Self>) {
        ProjectDialog::open(
            cx.entity().downgrade(),
            mode,
            self.machine.unwrap_or(Machine::Mac),
            window,
            cx,
        );
    }
    fn choose_project(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let state = cx.new(|cx| CommandState::new(window, cx));
        let screen = cx.entity().downgrade();
        let rows = self.sample.visible("", None);
        let selected = self.sample.selected;
        let online = Machine::ALL.map(|machine| self.sample.online(machine));
        let first = Cell::new(true);
        window.open_dialog(cx, move |dialog, window, cx| {
            if first.replace(false) {
                state.update(cx, |s, cx| s.focus(window, cx));
            }
            let ids: Vec<_> = std::iter::once(None)
                .chain(rows.iter().map(|p| Some(p.id)))
                .collect();
            let mut items = vec![
                CommandItem::new()
                    .label("No project")
                    .icon(IconName::MessageCircle)
                    .checked(selected.is_none()),
            ];
            items.extend(rows.iter().map(|p| {
                let project = p.clone();
                let available = online[Machine::ALL.iter().position(|m| *m == p.machine).unwrap()];
                CommandItem::new()
                    .label(format!(
                        "{} · {}{}",
                        p.title,
                        p.machine.label(),
                        if available { "" } else { " · Offline" }
                    ))
                    .keywords([p.root.clone()])
                    .child(move |_, cx| {
                        h_flex()
                            .gap_2()
                            .child(Icon::new(IconName::Folder).small())
                            .child(
                                v_flex()
                                    .min_w_0()
                                    .flex_1()
                                    .child(div().truncate().child(project.title.clone()))
                                    .child(
                                        div()
                                            .text_role(TextRole::Meta, cx)
                                            .text_color(cx.theme().muted_foreground)
                                            .truncate()
                                            .child(format!(
                                                "{} · {}{}",
                                                project.machine.label(),
                                                project.root,
                                                if available { "" } else { " · Offline" }
                                            )),
                                    ),
                            )
                    })
                    .icon(IconName::Folder)
                    .checked(Some(p.id) == selected)
                    .disabled(!available)
            }));
            let screen = screen.clone();
            dialog.title("Choose a project").child(
                Command::new(&state)
                    .placeholder("Search project title or path…")
                    .items(items)
                    .on_confirm(move |path, window, cx| {
                        if let Some(id) = ids.get(path.row) {
                            window.close_dialog(cx);
                            _ = screen.update(cx, |s, cx| {
                                if s.sample.select(*id).is_ok() {
                                    s.draft.update(cx, |d, cx| d.focus(window, cx));
                                }
                                cx.notify();
                            });
                        }
                    }),
            )
        });
    }
    fn start_sample(&mut self, id: Option<usize>, window: &mut Window, cx: &mut Context<Self>) {
        if self.sample.select(id).is_ok() {
            self.session = true;
            self.notice.clear();
            self.draft.update(cx, |d, cx| d.focus(window, cx));
            cx.notify();
        }
    }
    fn row(&self, project: SampleProject, cx: &mut Context<Self>) -> impl IntoElement {
        let id = project.id;
        let detail = format!("{}\n{}", project.title, project.root);
        h_flex()
            .gap_3()
            .py_2()
            .border_b_1()
            .border_color(cx.theme().border)
            .child(Icon::new(IconName::Folder).small())
            .child(
                v_flex()
                    .flex_1()
                    .min_w_0()
                    .gap_1()
                    .child(
                        h_flex().gap_2().child(
                            Button::new(SharedString::from(format!("project:{id}")))
                                .ghost()
                                .small()
                                .label(format!(
                                    "{}{}",
                                    if project.pinned { "★ " } else { "" },
                                    project.title
                                ))
                                .tooltip(detail)
                                .disabled(!self.sample.online(project.machine))
                                .on_click(cx.listener(move |s, _, window, cx| {
                                    s.start_sample(Some(id), window, cx)
                                })),
                        ),
                    )
                    .child(
                        div()
                            .text_role(TextRole::Meta, cx)
                            .text_color(cx.theme().muted_foreground)
                            .truncate()
                            .child(project.root.clone()),
                    ),
            )
            .child(
                div()
                    .w(px(130.))
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().muted_foreground)
                    .child(format!(
                        "{}{}",
                        project.machine.label(),
                        if self.sample.online(project.machine) {
                            ""
                        } else {
                            " · Offline"
                        }
                    )),
            )
            .child(
                div()
                    .w(px(100.))
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().muted_foreground)
                    .child(project.last_used),
            )
            .child(
                Button::new(SharedString::from(format!("session:{id}")))
                    .ghost()
                    .small()
                    .label("New session")
                    .disabled(!self.sample.online(project.machine))
                    .on_click(
                        cx.listener(move |s, _, window, cx| s.start_sample(Some(id), window, cx)),
                    ),
            )
            .child(self.project_actions(&project, id, cx))
    }
    fn project_actions(
        &self,
        project: &SampleProject,
        id: usize,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let screen = cx.entity().downgrade();
        let machine = project.machine;
        let pinned = project.pinned;
        Button::new(SharedString::from(format!("project-menu:{id}")))
            .ghost()
            .small()
            .label("…")
            .tooltip("Project actions")
            .dropdown_menu(move |menu, _, _| {
                let rename = screen.clone();
                let change = screen.clone();
                let pin = screen.clone();
                let remove = screen.clone();
                menu.item(
                    PopupMenuItem::new("Rename…").on_click(move |_, window, cx| {
                        ProjectDialog::open(
                            rename.clone(),
                            DialogMode::Rename(id),
                            machine,
                            window,
                            cx,
                        )
                    }),
                )
                .item(
                    PopupMenuItem::new("Change folder…").on_click(move |_, window, cx| {
                        ProjectDialog::open(
                            change.clone(),
                            DialogMode::ChangeFolder(id),
                            machine,
                            window,
                            cx,
                        )
                    }),
                )
                .item(
                    PopupMenuItem::new(if pinned { "Unpin" } else { "Pin" }).on_click(
                        move |_, _, cx| {
                            _ = pin.update(cx, |s, cx| {
                                match s.sample.pin(id, !pinned) {
                                    Ok(()) => {
                                        s.notice = "Sample pin updated in memory only.".into()
                                    }
                                    Err(e) => s.notice = e.into(),
                                };
                                cx.notify();
                            });
                        },
                    ),
                )
                .item(
                    PopupMenuItem::new("Remove…").on_click(move |_, window, cx| {
                        ProjectDialog::open(
                            remove.clone(),
                            DialogMode::Remove(id),
                            machine,
                            window,
                            cx,
                        )
                    }),
                )
            })
    }

    fn machines(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let mut pane = v_flex()
            .w(px(260.))
            .h_full()
            .gap_2()
            .p_4()
            .bg(cx.theme().group_box)
            .rounded_lg()
            .child(
                h_flex()
                    .justify_between()
                    .gap_2()
                    .child(div().font_weight(FontWeight::SEMIBOLD).child("Machines"))
                    .child(
                        Button::new("add-machine")
                            .ghost()
                            .small()
                            .label("Add machine")
                            .on_click(cx.listener(|_, _, _, cx| {
                                cx.emit(ProjectsEvent::ManageConnections { machine: None })
                            })),
                    ),
            );
        pane = pane.child(
            Button::new("all-machines")
                .ghost()
                .small()
                .label("All machines")
                .on_click(cx.listener(|s, _, _, cx| {
                    s.machine = None;
                    cx.notify();
                })),
        );
        for machine in Machine::ALL {
            let count = self
                .sample
                .projects
                .iter()
                .filter(|p| p.machine == machine && !p.removed)
                .count();
            let screen = cx.entity().downgrade();
            let connected = self.sample.online(machine);
            pane = pane.child(
                h_flex()
                    .gap_1()
                    .child(
                        Button::new(SharedString::from(format!("machine:{machine:?}")))
                            .small()
                            .when(self.machine == Some(machine), |b| b.secondary())
                            .when(self.machine != Some(machine), |b| b.ghost())
                            .label(format!("{}  ·  {count}", machine.label()))
                            .on_click(cx.listener(move |s, _, _, cx| {
                                s.machine = Some(machine);
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new(SharedString::from(format!("machine-menu:{machine:?}")))
                            .ghost()
                            .small()
                            .label("…")
                            .dropdown_menu(move |menu, _, _| {
                                let connect=screen.clone(); let create=screen.clone(); let add=screen.clone(); let manage=screen.clone();
                                let menu=if machine!=Machine::Mac {
                                    menu.item(PopupMenuItem::new(if connected{"Disconnect"}else{"Connect"}).on_click(move |_,_,cx|{
                                        _=connect.update(cx,|s,cx|{match s.sample.set_connected(machine,!connected){Ok(())=>s.notice="Sample connection state changed only. No real machine connection was attempted.".into(),Err(e)=>s.notice=e.into()};cx.notify();});
                                    }))
                                } else {menu};
                                menu.item(PopupMenuItem::new("New project here…").on_click(move |_,window,cx|ProjectDialog::open(create.clone(),DialogMode::Create,machine,window,cx)))
                                    .item(PopupMenuItem::new("Add folder here…").on_click(move |_,window,cx|ProjectDialog::open(add.clone(),DialogMode::Add,machine,window,cx)))
                                    .item(PopupMenuItem::new("Manage…").on_click(move |_,_,cx|{_=manage.update(cx,|_,cx|cx.emit(ProjectsEvent::ManageConnections{machine:Some(machine)}));}))
                            }),
                    ),
            );
            pane = pane.child(
                div()
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().muted_foreground)
                    .child(if connected {
                        "Sample · Connected"
                    } else {
                        "Sample · Offline"
                    }),
            );
        }
        pane
    }
    fn home(&mut self, cx: &mut Context<Self>) -> impl IntoElement {
        let rows = self
            .sample
            .visible(&self.filter.read(cx).value(), self.machine);
        let mut projects = v_flex()
            .flex_1()
            .min_w_0()
            .h_full()
            .gap_3()
            .p_4()
            .bg(cx.theme().group_box)
            .rounded_lg()
            .child(
                h_flex()
                    .justify_between()
                    .gap_4()
                    .child(div().font_weight(FontWeight::SEMIBOLD).child("Projects"))
                    .child(
                        div().w(px(260.)).child(
                            Input::new(&self.filter)
                                .small()
                                .cleanable(true)
                                .prefix(Icon::new(IconName::Search).small()),
                        ),
                    ),
            )
            .child(
                h_flex()
                    .justify_between()
                    .child(
                        Button::new("machine-filter")
                            .ghost()
                            .small()
                            .label(
                                self.machine
                                    .map(|m| format!("Showing: {}  ×", m.label()))
                                    .unwrap_or_else(|| "All machines".into()),
                            )
                            .on_click(cx.listener(|s, _, _, cx| {
                                s.machine = None;
                                cx.notify();
                            })),
                    )
                    .child(
                        h_flex()
                            .gap_2()
                            .child(
                                Button::new("new-project")
                                    .primary()
                                    .small()
                                    .icon(IconName::Plus)
                                    .label("New project")
                                    .on_click(cx.listener(|s, _, w, cx| {
                                        s.open_dialog(DialogMode::Create, w, cx)
                                    })),
                            )
                            .child(
                                Button::new("add-folder")
                                    .ghost()
                                    .small()
                                    .icon(IconName::FolderPlus)
                                    .label("Add folder")
                                    .on_click(cx.listener(|s, _, w, cx| {
                                        s.open_dialog(DialogMode::Add, w, cx)
                                    })),
                            ),
                    ),
            );
        if rows.is_empty() {
            projects = projects.child(
                v_flex()
                    .py_8()
                    .items_center()
                    .gap_2()
                    .child(if self.sample.projects.is_empty() {
                        "No projects yet."
                    } else {
                        "No projects match this search or machine."
                    })
                    .child(
                        Button::new("empty-add")
                            .small()
                            .label("Add existing folder")
                            .on_click(
                                cx.listener(|s, _, w, cx| s.open_dialog(DialogMode::Add, w, cx)),
                            ),
                    ),
            );
        }
        let mut list = v_flex()
            .id("sample-project-list")
            .overflow_y_scroll()
            .flex_1()
            .min_h_0();
        for row in rows {
            list = list.child(self.row(row, cx));
        }
        h_flex()
            .flex_1()
            .min_h_0()
            .gap_4()
            .child(projects.child(list))
            .child(self.machines(cx))
    }
    fn session(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let screen = cx.entity().downgrade();
        v_flex().flex_1().items_center().justify_center().gap_4()
            .child(h_flex().gap_2().child(div().font_weight(FontWeight::SEMIBOLD).child("What should we build in")).child(Button::new("choose-project").secondary().label(self.sample.selected_title()).dropdown_caret(true).on_click(cx.listener(|s,_,w,cx|s.choose_project(w,cx)))))
            .child(v_flex().w_full().max_w(px(800.)).gap_2().child(Textarea::new(&self.draft)).child(h_flex().justify_between().child(div().text_role(TextRole::Meta, cx).text_color(cx.theme().muted_foreground).child("Codex · This is a sample draft"))
                .child(Button::new("sample-send").primary().label("Preview session").on_click(move |_,_,cx|{_=screen.update(cx,|s,cx|{s.notice="Sample draft retained. No agent or session was started.".into();cx.notify();});}))))
            .child(Button::new("session-new-project").ghost().small().icon(IconName::FolderPlus).label("New project").on_click(cx.listener(|s,_,w,cx|s.open_dialog(DialogMode::Create,w,cx))))
    }
}
impl Render for ProjectsSampleScreen {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let body = if self.session {
            self.session(cx).into_any_element()
        } else {
            self.home(cx).into_any_element()
        };
        v_flex().size_full().min_h_0().p_4().gap_3().text_role(TextRole::Body, cx).text_color(cx.theme().foreground)
            .child(h_flex().justify_between().child(div().text_role(TextRole::Meta, cx).text_color(cx.theme().muted_foreground).child("Sample data · Changes stay in this preview. No folders, repositories or sessions are created."))
                .child(Button::new("sample-view-toggle").ghost().small().label(if self.session{"← Projects"}else{"New session"}).on_click(cx.listener(|s,_,_,cx|{s.session = !s.session;cx.notify();}))))
            .when(!self.notice.is_empty(),|v|v.child(div().text_role(TextRole::Meta, cx).child(self.notice.clone())))
            .child(body)
    }
}
