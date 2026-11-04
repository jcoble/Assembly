//! Which checkout Source control has actually read for the selected session. It publishes this only from an accepted
//! status answer, so a layout's hint or a pick still on its way never reaches the Changes tab.
use crate::core::socket::{checkout_selector::CheckoutSelector, row_source::ExpectedRowSource};
use gpui_kit::*;

#[derive(Clone, Debug, PartialEq)]
pub struct Inspected {
    pub session: String,
    pub source: Option<ExpectedRowSource>,
    /// `None` is the session's own folder.
    pub checkout: Option<CheckoutSelector>,
    pub fingerprint: String,
    /// Counts Source control's answers: the fingerprint alone misses further edits to a file already changed.
    pub answers: u64,
}
#[derive(Default)]
pub struct InspectedCheckout {
    pub current: Option<Inspected>,
}
struct Holder(Entity<InspectedCheckout>);
impl Global for Holder {}
impl InspectedCheckout {
    pub fn global(cx: &mut App) -> Entity<Self> {
        if let Some(holder) = cx.try_global::<Holder>() {
            return holder.0.clone();
        }
        let inspected = cx.new(|_| Self::default());
        cx.set_global(Holder(inspected.clone()));
        inspected
    }
    pub fn publish(current: Option<Inspected>, cx: &mut App) {
        Self::global(cx).update(cx, |this, cx| {
            if this.current != current {
                this.current = current;
                cx.notify();
            }
        });
    }
}
