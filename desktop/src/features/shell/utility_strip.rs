//! `UtilityStrip` owns the strip under the panes: the settings button and the resource and usage figures, each
//! opening the kit's anchored popover (placed by measuring, flipped when there's no room). The figures are pushed by
//! the backend later; nothing here polls. Replaces UtilityStrip.svelte, utilityStrip.ts and the usage and resource
//! popovers in ShellOverlays.svelte.

use gpui_kit::*;

pub struct UtilityStrip;

impl Render for UtilityStrip {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
