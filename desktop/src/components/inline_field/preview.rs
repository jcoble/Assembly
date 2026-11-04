//! Optional demonstration: the caller confirms changes in preview memory only.
use super::{ChoiceOption, InlineField, SaveRequested};
use chrono::{NaiveDate, NaiveTime};
use gpui_kit::component::group_box::{GroupBox, GroupBoxVariants as _};
use gpui_kit::component::label::Label;
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;

pub struct InlineFieldPreview {
    fields: Vec<Entity<InlineField>>,
    grouped: Entity<super::grouped::GroupedSampleForm>,
    notice: String,
    _heard: Vec<Subscription>,
}
impl InlineFieldPreview {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let fields = vec![
            cx.new(|cx| InlineField::text("Project name", "Assembly", window, cx)),
            cx.new(|cx| {
                InlineField::date(
                    "Due date",
                    NaiveDate::from_ymd_opt(2026, 11, 12),
                    window,
                    cx,
                )
            }),
            cx.new(|cx| {
                InlineField::time(
                    "Start time",
                    NaiveTime::from_hms_opt(9, 30, 0).expect("valid sample time"),
                    window,
                    cx,
                )
            }),
            cx.new(|cx| {
                InlineField::combo(
                    "Machine",
                    vec![
                        ChoiceOption::new("local", "This Mac"),
                        ChoiceOption::new("workbox", "Workbox"),
                    ],
                    Some("workbox".into()),
                    window,
                    cx,
                )
            }),
        ];
        let heard = fields
            .iter()
            .map(|field| {
                cx.subscribe_in(
                    field,
                    window,
                    |preview, field, save: &SaveRequested, window, cx| {
                        field.update(cx, |field, cx| {
                            field.complete_save(save.request, Ok(save.value.clone()), window, cx);
                        });
                        preview.notice =
                            "Stored in this preview's memory only. No real save was performed."
                                .into();
                        cx.notify();
                    },
                )
            })
            .collect();
        Self {
            fields,
            grouped: cx.new(|cx| super::grouped::GroupedSampleForm::new(window, cx)),
            notice: String::new(),
            _heard: heard,
        }
    }
}
impl Render for InlineFieldPreview {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let mut content=v_flex().gap_4().p_4().w_full().max_w(px(576.))
            .child(Label::new("Inline fields"))
            .child(Label::new("Sample only · Click a value to edit. Save stores changes in this preview’s memory only."));
        let mut group = GroupBox::new()
            .id("inline-fields-group")
            .outline()
            .title("Sample values");
        for field in &self.fields {
            group = group.child(field.clone());
        }
        content = content.child(group).child(self.grouped.clone());
        content.child(Label::new(self.notice.clone()).text_color(cx.theme().muted_foreground))
    }
}
