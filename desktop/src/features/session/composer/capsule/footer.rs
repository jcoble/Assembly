//! The controls under the draft: the add button on the left; on the right the new session's agent picker, the context
//! gauge, the settings pills, the mic that isn't available yet and one Send or Stop.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use std::rc::Rc;

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::Composer;
use crate::features::session::composer::config_pills::ConfigPills;
use crate::features::session::composer::config_pills::OnSelect;
use crate::features::session::composer::context_gauge::ContextGauge;
use crate::features::session::composer::shape::{Slot, slot};

/// Send and Stop.
const SEND_SIZE: Pixels = px(32.);

impl Composer {
    /// The add button carries a menu, so it turns into a close cross while that menu is on screen. Pulled left so its
    /// glyph lines up under the message text.
    pub(super) fn add_button(&self, cx: &mut Context<Self>) -> AnyElement {
        let opener = cx.weak_entity();
        let watcher = cx.weak_entity();
        let open = self.add_open;
        div()
            .flex_none()
            .ml(px(-8.))
            .child(
                Button::new("composer:attach")
                    .ghost()
                    .small()
                    .disabled(self.start_busy())
                    .icon(if open { Glyph::X } else { Glyph::Plus })
                    .accessibility_label(if open {
                        "Close the add menu"
                    } else {
                        "Add to this message"
                    })
                    .dropdown_menu_with_anchor(Anchor::BottomLeft, move |menu, _, _| {
                        let opener = opener.clone();
                        menu.max_w(px(320.)).item(
                            PopupMenuItem::element(|_, cx| {
                                let muted = cx.theme().muted_foreground;
                                v_flex().gap(px(2.)).child("Attach images").child(
                                    div()
                                        .text_role(TextRole::Meta, cx)
                                        .text_color(muted)
                                        .child("Or paste and drop them in"),
                                )
                            })
                            .icon(Icon::new(Glyph::ImageUp))
                            .on_click(move |_, window, cx| {
                                _ = opener
                                    .update(cx, |composer, cx| composer.pick_images(window, cx));
                            }),
                        )
                    })
                    .on_open_change(move |open, _, cx| {
                        _ = watcher.update(cx, |composer, cx| {
                            composer.add_open = *open;
                            cx.notify();
                        });
                    }),
            )
            .into_any_element()
    }

    /// The agent's settings: the new session's own while it has them, else this session's.
    fn settings(
        &self,
        compact: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Option<AnyElement> {
        if let Some(slots) = self.start_slots.clone() {
            return Some((slots.settings)(compact, window, cx));
        }
        if self.start_draft.is_some() {
            return None;
        }
        let options: Vec<_> = self
            .menus()
            .into_iter()
            .map(|(option, current)| (option.clone(), current.to_string()))
            .collect();
        if options.is_empty() {
            // Nothing to say while the first read or the warm is still going.
            let settled =
                self.config.is_some() && self._warm.as_ref().is_none_or(|warm| warm.is_ready());
            return settled.then(|| {
                div()
                    .flex_none()
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().muted_foreground)
                    .child("Agent settings unavailable")
                    .into_any_element()
            });
        }
        let composer = cx.weak_entity();
        let session = self.session.clone();
        let generation = self.target_generation;
        let on_select: OnSelect = Rc::new(move |id, value, _, cx| {
            _ = composer.update(cx, |composer, cx| {
                if composer.target_generation == generation && composer.session == session {
                    composer.choose(id.into(), value.into(), cx)
                }
            });
        });
        let offline = self.offline.is_some() || !self.operational();
        Some(
            ConfigPills {
                provider: self
                    .agent_name
                    .clone()
                    .unwrap_or_else(|| "the agent".into()),
                options,
                on_select,
                disabled: offline || !self.backend_connected || self.advertised_target.is_none(),
                saving: self.picking > 0,
                compact,
            }
            .into_any_element(),
        )
    }

    /// One slot: Stop while a turn runs with nothing that can join it, else Send.
    fn send_or_stop(&self, content: bool, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let pill = theme.radius_full();
        let offline = self.offline.is_some() || !self.operational();
        let empty = !content;
        let running = self.running.is_some();
        match slot(running, content, self.can_steer) {
            Slot::Stop => Button::new("composer:stop")
                .danger()
                .rounded(pill)
                .with_size(SEND_SIZE)
                .disabled(offline)
                .tooltip("Stop generation")
                .accessibility_label("Stop generation")
                .child(
                    div()
                        .size(px(11.))
                        .rounded(px(2.))
                        .bg(theme.danger_foreground),
                )
                .on_click(cx.listener(|composer, _, _, cx| composer.stop(cx)))
                .into_any_element(),
            Slot::Send { steer } => {
                let waiting = !self.all_saved();
                let disabled = if self.start_draft.is_some() {
                    empty || !self.start_ready()
                } else {
                    empty || waiting || offline
                };
                let words = if steer {
                    "Steer current turn"
                } else {
                    "Send message"
                };
                Button::new("composer:send")
                    .primary()
                    .icon(Glyph::ArrowUp)
                    .rounded(pill)
                    .with_size(SEND_SIZE)
                    .disabled(disabled)
                    .tooltip(words)
                    .accessibility_label(words)
                    .on_click(cx.listener(|composer, _, window, cx| composer.send(window, cx)))
                    .into_any_element()
            }
        }
    }

    /// The right side of the controls.
    pub(super) fn trail(
        &self,
        compact: bool,
        content: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Div {
        let footer = self
            .start_slots
            .clone()
            .map(|slots| (slots.footer)(window, cx));
        let settings = self.settings(compact, window, cx);
        h_flex()
            .flex_none()
            .min_w_0()
            .gap_1()
            .children(footer)
            .when_some(self.meter.clone(), |trail, meter| {
                let provider = self
                    .agent_name
                    .clone()
                    .unwrap_or_else(|| "the agent".into());
                trail.child(ContextGauge { meter, provider })
            })
            .children(settings)
            .child(
                Button::new("composer:mic")
                    .ghost()
                    .small()
                    .icon(Glyph::Mic)
                    .disabled(true)
                    .tooltip("Voice input is not available yet")
                    .accessibility_label("Voice input, not available yet"),
            )
            .child(self.send_or_stop(content, cx))
    }
}
