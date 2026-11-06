//! Building a `NewSessionForm` around a session client: its chooser, start composer and the starting subscriptions.
use super::*;

impl NewSessionForm {
    pub(super) fn with_client(
        client: Rc<dyn SessionClient>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let project_chooser = cx.new(|cx| {
            crate::features::projects::project_chooser::ProjectChooser::new(None, window, cx)
        });
        let start_composer = cx.new(|cx| {
            crate::features::session::composer::composer_view::Composer::new_start_draft(
                client.clone(),
                window,
                cx,
            )
        });
        let slots = start_slots::start_slots(cx.weak_entity());
        start_composer.update(cx, |composer, cx| composer.set_start_slots(slots, cx));
        let project_choice = cx.subscribe_in(
            &project_chooser,
            window,
            |form: &mut Self, _, event, window, cx| match event {
                crate::features::projects::project_chooser::ProjectChooserEvent::Picked {
                    project,
                    ..
                } => {
                    form.chooser_open = false;
                    form.select_project(project.clone(), window, cx);
                }
                crate::features::projects::project_chooser::ProjectChooserEvent::Cancelled => {
                    form.chooser_open = false;
                    window.focus(&form.chooser_focus, cx);
                    cx.notify();
                }
            },
        );
        let start_requested = cx.subscribe_in(&start_composer, window, |form: &mut Self, _, event, window, cx| {
            if let crate::features::session::composer::composer_view::ComposerEvent::StartRequested { text, images } = event {
                form.first_send(text.clone(), images.clone(), window, cx);
            }
        });
        // An agents load the connection dropped is made again once the machines list changes (after a reconnect).
        let machines = Machines::try_global(cx).map(|machines| {
            cx.observe(&machines, |form: &mut Self, _, cx| {
                if form.attempt.is_some() {
                    cx.notify();
                    return;
                }
                let route = form.machine_route(cx);
                if form.route != route {
                    form.route = route;
                    form.agents.clear();
                    form.auth_machine = None;
                    form._auth_machine = None;
                    form.location_source = None;
                    form.inspection = None;
                    form.cwd = None;
                    form.reload_location(cx);
                    form.discover(cx);
                    form.load_agents(cx);
                } else if form.retry_agents && !form.loading {
                    form.load_agents(cx);
                }
                cx.notify();
            })
        });
        let backend_connected = crate::core::socket::client::SocketClient::try_global(cx)
            .map(|socket| {
                matches!(
                    socket.read(cx).state(),
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                )
            })
            .unwrap_or(false);
        let backend_generation = crate::core::socket::client::SocketClient::try_global(cx)
            .and_then(|socket| socket.read(cx).connection_generation())
            .filter(|_| backend_connected);
        let backend_connected = backend_connected && backend_generation.is_some();
        let connection = crate::core::socket::client::SocketClient::try_global(cx).map(|socket| {
            cx.observe(&socket, |form: &mut Self, socket, cx| {
                let socket = socket.read(cx);
                let connected = matches!(
                    socket.state(),
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                );
                let generation = socket.connection_generation();
                form.observe_connection(connected, generation, cx);
            })
        });
        let bulk_connection =
            crate::core::socket::client::SocketClient::try_bulk(cx).map(|socket| {
                cx.observe(&socket, |form: &mut Self, _, cx| {
                    if form.attempt.is_some() {
                        return;
                    }
                    form.location_read = None;
                    form.location_source = None;
                    form.inspection = None;
                    form.cwd = None;
                    form.sync_start_state(cx);
                    form.reload_location(cx);
                })
            });
        let preferences =
            crate::core::state::preferences::Preferences::try_global(cx).map(|preferences| {
                cx.observe(&preferences, |form: &mut Self, _, cx| {
                    if form.attempt.is_some() || form.starting || form.picks != 0 {
                        return;
                    }
                    let remembered = form
                        .agent
                        .as_deref()
                        .and_then(|agent| draft_config::remembered(agent, cx));
                    if let Some(config) = form.config.as_mut()
                        && config.apply_remembered(remembered.as_ref()).is_ok()
                    {
                        cx.notify();
                    }
                })
            });
        let mut form = NewSessionForm {
            centered: false,
            chooser_open: false,
            chooser_focus: cx.focus_handle(),
            project_chooser,
            start_composer,
            _project_choice: project_choice,
            _start_requested: start_requested,
            location_source: None,
            inspection: None,
            location_read: None,
            location_generation: 0,
            attempt: None,
            selected_project: None,
            _project_created: None,
            machine: None,
            agent: None,
            agents: Vec::new(),
            auth_machine: None,
            auth_error: Default::default(),
            _auth_machine: None,
            _sign_in: None,
            client,
            config: None,
            generation: 0,
            picks: 0,
            list_generation: 0,
            route: String::new(),
            backend_connected,
            backend_generation,
            _connection: connection,
            _bulk_connection: bulk_connection,
            _preferences: preferences,
            discovering: false,
            _discover: None,
            cwd: None,
            draft_window: window.window_handle(),
            error: SharedString::default(),
            loading: false,
            starting: false,
            retry_agents: false,
            _load: None,
            _create: None,
            _machines: machines,
        };
        form.load_agents(cx);
        form.reload_location(cx);
        form
    }
}
