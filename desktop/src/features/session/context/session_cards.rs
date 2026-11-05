//! The Context tab's first two cards, drawn from the selected session's live facts: Session (its model, effort and
//! access mode, in the composer's words) and Context usage (current context of the window, the session's total, and the
//! newest turn's in and out). A figure nobody sent reads "not reported by NAME" in muted text, never a default or zero.

use gpui_kit::assets::IconName;
use gpui_kit::component::progress::Progress;
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::ContextPane;
use crate::core::ui::provider_name::provider_name;
use crate::features::session::composer::context_meter::thousands;
use crate::features::session::context::live_facts::LiveFacts;
use crate::features::session::context::usage_model::usage;
use crate::section_card::SectionCard;

/// One figure as a card words it: its debug selector, its words, and whether anyone reported it.
#[derive(Clone, Debug, PartialEq)]
pub(super) struct Words {
    pub id: &'static str,
    pub text: String,
    pub reported: bool,
}

/// "not reported by Claude", or "not reported" when the provider isn't known.
pub(super) fn not_reported(provider: Option<&str>) -> String {
    match provider {
        Some(provider) => format!("not reported by {}", provider_name(provider)),
        None => "not reported".into(),
    }
}

/// `value`'s words, else `label` and "not reported".
fn words(id: &'static str, value: Option<String>, label: &str, missing: &str) -> Words {
    Words {
        id,
        reported: value.is_some(),
        text: value.unwrap_or_else(|| format!("{label} {missing}").trim_start().to_string()),
    }
}

/// The Session card's Model, Effort and Access mode values.
pub(super) fn session_words(facts: Option<&LiveFacts>) -> [Words; 3] {
    let missing = not_reported(facts.and_then(|facts| facts.provider.as_deref()));
    let value =
        |pick: fn(&LiveFacts) -> &Option<String>| facts.and_then(|facts| pick(facts).clone());
    [
        words("context-model", value(|facts| &facts.model), "", &missing),
        words("context-effort", value(|facts| &facts.effort), "", &missing),
        words("context-access", value(|facts| &facts.access), "", &missing),
    ]
}

/// The Context usage card's percent, if it has one, and its used, window, total, in and out, in that order. `total`
/// is `None` until `rows/tokens` has answered, and the total reads "Reading…" until then.
pub(super) fn usage_words(
    facts: Option<&LiveFacts>,
    total: Option<Option<i64>>,
) -> (Option<u8>, [Words; 5]) {
    let missing = not_reported(facts.and_then(|facts| facts.provider.as_deref()));
    let context = facts.and_then(|facts| facts.context);
    let figures = usage(
        context.map(|context| context.used),
        context.map(|context| context.size),
        facts.and_then(|facts| facts.input_tokens),
        facts.and_then(|facts| facts.output_tokens),
        total.flatten(),
        total.is_some(),
    );
    let number = |n: Option<i64>, shape: fn(String) -> String| n.map(|n| shape(thousands(n)));
    let words = [
        words(
            "context-used",
            number(figures.used, |n| format!("Current context: {n}")),
            "Current context",
            &missing,
        ),
        words(
            "context-window",
            number(figures.window, |n| format!("of {n}")),
            "window size",
            &missing,
        ),
        // Still reading isn't a figure nobody sent.
        words(
            "context-total",
            number(figures.total, |n| format!("{n} tokens")),
            "",
            if total.is_some() {
                &missing
            } else {
                "Reading…"
            },
        ),
        words(
            "context-in",
            number(figures.input, |n| format!("In: {n}")),
            "In",
            &missing,
        ),
        words(
            "context-out",
            number(figures.output, |n| format!("Out: {n}")),
            "Out",
            &missing,
        ),
    ];
    (figures.percent, words)
}

/// A figure's words, muted when nobody reported it. Long words, such as "not reported by Claude" in a narrow drawer,
/// wrap rather than cut off.
fn figure(words: Words, muted: Hsla) -> Div {
    let id = words.id;
    div()
        .debug_selector(move || id.into())
        .min_w_0()
        .when(!words.reported, |figure| figure.text_color(muted))
        .child(words.text)
}

/// Two things on one line, the first on the left and the second on the right, each wrapping onto more lines when
/// they don't fit side by side; the right one stays against the right edge.
fn pair(left: impl IntoElement, right: impl IntoElement) -> Div {
    h_flex()
        .gap_2()
        .items_start()
        .child(left)
        .child(div().flex_1())
        .child(div().min_w_0().text_right().child(right))
}

impl ContextPane {
    pub(super) fn session_card(&self, facts: Option<&LiveFacts>, cx: &App) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, line) = (theme.muted_foreground, theme.border);
        let labels = ["Model", "Effort", "Access mode"];
        SectionCard::new(IconName::Bot, "Session").child(v_flex().children(
            (labels.into_iter().zip(session_words(facts)).enumerate()).map(
                |(index, (label, value))| {
                    pair(div().flex_none().child(label), figure(value, muted))
                        .py_1()
                        .when(index > 0, |row| row.border_t_1().border_color(line))
                },
            ),
        ))
    }

    pub(super) fn usage_card(&self, facts: Option<&LiveFacts>, cx: &App) -> impl IntoElement {
        let muted = cx.theme().muted_foreground;
        let (percent, [used, window, total, input, output]) = usage_words(facts, self.total);
        SectionCard::new(IconName::Gauge, "Context usage")
            .child(pair(figure(used, muted), figure(window, muted)))
            .when_some(percent, |card, percent| {
                card.child(
                    h_flex()
                        .gap_2()
                        .child(
                            div().flex_1().child(
                                Progress::new("context-usage-bar").value(f32::from(percent)),
                            ),
                        )
                        .child(
                            div()
                                .debug_selector(|| "context-percent".into())
                                .flex_none()
                                .child(format!("{percent}%")),
                        ),
                )
            })
            .child(pair(div().child("Session total"), figure(total, muted)))
            .child(pair(figure(input, muted), figure(output, muted)))
    }
}

#[cfg(test)]
#[path = "session_cards_tests.rs"]
mod tests;
