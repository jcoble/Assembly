//! `PrPane`'s panel traits (name, focus, saved layout) and its render.
use super::*;

impl EventEmitter<PanelEvent> for PrPane {}
impl Focusable for PrPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}
impl BasePanel for PrPane {
    fn panel_name(&self) -> &'static str {
        NAME
    }
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        self.active = active;
        self._pictures = active.then(|| PictureInterest::hold(cx)).flatten();
        self.sync_scope(cx);
        if let Some(detail) = &self.detail {
            detail.update(cx, |detail, cx| detail.set_active(active, cx));
        }
    }
    fn on_removed(&mut self, _: &mut Window, cx: &mut Context<Self>) {
        self.active = false;
        self._pictures = None;
        self.queue
            .update(cx, |queue, cx| queue.set_active(false, cx));
        if let Some(detail) = &self.detail {
            detail.update(cx, |detail, cx| detail.set_active(false, cx));
        }
    }
    fn dump(&self, _: &App) -> PanelState {
        PanelState {
            panel_name: NAME.into(),
            children: Vec::new(),
            info: PanelInfo::panel(json!({"session":self.session})),
        }
    }
}
impl Panel for PrPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        NAME
    }
}
impl Render for PrPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let page = match (self.detail.clone(), self.selected.clone()) {
            (Some(detail), _) => detail.into_any_element(),
            (None, Some(selected)) => self.opening_page(&selected, cx),
            (None, None) => self.list_page(cx).into_any_element(),
        };
        div()
            .track_focus(&self.focus)
            .size_full()
            .min_h_0()
            .child(page)
    }
}
