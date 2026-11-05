use std::cell::RefCell;
use std::rc::Rc;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::{ClipboardItem, Image, ImageFormat, TestAppContext, VisualTestContext};
use gpui_kit::{
    AppContext as _, Context, Entity, Focusable as _, IntoElement, ParentElement as _, Render,
    Styled as _, Subscription, Task, Window, div,
};

use super::super::super::task_export::*;
use super::super::{TaskDetailEvent, TaskDetailView, TaskSeed};
use super::{ImageCalls, MAX_BYTES, TOO_LARGE, WRONG_KIND, accepted};

/// The backend, answering at once. It records every call.
#[derive(Default)]
struct Backend {
    log: RefCell<Vec<String>>,
    sent: RefCell<Vec<u8>>,
    fail_chunk_at: Option<i64>,
    fail_begin: Option<&'static str>,
    commit_state: Option<&'static str>,
}

impl ImageCalls for Backend {
    fn begin(
        &self,
        _: NotionTaskRef,
        mime: String,
        size: i64,
        _: &mut gpui_kit::App,
    ) -> Task<Result<NotionImageLease, NotionError>> {
        self.log.borrow_mut().push(format!("begin {mime} {size}"));
        if let Some(code) = self.fail_begin {
            return Task::ready(Err(NotionError {
                code: code.into(),
                message: code.into(),
            }));
        }
        Task::ready(Ok(NotionImageLease {
            upload_id: "u".into(),
            chunk_bytes: 4,
        }))
    }
    fn chunk(
        &self,
        _: String,
        offset: i64,
        data: String,
        _: &mut gpui_kit::App,
    ) -> Task<Result<NotionEmpty, NotionError>> {
        self.log.borrow_mut().push(format!("chunk {offset}"));
        if self.fail_chunk_at == Some(offset) {
            return Task::ready(Err(NotionError {
                code: "disconnected".into(),
                message: String::new(),
            }));
        }
        self.sent
            .borrow_mut()
            .extend(STANDARD.decode(data).expect("base64"));
        Task::ready(Ok(NotionEmpty {}))
    }
    fn commit(
        &self,
        _: String,
        _: &mut gpui_kit::App,
    ) -> Task<Result<NotionEditReceipt, NotionError>> {
        self.log.borrow_mut().push("commit".into());
        Task::ready(Ok(NotionEditReceipt {
            page_id: "p".into(),
            revision: "r".into(),
            state: self.commit_state.unwrap_or("confirmed").into(),
            error: None,
        }))
    }
    fn abandon(&self, _: String, _: &mut gpui_kit::App) -> Task<Result<NotionEmpty, NotionError>> {
        self.log.borrow_mut().push("drop".into());
        Task::ready(Ok(NotionEmpty {}))
    }
}

struct Harness {
    view: Entity<TaskDetailView>,
    rereads: Entity<usize>,
    _heard: Subscription,
}

impl Render for Harness {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().size_full().child(self.view.clone())
    }
}

fn open(
    backend: Backend,
    cx: &mut TestAppContext,
) -> (
    Entity<TaskDetailView>,
    Entity<usize>,
    Rc<Backend>,
    &mut VisualTestContext,
) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        crate::inline_field::init(cx);
    });
    let backend = Rc::new(backend);
    let calls = backend.clone();
    let (harness, cx) = cx.add_window_view(|window, cx| {
        let source = NotionTaskRef {
            data_source_id: "s".into(),
            page_id: "p".into(),
        };
        let view = cx.new(|cx| {
            let mut view = TaskDetailView::new(source, TaskSeed::default(), window, cx);
            view.images.calls = calls;
            view
        });
        let rereads = cx.new(|_| 0usize);
        let counted = rereads.clone();
        let heard = cx.subscribe(&view, move |_, _, event: &TaskDetailEvent, cx| {
            if matches!(event, TaskDetailEvent::Reread) {
                counted.update(cx, |count, _| *count += 1);
            }
        });
        Harness {
            view,
            rereads,
            _heard: heard,
        }
    });
    let (view, rereads) = cx.read(|cx| {
        let harness = harness.read(cx);
        (harness.view.clone(), harness.rereads.clone())
    });
    (view, rereads, backend, cx)
}

fn picture(bytes: &[u8]) -> ClipboardItem {
    ClipboardItem::new_image(&Image::from_bytes(ImageFormat::Png, bytes.to_vec()))
}

fn paste(view: &Entity<TaskDetailView>, item: &ClipboardItem, cx: &mut VisualTestContext) -> bool {
    let taken =
        cx.update(|window, cx| view.update(cx, |view, cx| view.paste_item(item, window, cx)));
    cx.run_until_parked();
    taken
}

fn failure(view: &Entity<TaskDetailView>, cx: &VisualTestContext) -> Option<String> {
    cx.read(|cx| view.read(cx).images.failure.clone())
}

#[gpui::test]
fn a_pasted_picture_reaches_the_backend_whole_and_the_page_is_read_again(cx: &mut TestAppContext) {
    let (view, rereads, backend, cx) = open(Backend::default(), cx);
    let bytes = b"0123456789";
    assert!(paste(&view, &picture(bytes), cx));
    assert_eq!(
        *backend.log.borrow(),
        [
            "begin image/png 10",
            "chunk 0",
            "chunk 4",
            "chunk 8",
            "commit"
        ]
    );
    assert_eq!(*backend.sent.borrow(), bytes);
    assert_eq!(cx.read(|cx| *rereads.read(cx)), 1);
    assert_eq!(failure(&view, cx), None);
    assert!(cx.read(|cx| !view.read(cx).images.sending()));
}

