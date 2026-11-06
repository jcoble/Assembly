use super::{Created, NewSessionForm};
use crate::core::intents::open_project::OpenProject;
use crate::core::state::machines::Machines;
use crate::features::projects::projects::{ProjectRecord, Projects};
use gpui_kit::component::WindowExt as _;
use gpui_kit::*;

impl NewSessionForm {
    pub fn register_projects(cx: &mut App) {
        cx.on_action(|intent: &OpenProject, cx| {
            let intent = intent.clone();
            if let Some(window) = cx
                .active_window()
                .or_else(|| cx.windows().into_iter().next())
            {
                cx.defer(move |cx| {
                    _ = window.update(cx, |_, window, cx| Self::open_project(&intent, window, cx));
                });
            }
        });
    }
    fn open_project(intent: &OpenProject, window: &mut Window, cx: &mut App) {
        let project = Projects::get(cx).and_then(|projects| projects.read(cx).resolve_open(intent));
        let project = project.filter(|project| {
            project.machine == "local"
                || Machines::try_global(cx).is_some_and(|machines| {
                    machines.read(cx).machines().any(|machine| {
                        machine.name.as_deref() == Some(project.machine.as_str())
                            && !machine.didnt_answer()
                    })
                })
        });
        let Some(project) = project else {
            window.open_dialog(cx, |dialog, _, _| {
                dialog.title("Project unavailable").child(
                    "This project or its machine changed. Reopen Projects and choose it again.",
                )
            });
            return;
        };
        let title = format!("New session · {}", project.title);
        let form = cx.new(|cx| Self::new(window, cx));
        form.update(cx, |form, cx| form.fill_project(project, window, cx));
        let handle = window.window_handle();
        let made = cx.subscribe(&form, move |_, _: &Created, cx| {
            _ = handle.update(cx, |_, window, cx| window.close_dialog(cx));
        });
        form.update(cx, |form, _| form._project_created = Some(made));
        let dialog_form = form.clone();
        window.open_dialog(cx, move |dialog, _, _| {
            dialog
                .title(title.clone())
                .footer(Empty)
                .child(dialog_form.clone())
        });
        form.update(cx, |form, cx| {
            form.start_composer
                .update(cx, |composer, cx| composer.focus_input(window, cx))
        });
    }
    /// Selects an actual registry record as an unsent draft. It never invokes sessions/create.
    pub fn fill_project(
        &mut self,
        project: ProjectRecord,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.attempt.is_some() {
            return;
        }
        self.project_chooser
            .update(cx, |chooser, cx| chooser.set_opened(project.clone(), cx));
        self.select_project(Some(project), window, cx);
    }
}
