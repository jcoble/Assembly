//! How the composer draws, to the approved look (the mockup canvas's board 4): one box on the raised ground with the
//! design system's radius-xl corners and no edge until the draft has focus: attached images' chips, the draft, and a
//! footer with Attach and the agent's menus as outlined buttons on the left, then the branch the newest turn started
//! on, the context ring, Stop while a turn runs and the round Send, which is the one solid button and is dimmed while
//! there's nothing to send, an image hasn't uploaded or the session's machine is offline
//! (which a line above the box says; Stop is dimmed then too). Files dropped anywhere on the box light its edge. The "/" list floats just above the box, and why something failed sits above it.

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::Textarea;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use std::rc::Rc;

use super::Composer;
use crate::features::session::composer::attachment_chip::AttachmentChip;
use crate::features::session::composer::config_menu::{ConfigMenu, OnSelect};
use crate::features::session::composer::context_ring::ContextRing;
use crate::features::session::composer::slash_list::SlashList;
use crate::features::session::view::item_row::CHAT_COLUMN;

/// The design system's `radius-xl`, the composer's corners.
const BOX_RADIUS: Pixels = px(20.);
/// Send and Stop are round, this wide.
const ROUND_BUTTON: Pixels = px(30.);

impl Render for Composer {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let focused = self.draft.read(cx).focus_handle(cx).is_focused(window);
        let theme = cx.theme();
        let pill = theme.radius_full();
        let branch = self.branch.clone().map(|branch| {
            h_flex()
                .gap_1()
                .min_w_0()
                .max_w(px(160.))
                .font_family(theme.mono_font_family.clone())
                .text_size(px(11.5))
                .text_color(theme.muted_foreground)
                .child(Icon::new(Glyph::GitBranch).size(px(12.)))
                .child(div().min_w_0().truncate().child(branch))
        });
        let empty = self.draft.read(cx).value().trim().is_empty() && self.attachments.is_empty();
        let waiting = !self.all_saved();
        let running = self.running.is_some();
        let offline = self.offline.is_some();
        let composer = cx.weak_entity();
        let on_select: OnSelect = Rc::new(move |id, value, _, cx| {
            _ = composer.update(cx, |composer, cx| {
                composer.choose(id.into(), value.into(), cx)
            });
        });
        let menus = self
            .menus()
            .into_iter()
            .map(|(option, current)| ConfigMenu {
                config_id: option.id.clone(),
                label: option.name.clone().into(),
                choices: (option.options.iter())
                    .map(|choice| (choice.value.clone(), choice.name.clone().into()))
                    .collect(),
                current: current.to_string(),
                on_select: on_select.clone(),
            });
        let foot = h_flex()
            .gap_1p5()
            .child(
                Button::new("composer:attach")
                    .outline()
                    .small()
                    .icon(Glyph::Paperclip)
                    .rounded(pill)
                    .tooltip("Attach images")
                    .on_click(
                        cx.listener(|composer, _, window, cx| composer.pick_images(window, cx)),
                    ),
            )
            .children(menus)
            .child(div().flex_1())
            .children(branch)
            .when_some(self.meter.clone(), |foot, meter| {
                foot.child(ContextRing { meter })
            })
            .when(running, |foot| {
                foot.child(
                    Button::new("composer:stop")
                        .icon(Glyph::Square)
                        .rounded(pill)
                        .with_size(ROUND_BUTTON)
                        .disabled(offline)
                        .tooltip("Stop")
                        .on_click(cx.listener(|composer, _, _, cx| composer.stop(cx))),
                )
            })
            .child(
                Button::new("composer:send")
                    .primary()
                    .icon(Glyph::ArrowUp)
                    .rounded(pill)
                    .with_size(ROUND_BUTTON)
                    .disabled(empty || waiting || offline)
                    .tooltip(if running { "Steer" } else { "Send" })
                    .on_click(cx.listener(|composer, _, window, cx| composer.send(window, cx))),
            );
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
        let slash = (!commands.is_empty()).then(|| SlashList {
            commands,
            selected: self.slash_lit(cx),
            scroll: self.slash_scroll.clone(),
            on_pick: Rc::new(move |at, window, cx| {
                _ = composer.update(cx, |composer, cx| composer.pick_command(at, window, cx));
            }),
        });
        let chips = self.attachments.iter().map(|attachment| {
            let (composer, id) = (cx.weak_entity(), attachment.id);
            AttachmentChip::of(
                attachment,
                Rc::new(move |_, cx| {
                    _ = composer.update(cx, |composer, cx| composer.remove_attachment(id, cx));
                }),
            )
        });
        let chips =
            (!self.attachments.is_empty()).then(|| h_flex().flex_wrap().gap_1p5().children(chips));
        let composer = cx.weak_entity();
        let draft = Textarea::new(&self.draft)
            .appearance(false)
            .on_paste(move |item, _, cx| {
                composer
                    .update(cx, |composer, cx| composer.pasted(item, cx))
                    .unwrap_or(false)
            });
        let root = div().relative().w_full().max_w(CHAT_COLUMN).mx_auto();
        let root = self.slash_keys(root.px_4().pt_1().pb_3(), cx);
        let theme = cx.theme();
        let offline = (self.offline.as_ref()).map(|machine| {
            SharedString::from(if self.still_offline {
                format!("{machine} still isn't answering. Send and Stop wait until it does.")
            } else {
                format!("{machine} is offline. Send and Stop wait until it answers.")
            })
        });
        let connecting = self.connecting.is_some();
        root.when_some(offline, |root, line| {
            root.child(
                h_flex()
                    .px_1()
                    .pb_1()
                    .gap_2()
                    .text_xs()
                    .text_color(theme.muted_foreground)
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
        .when_some(self.error.clone(), |root, error| {
            root.child(
                div()
                    .px_1()
                    .pb_1()
                    .text_xs()
                    .text_color(theme.danger)
                    .child(error),
            )
        })
        .when_some(slash, |root, slash| {
            // Over the timeline, its bottom just above the box.
            root.child(
                div()
                    .absolute()
                    .left_4()
                    .right_4()
                    .bottom_full()
                    .child(slash),
            )
        })
        .child(
            v_flex()
                .gap_2()
                .pl(px(14.))
                .pr(px(10.))
                .py(px(10.))
                .rounded(BOX_RADIUS)
                .bg(theme.group_box)
                .border_1()
                .border_color(if focused {
                    theme.ring
                } else {
                    transparent_black()
                })
                .drag_over::<ExternalPaths>(|style, _, _, cx| {
                    style.border_color(cx.theme().drag_border)
                })
                .on_drop(cx.listener(|composer, paths: &ExternalPaths, window, cx| {
                    composer.dropped(paths.paths(), window, cx)
                }))
                .children(chips)
                .child(draft)
                .child(foot),
        )
    }
}
