use super::{Gate, PictureClient, PictureInterest, Pictures, agent, register};
use gpui_kit::http_client::{AsyncBody, HttpClient, http};
use std::future::Future;
use std::sync::Arc;
use std::task::{Context, Poll, Waker};

fn client() -> (PictureClient, Arc<Gate>) {
    let gate = Arc::new(Gate::default());
    (
        PictureClient {
            agent: agent(),
            gate: gate.clone(),
        },
        gate,
    )
}
fn request(method: http::Method, uri: &str) -> http::Request<AsyncBody> {
    http::Request::builder()
        .method(method)
        .uri(uri)
        .body(AsyncBody::from(()))
        .unwrap()
}
#[test]
fn anything_but_a_person_picture_is_refused_at_once_saying_no_address() {
    let (client, gate) = client();
    gate.watch();
    for refused in [
        request(http::Method::GET, "https://example.com/tracker.png"),
        request(http::Method::GET, "http://github.com/octocat.png?size=40"),
        request(http::Method::POST, "https://github.com/octocat.png?size=40"),
    ] {
        let error = futures_executor::block_on(client.send(refused))
            .err()
            .expect("refused");
        let said = format!("{error:?}");
        assert!(
            !said.contains("example.com") && !said.contains("github.com"),
            "{said}"
        );
    }
}
#[test]
fn with_no_screen_watching_a_picture_load_never_starts() {
    let (client, _gate) = client();
    let mut load = client.send(request(
        http::Method::GET,
        "https://github.com/octocat.png?size=40",
    ));
    let polled = load.as_mut().poll(&mut Context::from_waker(Waker::noop()));
    assert!(matches!(polled, Poll::Pending));
}
#[gpui::test]
fn holding_interest_is_what_lets_pictures_load(cx: &mut gpui::TestAppContext) {
    cx.update(|cx| {
        assert!(PictureInterest::hold(cx).is_none(), "no client registered");
        register(cx);
        let gate = cx.global::<Pictures>().0.clone();
        assert!(!gate.wanted());
        let held = PictureInterest::hold(cx).expect("registered");
        assert!(gate.wanted());
        drop(held);
        assert!(!gate.wanted());
    });
}
