//! A task's page: title, status and priority save as you change them, as in Notion; other properties read only; the body
//! block by block; then comments. Everything shown is selectable. A list draws it, so a long page costs only what's on
//! screen. The owner reads the page and hands it over with `detail_arrived`; this view asks by `TaskDetailEvent::Reread`.

use std::collections::HashSet;

use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;
use serde_json::Value;

use crate::core::intents::open_url::OpenUrl;
use crate::inline_field::{
    ChoiceOption, FieldValue, InlineField, SaveFailure, SaveRequest, SaveRequested,
};

use super::notion_client::{BlockEdit, NotionClient};
use super::task_body::{BodyRow, body_rows, edit_kind};
use super::task_edit::{AfterEdit, edit_outcome};
use super::task_export::*;
use super::task_properties::{choice_of, other_properties, title_of};
use super::tasks_state::TaskDetail;
use super::tasks_view::{priority_tone, status_tone};

#[path = "task_images.rs"]
mod images;
#[path = "task_pictures.rs"]
mod pictures;
#[path = "task_detail_rows.rs"]
mod rows;

pub enum TaskDetailEvent {
    Back,
    Reread,
    ListStale,
    /// The page's pictures have all gone on, or failed.
    PicturesSettled,
}

/// What the page opens with, from the list, before Notion answers.
#[derive(Clone, Debug, Default)]
pub struct TaskSeed {
    pub title: String,
    pub key: String,
    pub project: String,
    pub status: String,
    pub priority: String,
    pub url: String,
    pub statuses: Vec<String>,
    pub priorities: Vec<String>,
}

pub(super) struct Comment {
    pub(super) markdown: String,
    pub(super) when: String,
}

pub(super) struct BlockEditor {
    pub(super) id: String,
    pub(super) field: Entity<InlineField>,
    _heard: [Subscription; 2],
}

pub struct TaskDetailView {
    pub(super) source: NotionTaskRef,
    pub(super) seed: TaskSeed,
    client: NotionClient,
    revision: Option<String>,
    pub(super) title: Entity<InlineField>,
    pub(super) status: Entity<InlineField>,
    pub(super) priority: Entity<InlineField>,
    pub(super) add_text: Entity<InlineField>,
    pub(super) others: Vec<(String, String)>,
    blocks: Vec<NotionBlock>,
    pub(super) rows: Vec<BodyRow>,
    open: HashSet<String>,
    pub(super) comments: Vec<Comment>,
    pub(super) comments_unavailable: bool,
    pub(super) loaded: bool,
    pub(super) error: Option<String>,
    pub(super) note: Option<String>,
    pub(super) editor: Option<BlockEditor>,
    /// The to-do being saved, whose check can't be pressed again yet.
    pub(super) checking: Option<String>,
    pub(super) list: ListState,
    pub(super) focus: FocusHandle,
    pub(super) saves: Vec<Task<()>>,
    images: images::ImageQueue,
    pub(super) pictures: pictures::PagePictures,
    _heard: Vec<Subscription>,
}

impl EventEmitter<TaskDetailEvent> for TaskDetailView {}

fn choices(
    values: &[String],
    current: &str,
    tone: fn(&str) -> crate::activity::Tone,
) -> Vec<ChoiceOption> {
    let mut options: Vec<ChoiceOption> = values
        .iter()
        .map(|value| ChoiceOption::new(value.clone(), value.clone()).tone(tone(value)))
        .collect();
    if !current.is_empty() && !values.iter().any(|value| value == current) {
        options.push(ChoiceOption::new(current, current.to_owned()).tone(tone(current)));
    }
    options
}

fn choice_value(value: &str) -> Option<String> {
    (!value.is_empty()).then(|| value.to_owned())
}

fn field_text(value: &FieldValue) -> String {
    match value {
        FieldValue::Text(text) => text.clone(),
        FieldValue::Choice(choice) => choice.clone().unwrap_or_default(),
        _ => String::new(),
    }
}

