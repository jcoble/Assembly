use super::projects::{ProjectRecord, Projects};
use crate::core::state::machines::Machines;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::{Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

#[derive(Clone, Debug, PartialEq)]
pub enum ProjectChoice {
    Project(String),
    NoProject,
}
#[derive(Clone, Debug)]
pub enum ProjectChooserEvent {
    Picked {
        choice: ProjectChoice,
        project: Option<ProjectRecord>,
    },
    Cancelled,
}
pub struct ProjectChooser {
    projects: Option<Entity<Projects>>,
    opened_id: Option<String>,
    selected_session_project: Option<String>,
    choice: ProjectChoice,
    selected: Option<ProjectRecord>,
    explicit: bool,
    locked: bool,
    filter: Entity<InputState>,
    query: String,
    rows: Vec<ProjectRecord>,
    loading: bool,
    failure: Option<String>,
    highlighted: usize,
    generation: u64,
    registry_revision: Option<i64>,
    read: Option<Task<()>>,
    _filter: Subscription,
    _registry: Option<Subscription>,
    _machines: Option<Subscription>,
}
impl EventEmitter<ProjectChooserEvent> for ProjectChooser {}
impl ProjectChooser {
    pub fn new(opened: Option<ProjectRecord>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let filter = cx.new(|cx| InputState::new(window, cx).placeholder("Search projects…"));
        let changed = cx.subscribe(&filter, |chooser: &mut Self, _, event, cx| {
            if matches!(event, InputEvent::Change) {
                chooser.search(chooser.filter.read(cx).value().to_string(), cx);
            }
        });
        let projects = Projects::get(cx);
        let revision = projects
            .as_ref()
            .and_then(|projects| projects.read(cx).revision());
        let registry = projects.as_ref().map(|projects| {
            cx.observe(projects, |chooser: &mut Self, projects, cx| {
                let revision = projects.read(cx).revision();
                if revision != chooser.registry_revision {
                    chooser.registry_revision = revision;
                    chooser.search(chooser.query.clone(), cx);
                }
            })
        });
        let machines = Machines::try_global(cx)
            .map(|machines| cx.observe(&machines, |_: &mut Self, _, cx| cx.notify()));
        let selected_key = crate::core::state::selected_session::SelectedSession::try_global(cx)
            .and_then(|selected| selected.read(cx).key().cloned());
        let selected_session_project = selected_key.and_then(|key| {
            let feed = crate::core::engine::EngineFeed::global(cx);
            feed.read(cx)
                .item(&crate::core::engine::EngineKey {
                    machine: key.machine,
                    kind: "session".into(),
                    id: key.id,
                })
                .and_then(|item| item.value["projectId"].as_str().map(String::from))
        });
        let mut chooser = Self {
            projects,
            opened_id: opened.as_ref().map(|p| p.id.clone()),
            selected_session_project,
            choice: opened.as_ref().map_or(ProjectChoice::NoProject, |p| {
                ProjectChoice::Project(p.id.clone())
            }),
            explicit: opened.is_some(),
            locked: false,
            selected: opened,
            filter,
            query: String::new(),
            rows: Vec::new(),
            loading: false,
            failure: None,
            highlighted: 0,
            generation: 0,
            registry_revision: revision,
            read: None,
            _filter: changed,
            _registry: registry,
            _machines: machines,
        };
        chooser.search(String::new(), cx);
        chooser
    }
    pub fn set_opened(&mut self, project: ProjectRecord, cx: &mut Context<Self>) {
        if self.locked {
            return;
        }
        self.opened_id = Some(project.id.clone());
        self.choice = ProjectChoice::Project(project.id.clone());
        self.selected = Some(project);
        self.explicit = true;
        cx.notify();
    }
    pub fn set_locked(&mut self, locked: bool, cx: &mut Context<Self>) {
        self.locked = locked;
        if locked {
            self.explicit = true;
        }
        cx.notify();
    }
    pub fn choice(&self) -> &ProjectChoice {
        &self.choice
    }
    pub fn selected(&self) -> Option<&ProjectRecord> {
        self.selected.as_ref()
    }
    pub fn focus_search(&self, window: &mut Window, cx: &mut Context<Self>) {
        self.filter.update(cx, |input, cx| input.focus(window, cx));
    }
    fn search(&mut self, filter: String, cx: &mut Context<Self>) {
        self.read = None;
        self.generation += 1;
        let generation = self.generation;
        self.query = filter;
        self.failure = None;
        self.loading = true;
        self.highlighted = 0;
        let Some(projects) = self.projects.clone() else {
            self.loading = false;
            self.failure = Some("The backend isn't running.".into());
            cx.notify();
            return;
        };
        let call = projects.update(cx, |projects, cx| projects.search(&self.query, cx));
        self.read = Some(cx.spawn(async move |chooser, cx| {
            let answer = call.await;
            _ = chooser.update(cx, |chooser, cx| {
                if chooser.generation != generation {
                    return;
                }
                chooser.loading = false;
                chooser.read = None;
                match answer {
                    Ok(rows) => {
                        chooser.rows = rows
                            .into_iter()
                            .filter(|project| project.removed_at.is_none())
                            .collect();
                        if !chooser.locked && !chooser.explicit && chooser.query.trim().is_empty() {
                            let visible: Vec<_> = chooser
                                .rows
                                .iter()
                                .filter(|project| visible(project, cx))
                                .cloned()
                                .collect();
                            chooser.selected = default_project(
                                chooser.opened_id.as_deref(),
                                chooser.selected_session_project.as_deref(),
                                &visible,
                            )
                            .cloned();
                            chooser.choice = chooser
                                .selected
                                .as_ref()
                                .map_or(ProjectChoice::NoProject, |p| {
                                    ProjectChoice::Project(p.id.clone())
                                });
                            cx.emit(ProjectChooserEvent::Picked {
                                choice: chooser.choice.clone(),
                                project: chooser.selected.clone(),
                            });
                        }
                    }
                    Err(error) => chooser.failure = Some(error),
                }
                cx.notify();
            });
        }));
        cx.notify();
    }
    fn pick(&mut self, project: Option<ProjectRecord>, cx: &mut Context<Self>) {
        if self.locked
            || self.loading
            || project
                .as_ref()
                .is_some_and(|p| !visible(p, cx) || offline(p, cx))
        {
            return;
        }
        self.choice = project.as_ref().map_or(ProjectChoice::NoProject, |p| {
            ProjectChoice::Project(p.id.clone())
        });
        self.explicit = true;
        self.selected = project.clone();
        cx.emit(ProjectChooserEvent::Picked {
            choice: self.choice.clone(),
            project,
        });
        cx.notify();
    }
    fn key_down(&mut self, event: &KeyDownEvent, _: &mut Window, cx: &mut Context<Self>) {
        if self.locked {
            return;
        }
        let rows: Vec<_> = self
            .rows
            .iter()
            .filter(|p| visible(p, cx))
            .cloned()
            .collect();
        match event.keystroke.key.as_str() {
            "down" => {
                self.highlighted = (self.highlighted + 1).min(rows.len());
                cx.stop_propagation();
                cx.notify();
            }
            "up" => {
                self.highlighted = self.highlighted.saturating_sub(1);
                cx.stop_propagation();
                cx.notify();
            }
            "enter" => {
                self.pick(
                    self.highlighted
                        .checked_sub(1)
                        .and_then(|index| rows.get(index))
                        .cloned(),
                    cx,
                );
                cx.stop_propagation();
            }
            "escape" => {
                cx.emit(ProjectChooserEvent::Cancelled);
                cx.stop_propagation();
            }
            _ => {}
        }
    }
}
fn visible(project: &ProjectRecord, cx: &App) -> bool {
    project.machine == "local"
        || Machines::try_global(cx).is_some_and(|machines| {
            machines
                .read(cx)
                .machines()
                .any(|machine| machine.name.as_deref() == Some(project.machine.as_str()))
        })
}
fn offline(project: &ProjectRecord, cx: &App) -> bool {
    project.machine != "local" && Machines::offline(Some(&project.machine), cx)
}
fn default_project<'a>(
    opened: Option<&str>,
    selected: Option<&str>,
    rows: &'a [ProjectRecord],
) -> Option<&'a ProjectRecord> {
    opened
        .and_then(|id| rows.iter().find(|p| p.id == id))
        .or_else(|| selected.and_then(|id| rows.iter().find(|p| p.id == id)))
        .or_else(|| rows.iter().max_by_key(|p| p.last_used_at))
}
impl Render for ProjectChooser {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let view = v_flex()
            .gap_1()
            .capture_key_down(cx.listener(Self::key_down))
            .child(Input::new(&self.filter))
            .child(
                Button::new("project-choice-none")
                    .ghost()
                    .label("No project")
                    .disabled(self.loading || self.locked)
                    .on_click(cx.listener(|chooser, _, _, cx| chooser.pick(None, cx))),
            );
        if let Some(error) = &self.failure {
            return view.child(error.clone()).child(
                Button::new("project-choice-retry").label("Retry").on_click(
                    cx.listener(|chooser, _, _, cx| chooser.search(chooser.query.clone(), cx)),
                ),
            );
        }
        if self.loading {
            return view.child("Loading projects…");
        }
        let rows: Vec<_> = self
            .rows
            .iter()
            .filter(|p| visible(p, cx))
            .cloned()
            .collect();
        if rows.is_empty() {
            return view.child(if self.query.trim().is_empty() {
                "No projects yet. Add a project.".to_string()
            } else {
                format!("Nothing matches “{}”.", self.query)
            });
        }
        let mut choices = v_flex()
            .id("project-choice-rows")
            .max_h(px(240.))
            .overflow_y_scroll();
        for (index, project) in rows.into_iter().enumerate() {
            let unavailable = offline(&project, cx);
            let machine = if project.machine == "local" {
                "This computer".to_string()
            } else {
                project.machine.clone()
            };
            let label = format!(
                "{} · {}{}",
                project.title,
                machine,
                if unavailable { " · Offline" } else { "" }
            );
            choices = choices.child(
                h_flex().gap_1().child(
                    Button::new(SharedString::from(format!("project-choice:{}", project.id)))
                        .label(label)
                        .tooltip(project.root_path.clone())
                        .disabled(unavailable || self.locked)
                        .when(self.highlighted == index + 1, |button| button.primary())
                        .on_click(cx.listener(move |chooser, _, _, cx| {
                            chooser.pick(Some(project.clone()), cx)
                        })),
                ),
            );
        }
        view.child(choices)
    }
}
#[cfg(test)]
#[path = "project_chooser_tests.rs"]
mod tests;
