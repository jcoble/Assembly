//! `NewSessionForm` starts a session: the agent and machine (`machines/list`, `machines/agents`) and the folder, then
//! `sessions/create`. The new session is selected and opens empty, ready for its first message. Replaces the new
//! session form in routes/+page.svelte.

use gpui_kit::*;

pub struct NewSessionForm {
    machine: Option<String>,
    agent: Option<String>,
    folder: Option<String>,
    _tasks: Vec<Task<()>>,
}

impl NewSessionForm {
    pub fn new(_cx: &mut Context<Self>) -> Self {
        todo!()
    }

    fn create(&mut self, _cx: &mut Context<Self>) {
        todo!()
    }
}

impl Render for NewSessionForm {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
