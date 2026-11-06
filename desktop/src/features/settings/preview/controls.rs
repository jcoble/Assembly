//! Editable presentation fixtures using the same controlled inline editors as Connections.
//! These values never cross the preview boundary into application preferences.
use super::{fonts, row};
use crate::core::theme::theme_id::ThemeId;
use crate::inline_field::{ChoiceOption, FieldValue, InlineField, SaveFailure, SaveRequested};
use gpui_kit::component::label::Label;
use gpui_kit::component::switch::Switch;
use gpui_kit::component::v_flex;
use gpui_kit::*;

pub(super) struct Control {
    pub section: &'static str,
    pub card: &'static str,
    pub description: &'static str,
    pub title: &'static str,
    pub keywords: &'static str,
    pub field: AnyView,
    _save: Option<Subscription>,
}

/// A directly editable sample setting, retained when navigating between pages.
struct SwitchControl {
    title: &'static str,
    checked: bool,
}
impl Render for SwitchControl {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        v_flex().gap_2().child(Label::new(self.title)).child(
            Switch::new("value")
                .accessibility_label(self.title)
                .checked(self.checked)
                .on_change(cx.listener(|s, checked, _, cx| {
                    s.checked = *checked;
                    cx.notify();
                })),
        )
    }
}

#[derive(Clone, Copy)]
struct Range {
    min: f64,
    max: f64,
    whole: bool,
}
impl Range {
    fn validate(self, value: &FieldValue) -> Result<FieldValue, String> {
        let FieldValue::Text(text) = value else {
            return Err("Enter a number.".into());
        };
        let number = text
            .trim()
            .parse::<f64>()
            .map_err(|_| "Enter a number.".to_owned())?;
        if !number.is_finite()
            || number < self.min
            || number > self.max
            || (self.whole && number.fract() != 0.)
        {
            return Err(if self.max == f64::MAX {
                format!("Enter a whole number of at least {}.", self.min)
            } else {
                format!(
                    "Enter {}between {} and {}.",
                    if self.whole {
                        "a whole number "
                    } else {
                        "a number "
                    },
                    self.min,
                    self.max
                )
            });
        }
        Ok(FieldValue::Text(number.to_string()))
    }
}

