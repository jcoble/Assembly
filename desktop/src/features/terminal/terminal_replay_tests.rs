use gpui::TestAppContext;

use super::{Terminals, following, open};

#[gpui::test]
async fn replay_stops_at_its_first_boundary_even_when_every_page_adds_output(
    cx: &mut TestAppContext,
) {
    let mut open = open(
        cx,
        Terminals {
            kept: b"abc".to_vec(),
            read_limit: Some(4),
            grow_reads: 4,
            ..Default::default()
        },
        false,
    )
    .await;
    open.until("bounded replay finished", |_, pane| following(pane))
        .await;
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(pane.grid.text(), "abcxyz");
        assert_eq!(pane.link.as_ref().unwrap().end, 6);
    });
    assert_eq!(open.terminals.lock().unwrap().of("read").len(), 2);
}
