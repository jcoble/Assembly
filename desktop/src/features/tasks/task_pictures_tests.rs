use std::sync::{Arc, Mutex};

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::{
    Bounds, DevicePixels, Image, ImageFormat, Pixels, TestAppContext, VisualTestContext, px, size,
};
use gpui_kit::{
    AppContext as _, Context, Entity, IntoElement, ParentElement as _, Render, Styled as _, Window,
    div,
};
use serde_json::json;

use super::super::super::task_export::*;
use super::super::super::tasks_state::TaskDetail;
use super::super::{TaskDetailView, TaskSeed};
use super::{MOST_AT_ONCE, PictureState, TALLEST, largest, place};

/// One pixel.
const PNG: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

fn signed(path: &str, signature: &str) -> String {
    format!("https://files.example.com/{path}?X-Amz-Signature={signature}")
}

fn image_block(id: &str, url: &str) -> NotionBlock {
    NotionBlock {
        id: id.into(),
        kind: "image".into(),
        content: json!({"type":"file","file":{"url":url},"caption":[]}),
        children: vec![],
    }
}

fn page(blocks: Vec<NotionBlock>) -> TaskDetail {
    TaskDetail {
        source: NotionTaskRef {
            data_source_id: "s".into(),
            page_id: "p".into(),
        },
        revision: "r".into(),
        properties: json!({}),
        blocks,
        comments: vec![],
        comments_unavailable: false,
    }
}

struct Harness {
    view: Entity<TaskDetailView>,
}

impl Render for Harness {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().size_full().child(self.view.clone())
    }
}

type Asked = Arc<Mutex<Vec<String>>>;

/// A page whose pictures read from a fake that records each address, and answers unless the address has "broken" in it.
fn open(cx: &mut TestAppContext) -> (Entity<TaskDetailView>, Asked, &mut VisualTestContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        crate::inline_field::init(cx);
    });
    let asked: Asked = Arc::default();
    let log = asked.clone();
    let (harness, cx) = cx.add_window_view(|window, cx| {
        let source = NotionTaskRef {
            data_source_id: "s".into(),
            page_id: "p".into(),
        };
        let view = cx.new(|cx| {
            let mut view = TaskDetailView::new(source, TaskSeed::default(), window, cx);
            view.pictures.read = Arc::new(move |url, _| {
                log.lock().unwrap().push(url.to_owned());
                (!url.contains("broken")).then(|| {
                    Image::from_bytes(ImageFormat::Png, STANDARD.decode(PNG).expect("base64"))
                })
            });
            view
        });
        Harness { view }
    });
    let view = cx.read(|cx| harness.read(cx).view.clone());
    (view, asked, cx)
}

fn show(view: &Entity<TaskDetailView>, blocks: Vec<NotionBlock>, cx: &mut VisualTestContext) {
    cx.update(|window, cx| {
        view.update(cx, |view, cx| {
            view.detail_arrived(&page(blocks), window, cx)
        });
        window.refresh();
    });
    settle(cx);
}

/// Draws and lets the reads finish until no read is left to start: each read's end redraws, and rows then ask again.
fn settle(cx: &mut VisualTestContext) {
    for _ in 0..8 {
        cx.run_until_parked();
        cx.update(|window, cx| window.draw(cx).clear(cx));
        cx.run_until_parked();
    }
}

fn state(view: &Entity<TaskDetailView>, block: &str, cx: &VisualTestContext) -> &'static str {
    cx.read(|cx| match view.read(cx).pictures.state(block) {
        PictureState::Coming => "coming",
        PictureState::Shown(_) => "shown",
        PictureState::Unreadable => "unreadable",
    })
}

#[gpui::test]
fn a_picture_on_the_page_is_read_and_shown_where_it_sits(cx: &mut TestAppContext) {
    let (view, asked, cx) = open(cx);
    show(&view, vec![image_block("a", &signed("a.png", "1"))], cx);
    assert_eq!(state(&view, "a", cx), "shown");
    assert_eq!(*asked.lock().unwrap(), [signed("a.png", "1")]);
}