impl TaskDetailView {
    pub fn new(
        source: NotionTaskRef,
        seed: TaskSeed,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let title = cx.new(|cx| InlineField::multiline("Title", seed.title.clone(), window, cx));
        let status = cx.new(|cx| {
            let options = choices(&seed.statuses, &seed.status, status_tone);
            InlineField::chip("Status", options, choice_value(&seed.status), window, cx)
        });
        let priority = cx.new(|cx| {
            let options = choices(&seed.priorities, &seed.priority, priority_tone);
            InlineField::chip(
                "Priority",
                options,
                choice_value(&seed.priority),
                window,
                cx,
            )
        });
        let add_text = cx.new(|cx| InlineField::multiline("Add text", "", window, cx));
        Self::hand_pasted_pictures_to_page(&title, cx);
        Self::hand_pasted_pictures_to_page(&add_text, cx);
        let property_saved = |name: &'static str| {
            move |view: &mut Self,
                  field: &Entity<InlineField>,
                  event: &SaveRequested,
                  window: &mut Window,
                  cx: &mut Context<Self>| {
                view.save_property(field.clone(), name, event, window, cx);
            }
        };
        let heard = vec![
            cx.subscribe_in(&title, window, property_saved("title")),
            cx.subscribe_in(&status, window, property_saved("Status")),
            cx.subscribe_in(&priority, window, property_saved("Priority")),
            cx.subscribe_in(&add_text, window, Self::append_text),
            cx.observe(&title, |view, _, cx| view.field_changed(cx)),
            cx.observe(&status, |view, _, cx| view.field_changed(cx)),
            cx.observe(&priority, |view, _, cx| view.field_changed(cx)),
            cx.observe(&add_text, |view, _, cx| view.field_changed(cx)),
            cx.on_release(|view, cx| view.pictures.release(cx)),
        ];
        Self {
            source,
            seed,
            client: NotionClient,
            revision: None,
            title,
            status,
            priority,
            add_text,
            others: Vec::new(),
            blocks: Vec::new(),
            rows: Vec::new(),
            open: HashSet::new(),
            comments: Vec::new(),
            comments_unavailable: false,
            loaded: false,
            error: None,
            note: None,
            editor: None,
            checking: None,
            list: ListState::new(2, ListAlignment::Top, px(600.)),
            focus: cx.focus_handle(),
            saves: Vec::new(),
            images: images::ImageQueue::default(),
            pictures: pictures::PagePictures::default(),
            _heard: heard,
        }
    }

    /// A picture pasted into a text box goes on the page, as one pasted anywhere else does.
    fn hand_pasted_pictures_to_page(field: &Entity<InlineField>, cx: &mut Context<Self>) {
        let view = cx.entity().downgrade();
        field.update(cx, |field, _| {
            field.take_pastes(move |item, window, cx| {
                view.update(cx, |view, cx| view.paste_item(item, window, cx))
                    .unwrap_or(false)
            });
        });
    }

    /// A field can change height as it opens, shuts or wraps.
    fn field_changed(&mut self, cx: &mut Context<Self>) {
        self.list.remeasure();
        cx.notify();
    }

    /// The address to open the page at: the list's, or the page's own.
    pub(super) fn page_url(&self) -> String {
        if self.seed.url.is_empty() {
            format!(
                "https://www.notion.so/{}",
                self.source.page_id.replace('-', "")
            )
        } else {
            self.seed.url.clone()
        }
    }

    pub(super) fn open_in_notion(&self, window: &mut Window, cx: &mut App) {
        window.dispatch_action(
            Box::new(OpenUrl {
                url: self.page_url(),
            }),
            cx,
        );
    }

    /// What Notion holds. A value being edited stays as typed; a field waiting to learn what happened learns it here.
    pub fn detail_arrived(
        &mut self,
        detail: &TaskDetail,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.revision = Some(detail.revision.clone());
        self.loaded = true;
        self.error = None;
        let (title, status, priority) = (
            title_of(&detail.properties),
            choice_of(&detail.properties, "Status"),
            choice_of(&detail.properties, "Priority"),
        );
        self.seed.status = status.clone();
        self.seed.priority = priority.clone();
        self.seed.title = title.clone();
        self.title.update(cx, |field, cx| {
            let _ = field.set_committed(FieldValue::Text(title), window, cx);
        });
        self.status.update(cx, |field, cx| {
            let _ = field.set_committed(FieldValue::Choice(choice_value(&status)), window, cx);
        });
        self.priority.update(cx, |field, cx| {
            let _ = field.set_committed(FieldValue::Choice(choice_value(&priority)), window, cx);
        });
        self.others = other_properties(&detail.properties);
        self.blocks = detail.blocks.clone();
        self.comments = detail.comments.iter().map(comment).collect();
        self.comments_unavailable = detail.comments_unavailable;
        self.rebuild_rows();
        self.pictures.keep_only(&self.rows, window, cx);
        if self.images.take_reveal() {
            // Every row was just reset, so nothing has a height to reveal by: go to the new picture's row itself.
            self.list.scroll_to(ListOffset {
                item_ix: self.rows.len(),
                offset_in_item: px(0.),
            });
        }
        self.checking = None;
        if self
            .editor
            .as_ref()
            .is_some_and(|editor| !self.rows.iter().any(|row| row.id == editor.id))
        {
            self.editor = None;
        }
        cx.notify();
    }

    pub fn detail_failed(&mut self, message: String, cx: &mut Context<Self>) {
        self.error = Some(message);
        self.checking = None;
        self.list.remeasure();
        cx.notify();
    }

    /// The list keeps its place across the new rows.
    fn rebuild_rows(&mut self) {
        self.rows = body_rows(&self.blocks, &self.open);
        let place = self.list.logical_scroll_top();
        self.list.reset(self.rows.len() + 2);
        self.list.scroll_to(place);
    }

    pub(super) fn toggle(&mut self, id: &str, cx: &mut Context<Self>) {
        if !self.open.remove(id) {
            self.open.insert(id.to_owned());
        }
        self.rebuild_rows();
        cx.notify();
    }

    fn save_property(
        &mut self,
        field: Entity<InlineField>,
        property: &'static str,
        event: &SaveRequested,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let (request, value) = (event.request, event.value.clone());
        let Some(revision) = self.revision.clone() else {
            field.update(cx, |field, cx| {
                let refused = SaveFailure::Refused("The task is still loading.".into());
                field.complete_save(request, Err(refused), window, cx);
            });
            return;
        };
        let call = self.client.property(
            self.source.clone(),
            property.into(),
            field_text(&value),
            revision,
            cx,
        );
        let task = cx.spawn_in(window, async move |view, cx| {
            let result = call.await;
            let _ = view.update_in(cx, |view, window, cx| {
                view.save_finished(&field, request, value, result, window, cx);
            });
        });
        self.saves.push(task);
    }

    /// A save's answer: the field learns it, and what's on screen follows.
    pub(super) fn save_finished(
        &mut self,
        field: &Entity<InlineField>,
        request: SaveRequest,
        value: FieldValue,
        result: Result<NotionEditReceipt, NotionError>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let (saved, after) = edit_outcome(result.as_ref());
        if let (Ok(receipt), Ok(())) = (&result, &saved) {
            self.revision = Some(receipt.revision.clone());
        }
        field.update(cx, |field, cx| {
            field.complete_save(request, saved.map(|()| value), window, cx);
        });
        self.follow_up(after, cx);
    }

    fn follow_up(&mut self, after: AfterEdit, cx: &mut Context<Self>) {
        match after {
            AfterEdit::Nothing => {}
            AfterEdit::RefreshList => cx.emit(TaskDetailEvent::ListStale),
            AfterEdit::Reread => cx.emit(TaskDetailEvent::Reread),
        }
    }

    fn append_text(
        &mut self,
        field: &Entity<InlineField>,
        event: &SaveRequested,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let (field, request, text) = (field.clone(), event.request, field_text(&event.value));
        let empty = FieldValue::Text(String::new());
        if text.trim().is_empty() {
            field.update(cx, |field, cx| {
                field.complete_save(request, Ok(empty), window, cx);
            });
            return;
        }
        let call = self.client.append_text(self.source.clone(), text, cx);
        let task = cx.spawn_in(window, async move |view, cx| {
            let result = call.await;
            let _ = view.update_in(cx, |view, window, cx| {
                let (saved, after) = edit_outcome(result.as_ref());
                let landed = saved.is_ok();
                field.update(cx, |field, cx| {
                    field.complete_save(request, saved.map(|()| empty), window, cx);
                });
                view.follow_up(after, cx);
                if landed {
                    cx.emit(TaskDetailEvent::Reread);
                }
            });
        });
        self.saves.push(task);
    }

    pub(super) fn edit_block(
        &mut self,
        row: &BodyRow,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let (id, kind, checked) = (row.id.clone(), row.kind.clone(), row.checked);
        let field =
            cx.new(|cx| InlineField::multiline("Edit block", row.plain.clone(), window, cx));
        Self::hand_pasted_pictures_to_page(&field, cx);
        let saved = cx.subscribe_in(
            &field,
            window,
            move |view: &mut Self, field, event: &SaveRequested, window, cx| {
                view.save_block(
                    field.clone(),
                    id.clone(),
                    kind.clone(),
                    checked,
                    event,
                    window,
                    cx,
                );
            },
        );
        let watched = cx.observe(&field, |view, field, cx| {
            // Saved or backed out of: the block draws as itself again.
            let field = field.read(cx);
            if !field.editing() && !field.pending() {
                view.editor = None;
            }
            view.field_changed(cx);
        });
        field.update(cx, |field, cx| field.edit(window, cx));
        self.editor = Some(BlockEditor {
            id: row.id.clone(),
            field,
            _heard: [saved, watched],
        });
        self.list.remeasure();
        cx.notify();
    }

    #[expect(
        clippy::too_many_arguments,
        reason = "one save's parts, from one subscription"
    )]
    fn save_block(
        &mut self,
        field: Entity<InlineField>,
        id: String,
        kind: String,
        checked: Option<bool>,
        event: &SaveRequested,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let (request, value) = (event.request, event.value.clone());
        let (Some(edit), Some(revision)) = (edit_kind(&kind), self.revision.clone()) else {
            field.update(cx, |field, cx| {
                let refused = SaveFailure::Refused("This block can't be edited here.".into());
                field.complete_save(request, Err(refused), window, cx);
            });
            return;
        };
        let block = BlockEdit {
            block_id: id,
            kind: edit.into(),
            text: field_text(&value),
            checked,
        };
        let call = self.client.block(self.source.clone(), block, revision, cx);
        let task = cx.spawn_in(window, async move |view, cx| {
            let result = call.await;
            let _ = view.update_in(cx, |view, window, cx| {
                let landed = matches!(&result, Ok(receipt) if receipt.state != "uncertain");
                view.save_finished(&field, request, value, result, window, cx);
                if landed {
                    cx.emit(TaskDetailEvent::Reread);
                }
            });
        });
        self.saves.push(task);
    }

    pub(super) fn set_checked(
        &mut self,
        row: &BodyRow,
        checked: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(revision) = self.revision.clone() else {
            return;
        };
        self.checking = Some(row.id.clone());
        self.note = None;
        let block = BlockEdit {
            block_id: row.id.clone(),
            kind: "todo".into(),
            text: row.plain.clone(),
            checked: Some(checked),
        };
        let call = self.client.block(self.source.clone(), block, revision, cx);
        let task = cx.spawn_in(window, async move |view, cx| {
            let result = call.await;
            let _ = view.update_in(cx, |view, _, cx| {
                let (saved, after) = edit_outcome(result.as_ref());
                if let Err(SaveFailure::Refused(reason) | SaveFailure::Uncertain(reason)) = &saved {
                    view.note = Some(reason.clone());
                    view.checking = None;
                    view.list.remeasure();
                }
                let landed = saved.is_ok();
                view.follow_up(after, cx);
                if landed {
                    cx.emit(TaskDetailEvent::Reread);
                }
                cx.notify();
            });
        });
        self.saves.push(task);
    }

    /// Something is open for typing, so Escape belongs to it and rows may change height as it wraps.
    pub(super) fn any_editing(&self, cx: &App) -> bool {
        self.editor.is_some()
            || [&self.title, &self.status, &self.priority, &self.add_text]
                .iter()
                .any(|field| field.read(cx).editing())
    }
}

fn comment(value: &Value) -> Comment {
    let when = value["created_time"].as_str().unwrap_or_default();
    Comment {
        markdown: super::task_body::rich_markdown(&value["rich_text"]),
        when: when.get(..10).unwrap_or(when).to_owned(),
    }
}

impl Focusable for TaskDetailView {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

pub(super) fn muted_note(text: impl Into<SharedString>, cx: &App) -> Div {
    v_flex()
        .text_color(cx.theme().muted_foreground)
        .child(text.into())
}
