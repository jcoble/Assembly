//! One image's upload on the fake client: begin, a chunk a megabyte at a time, commit; a failure drops what began.

use std::rc::Rc;
use std::sync::Arc;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use serde_json::{Value, json};

use super::{CHUNK_BYTES, UploadSource, upload_image};
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::CallError;

async fn upload(
    client: &FakeClient,
    source: UploadSource,
    cx: &mut TestAppContext,
) -> Result<String, CallError> {
    let fake = client.clone();
    cx.update(|cx| upload_image(Rc::new(fake), "s1".into(), source, "image/png", cx))
        .await
}

fn methods(client: &FakeClient) -> Vec<String> {
    client.calls().into_iter().map(|(call, _)| call).collect()
}

// Pins: g0-session#24
#[gpui::test]
async fn an_image_goes_up_a_megabyte_at_a_time_and_answers_its_saved_path(cx: &mut TestAppContext) {
    let image: Arc<[u8]> = (0..CHUNK_BYTES * 2 + 300)
        .map(|i| (i % 251) as u8)
        .collect();
    let client = FakeClient::default();
    client.queue_answer(Ok(json!({ "upload": "u1" })));
    for _ in 0..3 {
        client.queue_answer(Ok(json!({ "bytes": 0 })));
    }
    client.queue_answer(Ok(json!({ "path": "/box/image.png" })));
    assert_eq!(
        upload(&client, UploadSource::Bytes(image.clone()), cx).await,
        Ok("/box/image.png".into())
    );
    let calls = client.calls();
    assert_eq!(
        calls[0],
        (
            "attachments/begin".into(),
            json!({ "session": "s1", "mime": "image/png", "size": image.len() })
        )
    );
    let mut sent = Vec::new();
    for (i, (call, args)) in calls[1..4].iter().enumerate() {
        assert_eq!(
            (call.as_str(), &args["upload"], &args["offset"]),
            ("attachments/chunk", &json!("u1"), &json!(i * CHUNK_BYTES))
        );
        sent.extend(STANDARD.decode(args["data"].as_str().unwrap()).unwrap());
    }
    assert_eq!(sent, &image[..]);
    assert_eq!(
        calls[4],
        ("attachments/commit".into(), json!({ "upload": "u1" }))
    );
}

// Pins: g0-session#24
#[gpui::test]
async fn a_failed_chunk_drops_the_upload_it_began_and_says_why(cx: &mut TestAppContext) {
    let client = FakeClient::default();
    client.queue_answer(Ok(json!({ "upload": "u1" })));
    client.queue_answer(Err(CallError::Failed("unknown upload".into())));
    client.queue_answer(Ok(Value::Null));
    let answer = upload(&client, UploadSource::Bytes(Arc::from(&b"hello"[..])), cx).await;
    assert_eq!(answer, Err(CallError::Failed("unknown upload".into())));
    assert_eq!(
        methods(&client),
        ["attachments/begin", "attachments/chunk", "attachments/drop"]
    );
    assert_eq!(client.calls()[2].1, json!({ "upload": "u1" }));
}

// Pins: g0-session#24
#[gpui::test]
async fn a_begin_turned_down_has_nothing_to_drop(cx: &mut TestAppContext) {
    let client = FakeClient::default();
    client.queue_answer(Err(CallError::BadArgs("not a supported image".into())));
    let answer = upload(&client, UploadSource::Bytes(Arc::from(&b"hello"[..])), cx).await;
    assert_eq!(
        answer,
        Err(CallError::BadArgs("not a supported image".into()))
    );
    assert_eq!(methods(&client), ["attachments/begin"]);
}

// Pins: g0-session#24
#[gpui::test]
async fn a_picked_file_is_read_off_the_main_thread_and_one_that_cant_be_read_says_so(
    cx: &mut TestAppContext,
) {
    let path = std::env::temp_dir().join(format!("g0-session-upload-{}.png", std::process::id()));
    std::fs::write(&path, b"png bytes").unwrap();
    let client = FakeClient::default();
    client.queue_answer(Ok(json!({ "upload": "u1" })));
    client.queue_answer(Ok(json!({ "bytes": 9 })));
    client.queue_answer(Ok(json!({ "path": "/box/a.png" })));
    assert_eq!(
        upload(&client, UploadSource::Path(path.clone()), cx).await,
        Ok("/box/a.png".into())
    );
    assert_eq!(client.calls()[0].1["size"], 9);
    std::fs::remove_file(&path).unwrap();
    let gone = upload(&client, UploadSource::Path(path), cx).await;
    assert!(
        matches!(gone, Err(CallError::Failed(message)) if message.starts_with("Couldn't read the image"))
    );
}
