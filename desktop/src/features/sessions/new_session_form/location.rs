use super::NewSessionForm;
use super::start_source::StartSource;
use crate::features::projects::{new_project::FolderInspection, projects::ProjectRecord};
use gpui_kit::*;
use serde_json::json;
use std::rc::Rc;
impl NewSessionForm {
    pub(super) fn reload_location(&mut self, cx: &mut Context<Self>) {
        if self.attempt.is_some() || !self.backend_connected {
            return;
        }
        let project = self.selected_project.clone();
        let window = self.draft_window;
        let form = cx.weak_entity();
        cx.defer(move |cx| {
            let _ = window.update(cx, |_, window, cx| {
                let _ = form.update(cx, |form, cx| form.select_project(project, window, cx));
            });
        });
    }
    pub(super) fn select_project(
        &mut self,
        project: Option<ProjectRecord>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.attempt.is_some() {
            return;
        }
        self.location_read = None;
        self.location_generation += 1;
        let generation = self.location_generation;
        self.location_source = None;
        self.inspection = None;
        self.cwd = None;
        self.config = None;
        let machine = project
            .as_ref()
            .map(|p| p.machine.clone())
            .unwrap_or_else(|| self.machine.clone().unwrap_or_else(|| "local".into()));
        let route = if machine == "local" {
            None
        } else {
            Some(machine.clone())
        };
        let changed = self.machine != route;
        self.machine = route.clone();
        self.selected_project = project.clone();
        self.route = self.machine_route(cx);
        if changed {
            self.agent = None;
            self.agents.clear();
            self.load_agents(cx);
        }
        self.sync_start_state(cx);
        let resolved = StartSource::resolve(route.clone(), cx);
        let root_args = if let Some(project) = &project {
            json!({"projectId":project.id})
        } else {
            json!({"machine":machine,"path":"~"})
        };
        let method = if project.is_some() {
            "inspect"
        } else {
            "folders"
        };
        self.location_read = Some(cx.spawn(async move |form, cx| {
            let source = match resolved.await {
                Ok(source) => Rc::new(source),
                Err(error) => {
                    let _ = form.update(cx, |form, cx| form.location_error(generation, &error, cx));
                    return;
                }
            };
            let call = form
                .update(cx, |form, cx| {
                    if form.location_generation != generation || !source.current(cx) {
                        return None;
                    }
                    Some(form.client.call("projects", method, root_args, None, cx))
                })
                .ok()
                .flatten();
            let Some(call) = call else {
                return;
            };
            let answer = call.await;
            // The saved route must still name the same actual source after its owner read.
            let verify = cx.update(|cx| StartSource::resolve(route, cx));
            let verified = verify.await;
            let _ = form.update(cx, |form, cx| {
                if form.location_generation != generation {
                    return;
                }
                if !source.current(cx)
                    || !verified
                        .as_ref()
                        .is_ok_and(|current| current.source == source.source)
                {
                    form.location_error(generation, "The project's current source changed.", cx);
                    return;
                }
                let value = match answer {
                    Ok(value) => value,
                    Err(error) => {
                        form.location_error(generation, &error.reason(), cx);
                        return;
                    }
                };
                let (cwd, inspection) = if project.is_some() {
                    match serde_json::from_value::<FolderInspection>(value) {
                        Ok(inspection) => (inspection.root_path.clone(), Some(inspection)),
                        Err(error) => {
                            form.location_error(generation, &error.to_string(), cx);
                            return;
                        }
                    }
                } else {
                    match value["path"].as_str().filter(|path| !path.is_empty()) {
                        Some(path) => (path.to_string(), None),
                        None => {
                            form.location_error(
                                generation,
                                "The owning machine did not return its home folder.",
                                cx,
                            );
                            return;
                        }
                    }
                };
                form.cwd = Some(cwd);
                form.inspection = inspection;
                form.location_source = Some(source);
                form.location_read = None;
                form.discover(cx);
                cx.notify();
            });
        }));
        self.start_composer
            .update(cx, |composer, cx| composer.focus_input(window, cx));
        cx.notify();
    }
    fn location_error(&mut self, generation: u64, error: &str, cx: &mut Context<Self>) {
        if self.location_generation != generation {
            return;
        }
        self.location_read = None;
        self.location_source = None;
        self.inspection = None;
        self.cwd = None;
        self.config = None;
        self.error = error.to_string().into();
        self.sync_start_state(cx);
        cx.notify();
    }
    pub(super) fn sync_start_state(&mut self, cx: &mut Context<Self>) {
        if self.attempt.is_some() {
            return;
        }
        let ready = self.backend_connected
            && !self.loading
            && !self.discovering
            && self.agent.is_some()
            && self.selected_target().is_some()
            && self.cwd.is_some()
            && self.config.as_ref().is_some_and(|config| config.ready())
            && self
                .location_source
                .as_ref()
                .is_some_and(|source| source.current(cx));
        self.start_composer.update(cx, |composer, cx| {
            composer.set_start_state(ready, false, None, cx)
        });
    }
}
