//! How the composer draws. One raised box with 12 px corners and no outline at rest, its edge lit by focus, a drag or a
//! pending answer. At rest it's one slim line when the pane is wide enough: add, the draft, the controls. Content, a
//! turn's first five seconds, a pending answer or a drag relaxes it into the draft on its own rows over the controls
//! (capsule/shape.rs). Under the box sit the new session's sub-bar or the branch, and the hint line. Why something failed
//! sits above the box, and the "/" list floats just above it.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::Textarea;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use std::rc::Rc;

use super::Composer;
use crate::features::session::composer::attachment_card::AttachmentCard;
use crate::features::session::composer::shape::{Facts, hint, look};
use crate::features::session::composer::slash_list::SlashList;

/// The box is 820 wide at most, with 22 on either side of it in a narrower pane.
const COLUMN: Pixels = px(864.);
const GUTTER: Pixels = px(22.);
/// Three lines of room once the box is open, so opening is visible.
const OPEN_DRAFT: Pixels = px(63.);

impl Render for Composer {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let focused = self.draft.read(cx).focus_handle(cx).is_focused(window);
        let provider = match &self.start_slots {
            Some(slots) => (slots.provider)(cx),
            None => self.agent_name.clone(),
        };
        let words = SharedString::from(hint(provider.as_deref(), self.locked));
        if self.hint != words {
            self.hint = words.clone();
            self.draft
                .update(cx, |draft, cx| draft.set_placeholder(words, window, cx));
        }
        if self.dragging && !cx.has_active_drag() {
            self.dragging = false;
        }
        let content =
            !self.draft.read(cx).value().trim().is_empty() || !self.attachments.is_empty();
        self.has_opened |= content;
        let locked = self.locked.is_some();
        let shape = look(Facts {
            content,
            sending: self.running.is_some() || self.start_busy(),
            settled: self.turn_settled,
            locked,
            dragging: self.dragging,
            opened: self.has_opened,
            width: self.width,
        });
        let commands: Vec<_> = (self.slash_list(cx).into_iter())
            .map(|command| {
                let slash = if command.name.starts_with('$') {
                    ""
                } else {
                    "/"
                };
                let name = format!("{slash}{}", command.name);
                (name.into(), command.description.clone().into())
            })
            .collect();
        let composer = cx.weak_entity();
        let slash = (!commands.is_empty() && !locked).then(|| SlashList {
            commands,
            selected: self.slash_lit(cx),
            scroll: self.slash_scroll.clone(),
            on_pick: Rc::new(move |at, window, cx| {
                _ = composer.update(cx, |composer, cx| composer.pick_command(at, window, cx));
            }),
        });
        let this = cx.weak_entity();
        let cards: Vec<_> = self
            .attachments
            .iter()
            .map(|attachment| {
                let id = attachment.id;
                let (opener, remover) = (this.clone(), this.clone());
                AttachmentCard::of(
                    attachment,
                    Rc::new(move |window, cx| {
                        _ = opener
                            .update(cx, |composer, cx| composer.open_attachment(id, window, cx));
                    }),
                    Rc::new(move |_, cx| {
                        _ = remover.update(cx, |composer, cx| composer.remove_attachment(id, cx));
                    }),
                )
            })
            .collect();
        let cards = (!self.attachments.is_empty())
            .then(|| h_flex().flex_wrap().gap_2().pb(px(2.)).children(cards));
        let composer = cx.weak_entity();
        let focus = self.draft.read(cx).focus_handle(cx);
        let prompt = div()
            .min_w_0()
            .cursor_text()
            .when(shape.one_line, |prompt| prompt.flex_1())
            .when(shape.relaxed, |prompt| prompt.w_full().min_h(OPEN_DRAFT))
            .when(!shape.relaxed && !shape.one_line, |prompt| prompt.w_full())
            .on_mouse_down(MouseButton::Left, move |_, window, cx| {
                focus.focus(window, cx)
            })
            .child(
                Textarea::new(&self.draft)
                    .disabled(self.start_busy() || locked)
                    .appearance(false)
                    .xsmall()
                    .text_size(px(13.))
                    .on_paste(move |item, _, cx| {
                        composer
                            .update(cx, |composer, cx| composer.pasted(item, cx))
                            .unwrap_or(false)
                    }),
            );
        let lead = (!locked).then(|| self.add_button(cx));
        let trail = (!locked).then(|| self.trail(shape.compact, content, window, cx));
        let sub_bar = match self.start_slots.clone() {
            Some(slots) => Some((slots.sub_bar)(window, cx)),
            None => self.branch.clone().map(|branch| {
                let muted = cx.theme().muted_foreground;
                h_flex()
                    .min_w_0()
                    .gap(px(6.))
                    .text_size(px(12.))
                    .text_color(muted)
                    .child(Icon::new(Glyph::GitBranch).size(px(13.)))
                    .child(div().min_w_0().truncate().child(branch))
                    .into_any_element()
            }),
        };
        let connecting = self.connecting.is_some();
        let theme = cx.theme();
        let offline = (self.offline.as_ref()).map(|machine| {
            SharedString::from(if self.still_offline {
                format!("{machine} still isn't answering. Send and Stop wait until it does.")
            } else {
                format!("{machine} is offline. Send and Stop wait until it answers.")
            })
        });
        let edge = if self.dragging {
            theme.drag_border
        } else if focused {
            theme.ring
        } else if locked {
            theme.warning
        } else {
            transparent_black()
        };
        let surface = if self.dragging {
            theme.drop_target
        } else {
            theme.muted
        };
        let inside = if shape.one_line {
            h_flex()
                .gap_2()
                .pl(px(16.))
                .pr(px(12.))
                .py(px(8.))
                .children(lead)
                .child(prompt)
                .children(trail)
        } else {
            v_flex()
                .gap_2()
                .pl(px(16.))
                .pr(px(12.))
                .pt(px(12.))
                .pb(px(8.))
                .children(cards)
                .child(prompt)
                .when(lead.is_some() || trail.is_some(), |inside| {
                    inside.child(
                        h_flex()
                            .justify_between()
                            .gap_2()
                            .children(lead)
                            .children(trail),
                    )
                })
                .when(self.dragging, |inside| {
                    inside.child(
                        div()
                            .text_size(px(13.))
                            .text_color(theme.drag_border)
                            .child("Drop images to attach them"),
                    )
                })
        };
        let capsule = v_flex()
            .children(self.answer.clone())
            .child(inside)
            .rounded(px(12.))
            .bg(surface)
            .border_1()
            .border_color(edge)
            .on_drag_move::<ExternalPaths>(cx.listener(
                |composer, event: &DragMoveEvent<ExternalPaths>, _, cx| {
                    let over = event.bounds.contains(&event.event.position);
                    if composer.dragging != over {
                        composer.dragging = over;
                        cx.notify();
                    }
                },
            ))
            .on_drop(cx.listener(|composer, paths: &ExternalPaths, window, cx| {
                composer.dragging = false;
                composer.dropped(paths.paths(), window, cx)
            }));
        let hint_line = h_flex()
            .justify_end()
            .gap_2()
            .pt(px(6.))
            .px_1()
            .text_size(px(12.))
            .text_color(theme.muted_foreground.opacity(0.78))
            .child("Paste or drop images · type / for commands")
            .when_some(self.meter.as_ref(), |line, meter| {
                line.child("·").child(format!("{}% left", meter.left))
            });
        let column = v_flex()
            .relative()
            .w_full()
            .max_w(COLUMN)
            .mx_auto()
            .px(GUTTER)
            .pt_2()
            .pb(px(10.));
        let column = self
            .slash_keys(column, cx)
            .when_some(offline, |column, line| {
                column.child(
                    h_flex()
                        .px_1()
                        .pb_1()
                        .gap_2()
                        .text_xs()
                        .text_color(cx.theme().muted_foreground)
                        .child(line)
                        .child(
                            Button::new("composer:connect")
                                .ghost()
                                .compact()
                                .label(if connecting {
                                    "Connecting…"
                                } else {
                                    "Connect"
                                })
                                .disabled(connecting)
                                .on_click(cx.listener(|composer, _, _, cx| composer.connect(cx))),
                        ),
                )
            })
            .when_some(self.error.clone(), |column, error| {
                column.child(
                    div()
                        .px_1()
                        .pb_1()
                        .text_xs()
                        .text_color(cx.theme().danger)
                        .child(error),
                )
            })
            .when_some(slash, |column, slash| {
                // Over the timeline, its bottom just above the box.
                column.child(
                    div()
                        .absolute()
                        .left(GUTTER)
                        .right(GUTTER)
                        .bottom_full()
                        .child(slash),
                )
            })
            .child(capsule)
            .when_some(sub_bar, |column, bar| {
                column.child(h_flex().min_w_0().mt(px(4.)).px(px(20.)).gap_2().child(bar))
            })
            .child(hint_line);
        let measure = cx.weak_entity();
        div()
            .relative()
            .w_full()
            .child(
                canvas(
                    move |bounds, _, cx| {
                        _ = measure.update(cx, |composer, cx| {
                            if composer.width != bounds.size.width {
                                composer.width = bounds.size.width;
                                cx.notify();
                            }
                        });
                    },
                    |_, _, _, _| {},
                )
                .absolute()
                .size_full(),
            )
            .child(column)
    }
}