#[gpui::test]
fn a_new_signature_on_the_same_file_isnt_read_again(cx: &mut TestAppContext) {
    let (view, asked, cx) = open(cx);
    show(&view, vec![image_block("a", &signed("a.png", "1"))], cx);
    show(&view, vec![image_block("a", &signed("a.png", "2"))], cx);
    assert_eq!(state(&view, "a", cx), "shown");
    assert_eq!(asked.lock().unwrap().len(), 1);
}

#[gpui::test]
fn a_different_file_in_the_same_block_is_read(cx: &mut TestAppContext) {
    let (view, asked, cx) = open(cx);
    show(&view, vec![image_block("a", &signed("a.png", "1"))], cx);
    show(&view, vec![image_block("a", &signed("b.png", "2"))], cx);
    assert_eq!(state(&view, "a", cx), "shown");
    assert_eq!(asked.lock().unwrap().len(), 2);
}

#[gpui::test]
fn a_picture_that_cant_be_read_stays_unread_until_its_address_changes(cx: &mut TestAppContext) {
    let (view, asked, cx) = open(cx);
    let broken = signed("broken.png", "1");
    show(&view, vec![image_block("a", &broken)], cx);
    assert_eq!(state(&view, "a", cx), "unreadable");
    show(&view, vec![image_block("a", &broken)], cx);
    assert_eq!(asked.lock().unwrap().len(), 1, "no asking again and again");
    show(
        &view,
        vec![image_block("a", &signed("broken.png", "2"))],
        cx,
    );
    assert_eq!(
        asked.lock().unwrap().len(),
        2,
        "a newly signed address gets a go"
    );
}

#[gpui::test]
fn a_picture_whose_block_left_the_page_is_let_go(cx: &mut TestAppContext) {
    let (view, _, cx) = open(cx);
    show(&view, vec![image_block("a", &signed("a.png", "1"))], cx);
    show(&view, vec![], cx);
    assert_eq!(cx.read(|cx| view.read(cx).pictures.slots.len()), 0);
}

#[gpui::test]
fn many_pictures_all_arrive_though_only_a_few_read_at_once(cx: &mut TestAppContext) {
    let (view, asked, cx) = open(cx);
    cx.simulate_resize(size(px(800.), px(3000.)));
    let count = MOST_AT_ONCE + 2;
    let blocks = (0..count)
        .map(|n| image_block(&format!("b{n}"), &signed(&format!("{n}.png"), "1")))
        .collect();
    cx.update(|window, cx| {
        view.update(cx, |view, cx| {
            view.detail_arrived(&page(blocks), window, cx)
        });
        window.draw(cx).clear(cx);
        assert_eq!(view.read(cx).pictures.reading(), MOST_AT_ONCE);
    });
    settle(cx);
    assert_eq!(asked.lock().unwrap().len(), count);
    for n in 0..count {
        assert_eq!(state(&view, &format!("b{n}"), cx), "shown");
    }
}

#[test]
fn only_a_signature_is_ignored_in_an_address() {
    assert_eq!(place(&signed("a.png", "1")), place(&signed("a.png", "2")));
    assert_ne!(
        place("https://x.example/a?id=1"),
        place("https://x.example/a?id=2")
    );
}

#[test]
fn a_picture_never_grows_and_stops_at_the_old_pages_height() {
    let natural = |width, height| size(DevicePixels(width), DevicePixels(height));
    assert_eq!(largest(natural(100, 50)), size(px(100.), px(50.)));
    assert_eq!(
        largest(natural(1000, 2000)),
        size(px(TALLEST / 2.), px(TALLEST))
    );
    assert_eq!(largest(natural(0, 0)), size(px(0.), px(0.)));
}

