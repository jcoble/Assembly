use super::projects_screen::ProjectsSampleScreen;
use super::sample::{Machine, SAMPLE_FOLDER_LIMITATION, StartWith, normalize_path, parent_path};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::command::{Command, CommandItem, CommandState};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::label::Label;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use std::cell::Cell;

#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) enum DialogMode {
    Create,
    Add,
    Rename(usize),
    ChangeFolder(usize),
    Remove(usize),
}
pub(super) struct ProjectDialog {
    screen: WeakEntity<ProjectsSampleScreen>,
    mode: DialogMode,
    machine: Machine,
    name: Entity<InputState>,
    path: Entity<InputState>,
    source: Entity<InputState>,
    start: StartWith,
    browsing: bool,
    error: String,
    folder_list: Entity<CommandState>,
    _heard: Vec<Subscription>,
}
impl ProjectDialog {
    pub(super) fn open(
        screen: WeakEntity<ProjectsSampleScreen>,
        mode: DialogMode,
        machine: Machine,
        window: &mut Window,
        cx: &mut App,
    ) {
        let view = cx.new(|cx| Self::new(screen, mode, machine, window, cx));
        let first = Cell::new(true);
        window.open_dialog(cx, move |dialog, window, cx| {
            if first.replace(false) && !matches!(mode, DialogMode::Remove(_)) {
                let field = if matches!(mode, DialogMode::Create | DialogMode::Rename(_)) {
                    view.read(cx).name.clone()
                } else {
                    view.read(cx).path.clone()
                };
                field.update(cx, |input, cx| input.focus(window, cx));
            }
            dialog
                .title(match mode {
                    DialogMode::Create => "New project",
                    DialogMode::Add => "Add existing folder",
                    DialogMode::Rename(_) => "Rename project",
                    DialogMode::ChangeFolder(_) => "Change folder",
                    DialogMode::Remove(_) => "Remove project",
                })
                .w(px(560.))
                .centered()
                .on_ok(|_, _, _| false)
                .child(view.clone())
        });
    }
    fn new(
        screen: WeakEntity<ProjectsSampleScreen>,
        mode: DialogMode,
        machine: Machine,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let name = cx.new(|cx| InputState::new(window, cx).placeholder("Project name"));
        let path = cx.new(|cx| InputState::new(window, cx).placeholder("Absolute folder path"));
        path.update(cx, |p, cx| p.set_value(machine.home(), window, cx));
        let project_id = match mode {
            DialogMode::Rename(id) | DialogMode::ChangeFolder(id) | DialogMode::Remove(id) => {
                Some(id)
            }
            _ => None,
        };
        if let Some(id) = project_id {
            let project = screen.upgrade().and_then(|s| {
                s.read(cx)
                    .sample
                    .projects
                    .iter()
                    .find(|p| p.id == id)
                    .cloned()
            });
            if let Some(project) = project {
                name.update(cx, |n, cx| n.set_value(project.title, window, cx));
                path.update(cx, |p, cx| p.set_value(project.root, window, cx));
            }
        }
        let source = cx.new(|cx| {
            InputState::new(window, cx).placeholder("https://github.com/owner/repository.git")
        });
        let folder_list = cx.new(|cx| CommandState::new(window, cx));
        let heard = vec![
            cx.subscribe_in(&path, window, |s, _, event, window, cx| {
                if matches!(
                    event,
                    InputEvent::PressEnter {
                        secondary: true,
                        ..
                    }
                ) {
                    s.confirm(window, cx);
                    return;
                }
                if matches!(event, InputEvent::Change) {
                    s.error.clear();
                    cx.notify();
                }
            }),
            cx.subscribe_in(&name, window, |s, _, event, window, cx| {
                if matches!(event, InputEvent::PressEnter { .. }) && !s.browsing {
                    s.confirm(window, cx);
                    return;
                }
                if matches!(event, InputEvent::Change) {
                    s.error.clear();
                    cx.notify();
                }
            }),
            cx.subscribe_in(&source, window, |s, _, event, window, cx| {
                if matches!(event, InputEvent::PressEnter { .. }) && !s.browsing {
                    s.confirm(window, cx);
                    return;
                }
                if matches!(event, InputEvent::Change) {
                    s.error.clear();
                    cx.notify();
                }
            }),
        ];
        Self {
            screen,
            mode,
            machine,
            name,
            path,
            source,
            start: StartWith::Empty,
            browsing: matches!(mode, DialogMode::Add | DialogMode::ChangeFolder(_)),
            error: String::new(),
            folder_list,
            _heard: heard,
        }
    }
    fn machine_menu(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let dialog = cx.entity().downgrade();
        let machine = self.machine;
        let online = Machine::ALL.map(|m| {
            self.screen
                .upgrade()
                .is_some_and(|s| s.read(cx).sample.online(m))
        });
        Button::new("project-machine")
            .secondary()
            .label(machine.label())
            .dropdown_caret(true)
            .dropdown_menu(move |menu, _, _| {
                Machine::ALL.into_iter().fold(menu, |menu, m| {
                    let dialog = dialog.clone();
                    menu.item(
                        PopupMenuItem::new(format!(
                            "{}{}",
                            m.label(),
                            if online[Machine::ALL.iter().position(|item| *item == m).unwrap()] {
                                ""
                            } else {
                                " · Offline"
                            }
                        ))
                        .checked(machine == m)
                        .on_click(move |_, window, cx| {
                            _ = dialog.update(cx, |s, cx| {
                                s.machine = m;
                                s.path.update(cx, |p, cx| p.set_value(m.home(), window, cx));
                                s.error.clear();
                                cx.notify();
                            });
                        }),
                    )
                })
            })
    }
    fn navigate(&mut self, path: String, window: &mut Window, cx: &mut Context<Self>) {
        self.path.update(cx, |p, cx| p.set_value(path, window, cx));
        self.error.clear();
        cx.notify();
    }
    fn confirm(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let path = normalize_path(self.machine, &self.path.read(cx).value());
        if self.browsing && self.mode == DialogMode::Create {
            match self.list_folders(&path, cx) {
                Ok(_) => {
                    self.browsing = false;
                    self.error.clear();
                }
                Err(e) => self.error = e.into(),
            }
            cx.notify();
            return;
        }
        let machine = self.machine;
        let name = self.name.read(cx).value().to_string();
        let source = self.source.read(cx).value().to_string();
        let mode = self.mode;
        let start = self.start;
        let result=self.screen.update(cx,|screen,cx|{
            let result=match mode {
                DialogMode::Add=>screen.sample.add_folder(machine,&path),
                DialogMode::Create=>screen.sample.create(machine,&name,&path,start,&source),
                DialogMode::Rename(id)=>screen.sample.rename(id,&name).map(|_|id),
                DialogMode::ChangeFolder(id)=>screen.sample.change_folder(id,&path).map(|_|id),
                DialogMode::Remove(id)=>screen.sample.remove(id).map(|_|id),
            };
            if result.is_ok() {
                screen.notice=match mode {
                    DialogMode::Add=>"Sample folder added to this preview only. No files were read or changed.",
                    DialogMode::Create=>"Sample project added to this preview only. No folder, Git repository or clone was created.",
                    DialogMode::Rename(_)=>"Sample project title updated in memory only.",
                    DialogMode::ChangeFolder(_)=>"Sample project folder changed in memory only. Sessions and files stay as they were.",
                    DialogMode::Remove(_)=>"Sample project removed from the list. Its session association and draft stay; no files were touched.",
                }.into();
                cx.notify();
            }
            result
        });
        match result {
            Ok(Ok(id)) => {
                window.close_dialog(cx);
                if matches!(mode, DialogMode::Add | DialogMode::Create) {
                    _ = self.screen.update(cx, |screen, cx| {
                        screen.registration_completed(id, window, cx)
                    });
                }
            }
            Ok(Err(e)) => {
                self.error = e.into();
                cx.notify();
            }
            Err(_) => {
                self.error = "The sample screen has closed.".into();
                cx.notify();
            }
        }
    }
    fn list_folders(&self, path: &str, cx: &App) -> Result<Vec<&'static str>, &'static str> {
        self.screen
            .upgrade()
            .ok_or("The sample screen has closed.")?
            .read(cx)
            .sample
            .list_folders(self.machine, path)
    }
    fn browser(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let path = normalize_path(self.machine, &self.path.read(cx).value());
        let children = self.list_folders(&path, cx);
        let mut browser = v_flex().gap_2().child(field("Folder path", &self.path, cx));
        match children {
            Ok(children) => {
                let mut paths = vec![parent_path(&path)];
                paths.extend(
                    children
                        .iter()
                        .map(|name| format!("{}/{name}", path.trim_end_matches('/'))),
                );
                let mut items = vec![
                    CommandItem::new()
                        .label(".. · Parent folder")
                        .icon(IconName::ArrowUp),
                ];
                items.extend(
                    children
                        .iter()
                        .map(|name| CommandItem::new().label(*name).icon(IconName::Folder)),
                );
                browser = browser.child(
                    div()
                        .text_role(TextRole::Meta, cx)
                        .text_color(cx.theme().muted_foreground)
                        .child(format!("Sample folders on {}", self.machine.label())),
                );
                if children.is_empty() {
                    browser = browser.child(
                        div()
                            .text_role(TextRole::Meta, cx)
                            .child("This sample folder is empty. You can select it."),
                    );
                }
                let dialog = cx.entity().downgrade();
                browser = browser.child(
                    Command::new(&self.folder_list)
                        .placeholder("Find a sample folder…")
                        .items(items)
                        .on_confirm(move |item, window, cx| {
                            if let Some(path) = paths.get(item.row) {
                                let path = path.clone();
                                _ = dialog.update(cx, |s, cx| s.navigate(path, window, cx));
                            }
                        }),
                );
            }
            Err(SAMPLE_FOLDER_LIMITATION) => {
                browser = browser
                    .child(
                        Label::new(SAMPLE_FOLDER_LIMITATION)
                            .text_color(cx.theme().muted_foreground),
                    )
                    .child(self.return_to_sample_folders(cx));
            }
            Err(message) => {
                browser = browser.child(Label::new(message).text_color(cx.theme().danger));
            }
        }
        browser
    }
    fn return_to_sample_folders(&self, cx: &mut Context<Self>) -> Button {
        Button::new("return-sample-folders")
            .small()
            .label("Return to sample folders")
            .on_click(cx.listener(|s, _, window, cx| {
                s.browsing = true;
                s.navigate(s.machine.home().into(), window, cx);
            }))
    }
    fn create_form(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let mut choices = h_flex().gap_1().flex_wrap();
        for start in StartWith::ALL {
            choices = choices.child(
                Button::new(SharedString::from(format!("start:{start:?}")))
                    .small()
                    .when(self.start == start, |b| b.secondary())
                    .when(self.start != start, |b| b.ghost())
                    .label(start.label())
                    .on_click(cx.listener(move |s, _, _, cx| {
                        s.start = start;
                        s.error.clear();
                        cx.notify();
                    })),
            );
        }
        v_flex()
            .gap_3()
            .child(field("Name", &self.name, cx))
            .child(
                v_flex().gap_1().child(label("Put it in", cx)).child(
                    h_flex()
                        .gap_2()
                        .child(
                            div()
                                .flex_1()
                                .min_w_0()
                                .child(Input::new(&self.path).small()),
                        )
                        .child(
                            Button::new("choose-parent")
                                .small()
                                .label("Choose…")
                                .on_click(cx.listener(|s, _, _, cx| {
                                    s.browsing = true;
                                    s.error.clear();
                                    cx.notify();
                                })),
                        ),
                ),
            )
            .child(label("Start with", cx))
            .child(choices)
            .when(self.start == StartWith::Clone, |form| {
                form.child(field("Repository URL or path", &self.source, cx))
            })
    }
}
impl Render for ProjectDialog {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let body = match self.mode {
            DialogMode::Rename(_) => field("Project title", &self.name, cx).into_any_element(),
            DialogMode::Remove(_) => v_flex()
                .gap_2()
                .child(format!("Remove {}?", self.name.read(cx).value()))
                .child("Its sessions stay and show it as removed. No files are touched.")
                .into_any_element(),
            _ if self.browsing => self.browser(cx).into_any_element(),
            _ => self.create_form(cx).into_any_element(),
        };
        let confirm = if self.browsing && self.mode == DialogMode::Create {
            "Select sample folder"
        } else {
            match self.mode {
                DialogMode::Create => "Create sample project",
                DialogMode::Add => "Add sample folder",
                DialogMode::Rename(_) => "Save sample title",
                DialogMode::ChangeFolder(_) => "Change sample folder",
                DialogMode::Remove(_) => "Remove sample project",
            }
        };
        v_flex().gap_3()
            .child(div().text_role(TextRole::Meta, cx).text_color(cx.theme().muted_foreground)
                .child("Sample preview · Changes stay in memory. No real folders or repositories are created."))
            .when(matches!(self.mode, DialogMode::Create | DialogMode::Add), |form| {
                form.child(label("Machine", cx)).child(self.machine_menu(cx))
            })
            .when(matches!(self.mode, DialogMode::ChangeFolder(_)), |form| {
                form.child(label("Machine", cx)).child(self.machine.label())
            })
            .child(body)
            .when(self.error == SAMPLE_FOLDER_LIMITATION && !self.browsing, |form| {
                form.child(Label::new(SAMPLE_FOLDER_LIMITATION).text_color(cx.theme().muted_foreground))
                    .child(self.return_to_sample_folders(cx))
            })
            .when(!self.error.is_empty() && self.error != SAMPLE_FOLDER_LIMITATION, |form| {
                form.child(div().text_role(TextRole::Meta, cx).text_color(cx.theme().danger).child(self.error.clone()))
            })
            .child(h_flex().justify_end().gap_2()
                .when(self.browsing && self.mode == DialogMode::Create, |row| {
                    row.child(Button::new("folder-back").ghost().label("Back")
                        .on_click(cx.listener(|s, _, _, cx| { s.browsing = false; cx.notify(); })))
                })
                .child(Button::new("cancel-project").ghost().label("Cancel")
                    .on_click(|_, window, cx| window.close_dialog(cx)))
                .child(Button::new("confirm-project").primary().label(confirm)
                    .on_click(cx.listener(|s, _, window, cx| s.confirm(window, cx)))))
    }
}

fn label(text: &'static str, cx: &App) -> Div {
    div()
        .text_role(TextRole::Meta, cx)
        .text_color(cx.theme().muted_foreground)
        .child(text)
}
fn field(text: &'static str, input: &Entity<InputState>, cx: &App) -> Div {
    v_flex()
        .gap_1()
        .child(label(text, cx))
        .child(Input::new(input).small())
}
