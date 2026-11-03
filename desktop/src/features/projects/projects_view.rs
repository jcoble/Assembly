use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::projects::Projects;
use crate::core::intents::open_new_project::OpenNewProject;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

struct ProjectsView {
    projects: Option<Entity<Projects>>,
    _heard: Option<Subscription>,
}
impl ProjectsView {
    fn new(cx: &mut Context<Self>) -> Self {
        let projects = Projects::get(cx);
        let heard = projects.as_ref().map(|projects| cx.observe(projects, |_, _, cx| cx.notify()));
        Self { projects, _heard: heard }
    }
}

/// Opens the actual pushed project registry. Samples remain confined to the preview.
pub fn open(window: &mut Window, cx: &mut App) {
    let view = cx.new(ProjectsView::new);
    window.open_dialog(cx, move |dialog, _, _| dialog.title("Projects").footer(Empty).child(view.clone()));
}
impl Render for ProjectsView {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let mut content = v_flex().gap_2().child(
            h_flex().gap_2().child(Button::new("projects-new").primary().label("New project").on_click(|_, window, cx| {
                window.close_dialog(cx);
                window.dispatch_action(Box::new(OpenNewProject { machine: None }), cx);
            })),
        );
        let Some(projects) = self.projects.clone() else {
            return content.child("The backend isn't running.");
        };
        let (rows, more, loading, failure, empty) = {
            let data = projects.read(cx);
            let mut rows = v_flex().gap_2().id("projects-registry").max_h(px(480.)).overflow_y_scroll();
            for project in data.rows() {
                let machine = if project.machine == "local" { "This computer" } else { &project.machine };
                rows = rows.child(
                    v_flex().gap_1().p_2()
                        .child(project.title.clone())
                        .child(div().text_role(TextRole::Body, cx).child(project.root_path.clone()))
                        .child(div().text_role(TextRole::Body, cx).child(machine.to_string()))
                        .children(project.repo_key.clone().map(|repo| div().text_role(TextRole::Body, cx).child(repo))),
                );
            }
            (rows, data.next.is_some(), data.loading, data.failure.clone(), data.rows().is_empty())
        };
        content = content.child(rows);
        if let Some(error) = failure { content = content.child(error); }
        else if empty && !loading { content = content.child("No projects yet. Add one with New project."); }
        else if loading { content = content.child("Loading projects…"); }
        content.when(more, |view| view.child(
            Button::new("projects-more").ghost().label("Load more").disabled(loading)
                .on_click(cx.listener(|view, _, _, cx| {
                    if let Some(projects) = &view.projects { projects.update(cx, |projects, cx| projects.load_more(cx)); }
                })),
        ))
    }
}
