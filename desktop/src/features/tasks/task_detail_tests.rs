use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::{
    AppContext as _, Context, Entity, IntoElement, ParentElement as _, Render, Styled as _,
    Subscription, Window, div,
};
use serde_json::json;

use super::task_detail::{TaskDetailEvent, TaskDetailView, TaskSeed};
use super::task_export::*;
use super::tasks_state::TaskDetail;
use crate::inline_field::{FieldValue, InlineField, SaveRequest, SaveRequested};

fn source() -> NotionTaskRef {
    NotionTaskRef {
        data_source_id: "s".into(),
        page_id: "11111111-1111-1111-1111-111111111111".into(),
    }
}

fn detail(status: &str) -> TaskDetail {
    TaskDetail {
        source: source(),
        revision: "r2".into(),
        properties: json!({
            "Name":{"type":"title","title":[{"plain_text":"From Notion"}]},
            "Status":{"type":"status","status":{"name":status}},
            "Priority":{"type":"select","select":{"name":"High"}},
        }),
        blocks: vec![],
        comments: vec![],
        comments_unavailable: false,
    }
}

fn receipt(state: &str) -> NotionEditReceipt {
    NotionEditReceipt {
        page_id: source().page_id,
        revision: "r3".into(),
        state: state.into(),
        error: None,
    }
}

struct Harness {
    view: Entity<TaskDetailView>,
    rereads: Entity<usize>,
    requests: Entity<Vec<SaveRequest>>,
    _heard: Vec<Subscription>,
}

impl Render for Harness {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().size_full().child(self.view.clone())
    }
}

fn open(cx: &mut TestAppContext) -> (Entity<Harness>, &mut VisualTestContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        crate::inline_field::init(cx);
    });
    cx.add_window_view(|window, cx| {
        let seed = TaskSeed {
            title: "From the list".into(),
            status: "To Do".into(),
            priority: "High".into(),
            statuses: vec!["To Do".into(), "Done".into()],
            priorities: vec!["High".into()],
            ..TaskSeed::default()
        };
        let view = cx.new(|cx| TaskDetailView::new(source(), seed, window, cx));
        let rereads = cx.new(|_| 0usize);
        let requests = cx.new(|_| Vec::new());
        let status = view.read(cx).status.clone();
        let heard = vec![
            {
                let rereads = rereads.clone();
                cx.subscribe(&view, move |_, _, event: &TaskDetailEvent, cx| {
                    if matches!(event, TaskDetailEvent::Reread) {
                        rereads.update(cx, |count, _| *count += 1);
                    }
                })
            },
            {
                let requests = requests.clone();
                cx.subscribe(&status, move |_, _, event: &SaveRequested, cx| {
                    requests.update(cx, |list, _| list.push(event.request));
                })
            },
        ];
        Harness {
            view,
            rereads,
            requests,
            _heard: heard,
        }
    })
}

fn pick_done(
    harness: &Entity<Harness>,
    cx: &mut VisualTestContext,
) -> (Entity<InlineField>, SaveRequest) {
    let view = cx.read(|cx| harness.read(cx).view.clone());
    let status = cx.read(|cx| view.read(cx).status.clone());
    cx.update(|window, cx| {
        view.update(cx, |view, cx| {
            view.detail_arrived(&detail("To Do"), window, cx)
        });
        status.update(cx, |field, cx| field.pick_for_test("Done", window, cx));
    });
    // There's no backend here: each test answers the save itself.
    cx.update(|_, cx| view.update(cx, |view, _| view.saves.clear()));
    let request = cx.read(|cx| {
        *harness
            .read(cx)
            .requests
            .read(cx)
            .last()
            .expect("the pick saves")
    });
    (status, request)
}

#[gpui::test]
fn an_uncertain_edit_rereads_the_task_and_shows_what_notion_holds(cx: &mut TestAppContext) {
    let (harness, cx) = open(cx);
    let (status, request) = pick_done(&harness, cx);
    let view = cx.read(|cx| harness.read(cx).view.clone());
    cx.update(|window, cx| {
        view.update(cx, |view, cx| {
            let done = FieldValue::Choice(Some("Done".into()));
            view.save_finished(&status, request, done, Ok(receipt("uncertain")), window, cx);
        });
    });
    cx.run_until_parked();
    assert_eq!(cx.read(|cx| *harness.read(cx).rereads.read(cx)), 1);
    let shown = |cx: &VisualTestContext| cx.read(|cx| status.read(cx).committed().clone());
    assert_eq!(
        shown(cx),
        FieldValue::Choice(Some("To Do".into())),
        "nothing is claimed yet"
    );

    cx.update(|window, cx| {
        view.update(cx, |view, cx| {
            view.detail_arrived(&detail("Done"), window, cx)
        });
    });
    assert_eq!(
        shown(cx),
        FieldValue::Choice(Some("Done".into())),
        "Notion held the edit"
    );
    assert!(cx.read(|cx| !status.read(cx).pending()));
}

#[gpui::test]
fn a_refused_edit_rolls_back_to_what_was_saved_when_backed_out(cx: &mut TestAppContext) {
    let (harness, cx) = open(cx);
    let (status, request) = pick_done(&harness, cx);
    let view = cx.read(|cx| harness.read(cx).view.clone());
    cx.update(|window, cx| {
        view.update(cx, |view, cx| {
            let done = FieldValue::Choice(Some("Done".into()));
            let refused = Err(NotionError {
                code: "notion_http_403".into(),
                message: String::new(),
            });
            view.save_finished(&status, request, done, refused, window, cx);
        });
    });
    assert_eq!(
        cx.read(|cx| status.read(cx).committed().clone()),
        FieldValue::Choice(Some("To Do".into()))
    );
    assert_eq!(cx.read(|cx| *harness.read(cx).rereads.read(cx)), 0);
}

#[gpui::test]
fn a_confirmed_edit_takes_the_new_revision_and_value(cx: &mut TestAppContext) {
    let (harness, cx) = open(cx);
    let (status, request) = pick_done(&harness, cx);
    let view = cx.read(|cx| harness.read(cx).view.clone());
    cx.update(|window, cx| {
        view.update(cx, |view, cx| {
            let done = FieldValue::Choice(Some("Done".into()));
            view.save_finished(&status, request, done, Ok(receipt("confirmed")), window, cx);
        });
    });
    assert_eq!(
        cx.read(|cx| status.read(cx).committed().clone()),
        FieldValue::Choice(Some("Done".into()))
    );
    assert_eq!(cx.read(|cx| *harness.read(cx).rereads.read(cx)), 0);
}

#[gpui::test]
fn the_read_replaces_what_the_list_said(cx: &mut TestAppContext) {
    let (harness, cx) = open(cx);
    let view = cx.read(|cx| harness.read(cx).view.clone());
    let title = cx.read(|cx| view.read(cx).title.clone());
    assert_eq!(
        cx.read(|cx| title.read(cx).committed().clone()),
        FieldValue::Text("From the list".into())
    );
    cx.update(|window, cx| {
        view.update(cx, |view, cx| {
            view.detail_arrived(&detail("Done"), window, cx)
        });
    });
    assert_eq!(
        cx.read(|cx| title.read(cx).committed().clone()),
        FieldValue::Text("From Notion".into())
    );
}