#[gpui::test]
fn the_paste_key_on_the_page_adds_the_picture_on_the_clipboard(cx: &mut TestAppContext) {
    let (view, _, backend, cx) = open(Backend::default(), cx);
    cx.write_to_clipboard(picture(b"png!"));
    cx.update(|window, cx| {
        let focus = view.read(cx).focus_handle(cx);
        focus.focus(window, cx);
    });
    cx.run_until_parked();
    cx.simulate_keystrokes("secondary-v");
    cx.run_until_parked();
    assert_eq!(*backend.sent.borrow(), b"png!");
}

#[gpui::test]
fn pasted_text_on_the_page_is_not_taken(cx: &mut TestAppContext) {
    let (view, rereads, backend, cx) = open(Backend::default(), cx);
    assert!(!paste(
        &view,
        &ClipboardItem::new_string("words".into()),
        cx
    ));
    assert!(backend.log.borrow().is_empty());
    assert_eq!(cx.read(|cx| *rereads.read(cx)), 0);
}

#[gpui::test]
fn the_add_text_box_hands_a_pasted_picture_to_the_page(cx: &mut TestAppContext) {
    let (view, _, backend, cx) = open(Backend::default(), cx);
    let add_text = cx.read(|cx| view.read(cx).add_text.clone());
    let pastes = cx.read(|cx| add_text.read(cx).pastes_for_test());
    let taken = cx.update(|window, cx| pastes(&picture(b"png!"), window, cx));
    cx.run_until_parked();
    assert!(taken);
    assert_eq!(*backend.sent.borrow(), b"png!");
}

#[gpui::test]
fn the_add_text_box_keeps_pasted_words(cx: &mut TestAppContext) {
    let (view, _, backend, cx) = open(Backend::default(), cx);
    let add_text = cx.read(|cx| view.read(cx).add_text.clone());
    let pastes = cx.read(|cx| add_text.read(cx).pastes_for_test());
    let words = ClipboardItem::new_string("words".into());
    let taken = cx.update(|window, cx| pastes(&words, window, cx));
    assert!(!taken);
    assert!(backend.log.borrow().is_empty());
}

#[gpui::test]
fn pictures_go_one_at_a_time_in_the_order_they_came(cx: &mut TestAppContext) {
    let (view, rereads, backend, cx) = open(Backend::default(), cx);
    cx.update(|window, cx| {
        view.update(cx, |view, cx| {
            view.paste_item(&picture(b"first"), window, cx);
            view.paste_item(&picture(b"second!"), window, cx);
        });
    });
    cx.run_until_parked();
    let log = backend.log.borrow();
    let order: Vec<&str> = log
        .iter()
        .map(String::as_str)
        .filter(|line| line.starts_with("begin") || *line == "commit")
        .collect();
    assert_eq!(
        order,
        ["begin image/png 5", "commit", "begin image/png 7", "commit"]
    );
    assert_eq!(*backend.sent.borrow(), b"firstsecond!");
    assert_eq!(cx.read(|cx| *rereads.read(cx)), 2);
}

#[gpui::test]
fn a_chunk_that_fails_drops_the_upload_and_never_commits(cx: &mut TestAppContext) {
    let backend = Backend {
        fail_chunk_at: Some(4),
        ..Backend::default()
    };
    let (view, rereads, backend, cx) = open(backend, cx);
    paste(&view, &picture(b"0123456789"), cx);
    assert_eq!(
        *backend.log.borrow(),
        ["begin image/png 10", "chunk 0", "chunk 4", "drop"]
    );
    assert_eq!(
        failure(&view, cx).as_deref(),
        Some("Assembly couldn't reach Notion."),
        "nothing reached the page, so this says plainly it didn't work"
    );
    assert_eq!(cx.read(|cx| *rereads.read(cx)), 0);
}

#[gpui::test]
fn a_refused_picture_says_why_and_the_next_one_still_goes(cx: &mut TestAppContext) {
    let backend = Backend {
        fail_begin: Some("notion_source_mismatch"),
        ..Backend::default()
    };
    let (view, _, backend, cx) = open(backend, cx);
    paste(&view, &picture(b"one"), cx);
    assert_eq!(
        failure(&view, cx).as_deref(),
        Some("That page isn't in the connected task database.")
    );
    paste(&view, &picture(b"two"), cx);
    assert_eq!(backend.log.borrow().len(), 2);
}

#[gpui::test]
fn an_uncertain_commit_says_so_and_rereads_the_page(cx: &mut TestAppContext) {
    let backend = Backend {
        commit_state: Some("uncertain"),
        ..Backend::default()
    };
    let (view, rereads, _, cx) = open(backend, cx);
    paste(&view, &picture(b"png!"), cx);
    assert!(failure(&view, cx).is_some_and(|text| text.contains("can't tell")));
    assert_eq!(cx.read(|cx| *rereads.read(cx)), 1);
}

#[gpui::test]
fn a_kind_notion_refuses_is_turned_away_before_any_call(cx: &mut TestAppContext) {
    let (view, _, backend, cx) = open(Backend::default(), cx);
    let tiff = ClipboardItem::new_image(&Image::from_bytes(ImageFormat::Tiff, vec![1, 2]));
    assert!(paste(&view, &tiff, cx));
    assert!(backend.log.borrow().is_empty());
    assert_eq!(failure(&view, cx).as_deref(), Some(WRONG_KIND));
}

#[test]
fn a_picture_over_the_limit_is_turned_away() {
    let at_limit: std::sync::Arc<[u8]> = vec![0; MAX_BYTES].into();
    assert!(accepted(Some("image/png"), at_limit).is_ok());
    let over: std::sync::Arc<[u8]> = vec![0; MAX_BYTES + 1].into();
    assert_eq!(accepted(Some("image/png"), over).unwrap_err(), TOO_LARGE);
}