impl Control {
    fn choice(
        labels: [&'static str; 4],
        choices: Vec<ChoiceOption>,
        value: &str,
        window: &mut Window,
        cx: &mut App,
    ) -> Self {
        let [section, card, title, description] = labels;
        let allowed = choices
            .iter()
            .map(|choice| choice.value.to_string())
            .collect::<Vec<_>>();
        let field = cx.new(|cx| InlineField::combo(title, choices, Some(value.into()), window, cx));
        let save = field.update(cx, |_, cx| {
            cx.subscribe_in(
                &cx.entity(),
                window,
                move |field, _, save: &SaveRequested, window, cx| {
                    let result = match &save.value {
                        FieldValue::Choice(Some(value)) if allowed.contains(value) => {
                            Ok(save.value.clone())
                        }
                        _ => Err(SaveFailure::Refused("Choose a value from the list.".into())),
                    };
                    field.complete_save(save.request, result, window, cx);
                },
            )
        });
        Self {
            section,
            card,
            title,
            description,
            keywords: "",
            field: field.into(),
            _save: Some(save),
        }
    }
    fn number(
        row: &'static row::Row,
        value: &str,
        range: Range,
        window: &mut Window,
        cx: &mut App,
    ) -> Self {
        let field = cx.new(|cx| InlineField::text(row.title, value, window, cx));
        let save = field.update(cx, |_, cx| {
            cx.subscribe_in(
                &cx.entity(),
                window,
                move |field, _, save: &SaveRequested, window, cx| {
                    let result = range.validate(&save.value).map_err(SaveFailure::Refused);
                    field.complete_save(save.request, result, window, cx);
                },
            )
        });
        Self {
            section: row.section,
            card: row.card,
            title: row.title,
            description: row.description,
            keywords: row.keywords,
            field: field.into(),
            _save: Some(save),
        }
    }
}
fn choices(values: &[(&str, &str)]) -> Vec<ChoiceOption> {
    values
        .iter()
        .map(|(value, label)| ChoiceOption::new(*value, *label))
        .collect()
}

pub(super) fn create(window: &mut Window, cx: &mut App) -> Vec<Control> {
    let (ui, code) = fonts::installed(cx);
    let font_choices = |names: &[SharedString], inherit: bool| {
        let mut list = Vec::new();
        if inherit {
            list.push(ChoiceOption::new("", "Same as Code font"));
        }
        list.extend(
            names
                .iter()
                .map(|name| ChoiceOption::new(name.to_string(), fonts::label(name))),
        );
        list
    };
    let mut controls = Vec::new();
    for row in &row::ROWS {
        let number = match row.id {
            "ui-font-size" => Some(("13", 11., 18., true)),
            "editor-font-size" => Some(("13", 10., 24., true)),
            "editor-line-height" => Some(("20", 14., 36., true)),
            "terminal-font-size" => Some(("15", 10., 24., true)),
            "terminal-line-height" => Some(("1.2", 1., 2., false)),
            _ => None,
        };
        if let Some((value, min, max, whole)) = number {
            controls.push(Control::number(
                row,
                value,
                Range { min, max, whole },
                window,
                cx,
            ));
            continue;
        }
        let choice = match row.id {
            "theme" => Some((
                ThemeId::ALL
                    .into_iter()
                    .map(|id| ChoiceOption::new(id.as_str(), id.theme_name()))
                    .collect(),
                "assembly",
            )),
            "ui-font" => Some((font_choices(&ui, false), ".SystemUIFont")),
            "code-font" => Some((font_choices(&code, false), "Menlo")),
            "editor-font" | "terminal-font" => Some((font_choices(&code, true), "")),
            _ => None,
        };
        if let Some((items, value)) = choice {
            let mut control = Control::choice(
                [row.section, row.card, row.title, row.description],
                items,
                value,
                window,
                cx,
            );
            control.keywords = row.keywords;
            controls.push(control);
        }
    }
    for (section, card, title, description, values, value) in [
        (
            "general",
            "Outside the app",
            "External terminal app",
            "Used when opening a folder in a terminal.",
            vec![
                ("Warp", "Warp"),
                ("Terminal", "Terminal"),
                ("iTerm", "iTerm"),
                ("iTerm2", "iTerm2"),
                ("Ghostty", "Ghostty"),
                ("WezTerm", "WezTerm"),
                ("Alacritty", "Alacritty"),
            ],
            "Warp",
        ),
        (
            "general",
            "Panels",
            "Problems list",
            "Choose where the project's errors and warnings appear.",
            vec![("bottom", "Along the bottom"), ("hidden", "Hidden")],
            "hidden",
        ),
        (
            "helper",
            "Which model",
            "Connection",
            "Use a signed-in CLI or your own API connection.",
            vec![("cli", "Signed-in CLI"), ("api", "API key")],
            "cli",
        ),
        (
            "helper",
            "Which model",
            "Service",
            "The provider used for session titles, Inspect and review drafts.",
            vec![
                ("openai", "OpenAI / Codex"),
                ("anthropic", "Anthropic / Claude"),
            ],
            "openai",
        ),
    ] {
        controls.push(Control::choice(
            [section, card, title, description],
            choices(&values),
            value,
            window,
            cx,
        ));
    }
    for (section, card, title, description) in [
        (
            "general",
            "Language servers",
            "Supercharged",
            "Allow language servers while working in the editor.",
        ),
        (
            "general",
            "Language servers",
            "C#",
            "Allow the C# language server while Supercharged is on.",
        ),
        (
            "general",
            "Language servers",
            "TypeScript / JavaScript",
            "Allow the TypeScript language server while Supercharged is on.",
        ),
        (
            "general",
            "Language servers",
            "Rust",
            "Allow rust-analyzer while Supercharged is on.",
        ),
        (
            "editor",
            "Text",
            "Font ligatures",
            "Show combined glyphs for character sequences such as =>.",
        ),
        (
            "terminal",
            "Behaviour",
            "Cursor blink",
            "Blink the cursor while the terminal has focus.",
        ),
    ] {
        controls.push(Control {
            section,
            card,
            title,
            description,
            keywords: "",
            field: cx
                .new(|_| SwitchControl {
                    title,
                    checked: true,
                })
                .into(),
            _save: None,
        });
    }
    for row in &AGENT_ROWS {
        let (value, max) = if row.id == "auto-done" {
            ("60", f64::MAX)
        } else {
            ("5", 10.)
        };
        let min = if row.id == "auto-done" { 0. } else { 1. };
        controls.push(Control::number(
            row,
            value,
            Range {
                min,
                max,
                whole: true,
            },
            window,
            cx,
        ));
    }
    controls
}

static AGENT_ROWS: [row::Row; 2] = [
    row::Row {
        id: "auto-done",
        section: "agents",
        card: "Helper sessions",
        title: "Mark finished helpers Done after (minutes)",
        description: "Minutes to wait on this machine; 0 means Never.",
        keywords: "helpers agents done",
    },
    row::Row {
        id: "helper-limit",
        section: "agents",
        card: "Helper sessions",
        title: "Helpers per session",
        description: "Maximum unfinished helpers for each session on this machine (1–10).",
        keywords: "helpers agents limit",
    },
];

#[cfg(test)]
mod tests {
    use super::{FieldValue, Range};
    fn validate(text: &str, min: f64, max: f64, whole: bool) -> Result<FieldValue, String> {
        Range { min, max, whole }.validate(&FieldValue::Text(text.into()))
    }
    #[test]
    fn typography_range_accepts_endpoints_and_normalizes() {
        assert_eq!(
            validate(" 11.0 ", 11., 18., true),
            Ok(FieldValue::Text("11".into()))
        );
        assert!(validate("18", 11., 18., true).is_ok());
        for bad in ["10", "19", "13.5", "", "NaN", "inf"] {
            assert!(validate(bad, 11., 18., true).is_err(), "{bad}");
        }
    }
    #[test]
    fn line_height_accepts_fractional_values() {
        assert_eq!(
            validate("1.25", 1., 2., false),
            Ok(FieldValue::Text("1.25".into()))
        );
        assert!(validate("0.99", 1., 2., false).is_err());
    }
    #[test]
    fn helpers_allow_never_and_no_arbitrary_timeout_ceiling() {
        assert!(validate("0", 0., f64::MAX, true).is_ok());
        assert!(validate("20000", 0., f64::MAX, true).is_ok());
        assert!(validate("-1", 0., f64::MAX, true).is_err());
        assert!(validate("11", 1., 10., true).is_err());
    }
}