const WIDE: &str = "iVBORw0KGgoAAAANSUhEUgAAAMgAAABkCAYAAADDhn8LAAAA3klEQVR42u3TMQ0AAAzDsEIv843A3n0+TCBS0naAW0QAg4BBwCBgEDAIGAQMAgYBgwAGAYOAQcAgYBAwCBgEDAIGAYMIAQYBg4BBwCBgEDAIGAQMAgYBDAIGAYOAQcAgYBAwCBgEDAIGAQwCBgGDgEHAIGAQMAgYBAwCGAQMAgYBg4BBwCBgEDAIGAQMAhgEDAIGAYOAQcAgYBAwCBgEMAgYBAwCBgGDgEHAIGAQMAgYBDAIGAQMAgYBg4BBwCBgEDAIGEQEMAgYBAwCBgGDgEHAIGAQMAhgEDAIGAQ+LWT2DU59s4jqAAAAAElFTkSuQmCC";
const TALL: &str = "iVBORw0KGgoAAAANSUhEUgAAACgAAAJYCAYAAAAQQ6SFAAABk0lEQVR42u3OsQ0AAAgDoJ7ez/UNYxjYSdu5LIKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKfggvDsQ/32oKWIwAAAABJRU5ErkJggg==";

/// Opens a page holding one picture and reads where it drew.
fn drawn<'a>(
    bytes: &'static str,
    cx: &'a mut TestAppContext,
) -> (&'a mut VisualTestContext, Bounds<Pixels>) {
    let (view, _, cx) = open(cx);
    cx.update(|_, cx| {
        view.update(cx, |view, _| {
            view.pictures.read = Arc::new(move |_, _| {
                let bytes = STANDARD.decode(bytes).expect("base64");
                Some(Image::from_bytes(ImageFormat::Png, bytes))
            });
        })
    });
    show(&view, vec![image_block("a", &signed("a.png", "1"))], cx);
    let bounds = cx.debug_bounds("picture-a").expect("drawn");
    (cx, bounds)
}

#[gpui::test]
fn a_picture_with_room_is_its_own_size(cx: &mut TestAppContext) {
    let (_, bounds) = drawn(WIDE, cx);
    assert_eq!(bounds.size, size(px(200.), px(100.)));
}

#[gpui::test]
fn a_tall_picture_stops_at_the_old_pages_height_and_keeps_its_shape(cx: &mut TestAppContext) {
    let (_, bounds) = drawn(TALL, cx);
    assert_eq!(bounds.size, size(px(32.), px(TALLEST)));
}

#[gpui::test]
fn a_picture_wider_than_the_page_fits_across_and_follows_the_window(cx: &mut TestAppContext) {
    let (cx, _) = drawn(WIDE, cx);
    cx.simulate_resize(size(px(150.), px(600.)));
    settle(cx);
    let narrow = cx.debug_bounds("picture-a").expect("drawn");
    assert!(narrow.size.width < px(150.), "{narrow:?}");
    assert!(
        (narrow.size.width / narrow.size.height - 2.).abs() < 0.01,
        "still twice as wide as tall: {narrow:?}"
    );
    cx.simulate_resize(size(px(800.), px(600.)));
    settle(cx);
    let wide = cx.debug_bounds("picture-a").expect("drawn");
    assert_eq!(wide.size, size(px(200.), px(100.)));
}

#[gpui::test]
fn a_picture_below_the_screen_isnt_read_until_it_comes_on(cx: &mut TestAppContext) {
    let (view, asked, cx) = open(cx);
    cx.simulate_resize(size(px(800.), px(300.)));
    let tall = |n: usize| NotionBlock {
        id: format!("t{n}"),
        kind: "image".into(),
        content: json!({"type":"file","file":{"url":signed(&format!("{n}.png"), "1")},"caption":[]}),
        children: vec![],
    };
    cx.update(|_, cx| {
        view.update(cx, |view, _| {
            view.pictures.read = Arc::new({
                let asked = asked.clone();
                move |url, _| {
                    asked.lock().unwrap().push(url.to_owned());
                    let bytes = STANDARD.decode(TALL).expect("base64");
                    Some(Image::from_bytes(ImageFormat::Png, bytes))
                }
            });
        })
    });
    show(&view, (0..40).map(tall).collect(), cx);
    let read = asked.lock().unwrap().len();
    assert!(read < 40, "only what's near the screen: {read}");
    let last = cx.read(|cx| view.read(cx).rows.len());
    cx.update(|window, cx| {
        view.read(cx).list.scroll_to(gpui::ListOffset {
            item_ix: last,
            offset_in_item: px(0.),
        });
        window.refresh();
    });
    settle(cx);
    assert_eq!(state(&view, "t39", cx), "shown", "scrolling there reads it");
}
