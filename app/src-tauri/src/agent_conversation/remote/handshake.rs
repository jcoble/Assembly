//! How the desktop and a remote backend sign in to each other without the token crossing the tunnel.
//! Anything can be listening at either end of the SSH forward (another user on the remote's port 7777,
//! or on the desktop's forwarded port), so each side proves it knows the token by hashing it with a
//! random challenge from the other side using HMAC, the backend first. Neither proof can be reused.

use axum::extract::ws::{Message as AxumMessage, WebSocket};
use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};
use futures_util::{SinkExt, StreamExt};
use ring::hmac;
use tokio::io::{AsyncRead, AsyncWrite};
use tokio_tungstenite::{WebSocketStream, tungstenite::{client::IntoClientRequest, handshake::client::Request}};
use super::{PROTOCOL_VERSION, wire::{ClientFrame, ServerFrame, send_client_frame}};

pub(super) const CHALLENGE_HEADER: &str = "x-assembly-challenge";
const TIMEOUT: std::time::Duration = std::time::Duration::from_secs(15);

fn challenge() -> String { format!("{}{}", uuid::Uuid::new_v4().simple(), uuid::Uuid::new_v4().simple()) }
pub(super) fn valid_challenge(value: &str) -> bool { value.len() == 64 && value.bytes().all(|c| c.is_ascii_hexdigit()) }
fn proof_input(domain: &str, client: &str, server: &str) -> String { format!("assembly/{domain}/v{PROTOCOL_VERSION}\0{client}\0{server}") }
fn proof(token: &str, domain: &str, client: &str, server: &str) -> String {
    BASE64.encode(hmac::sign(&hmac::Key::new(hmac::HMAC_SHA256, token.as_bytes()), proof_input(domain, client, server).as_bytes()).as_ref())
}
fn verified(token: &str, domain: &str, client: &str, server: &str, candidate: &str) -> bool {
    BASE64.decode(candidate).is_ok_and(|bytes| hmac::verify(&hmac::Key::new(hmac::HMAC_SHA256, token.as_bytes()),
        proof_input(domain, client, server).as_bytes(), &bytes).is_ok())
}

pub(super) fn upgrade_request(url: &str) -> Result<(Request, String), String> {
    let mut request = url.into_client_request().map_err(|error| format!("Invalid remote URL: {error}"))?;
    let nonce = challenge();
    request.headers_mut().insert(CHALLENGE_HEADER, nonce.parse().unwrap());
    Ok((request, nonce))
}

/// Authenticate before any command or subscription is admitted.
pub(super) async fn admit_desktop(socket: &mut WebSocket, token: &str, client: &str) -> bool {
    tokio::time::timeout(TIMEOUT, async {
        let server = challenge();
        let ready = ServerFrame::Ready { protocol_version: PROTOCOL_VERSION, proof: proof(token, "server", client, &server), challenge: server.clone() };
        if socket.send(AxumMessage::Text(serde_json::to_string(&ready).unwrap().into())).await.is_err() { return false; }
        let Some(Ok(AxumMessage::Text(text))) = socket.next().await else { return false; };
        let Ok(ClientFrame::Authenticate { proof }) = serde_json::from_str(&text) else { return false; };
        verified(token, "client", client, &server, &proof)
    }).await.unwrap_or(false)
}

pub(super) async fn sign_in<S: AsyncRead + AsyncWrite + Unpin>(socket: &mut WebSocketStream<S>, token: &str, client: &str) -> Result<(), String> {
    tokio::time::timeout(TIMEOUT, async {
        let hello = socket.next().await.ok_or("Backend closed before protocol readiness")?.map_err(|error| error.to_string())?;
        let value: serde_json::Value = serde_json::from_str(hello.to_text().map_err(|error| error.to_string())?).map_err(|error| error.to_string())?;
        let version = value["protocol_version"].as_u64().ok_or("Remote backend needs an update: no protocol readiness")?;
        if version != u64::from(PROTOCOL_VERSION) {
            return Err(if version > u64::from(PROTOCOL_VERSION) { format!("This Assembly client needs an update: backend protocol {version}, client protocol {PROTOCOL_VERSION}") }
                else { format!("Remote backend needs an update: protocol {version}, this Assembly requires {PROTOCOL_VERSION}") });
        }
        let ServerFrame::Ready { proof: candidate, challenge: server, .. } = serde_json::from_value(value).map_err(|_| "Backend did not prove it knows the token")?
            else { return Err("Backend did not announce protocol readiness".into()); };
        if !valid_challenge(&server) || !verified(token, "server", client, &server, &candidate) {
            return Err("Backend did not prove it knows the token".into());
        }
        send_client_frame(socket, &ClientFrame::Authenticate { proof: proof(token, "client", client, &server) }).await
    }).await.unwrap_or_else(|_| Err("Backend readiness timed out".into()))
}

#[cfg(test)]
pub(super) async fn test_backend(stream: tokio::net::TcpStream, version: u16) -> WebSocketStream<tokio::net::TcpStream> {
    let mut client = String::new();
    let mut socket = tokio_tungstenite::accept_hdr_async(stream, |request: &tokio_tungstenite::tungstenite::handshake::server::Request, response| {
        client = request.headers()[CHALLENGE_HEADER].to_str().unwrap().to_string();
        Ok(response)
    }).await.unwrap();
    let server = challenge();
    let frame = ServerFrame::Ready { protocol_version: version, proof: proof("test-token", "server", &client, &server), challenge: server.clone() };
    socket.send(tokio_tungstenite::tungstenite::Message::Text(serde_json::to_string(&frame).unwrap().into())).await.unwrap();
    if version == PROTOCOL_VERSION {
        let frame = socket.next().await.unwrap().unwrap();
        let ClientFrame::Authenticate { proof } = serde_json::from_str(frame.to_text().unwrap()).unwrap() else { panic!("authenticate first"); };
        assert!(verified("test-token", "client", &client, &server, &proof));
    }
    socket
}

#[cfg(test)]
pub(super) async fn test_client(url: &str) -> WebSocketStream<tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>> {
    let (request, nonce) = upgrade_request(url).unwrap();
    let (mut socket, _) = tokio_tungstenite::connect_async(request).await.unwrap();
    sign_in(&mut socket, "test-token", &nonce).await.unwrap();
    send_client_frame(&mut socket, &ClientFrame::Request { id: 0, command: super::wire::RemoteCommand::ListSessions }).await.unwrap();
    socket.next().await.unwrap().unwrap();
    socket
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::AtomicBool;
    use std::sync::Arc;

    use axum::extract::ws::WebSocketUpgrade;
    use axum::http::HeaderMap;
    use axum::{routing::get, Router};
    use futures_util::{SinkExt, StreamExt};
    use mcb_core::session_store::SessionStore;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;
    use tokio::sync::{mpsc, oneshot};
    use tokio_tungstenite::tungstenite::{handshake::server::Request, Message};

    use super::*;
    use super::super::wire::send_client_frame;

    async fn first_connection_error(url: String) -> String {
        let (_requests, mut receiver) = mpsc::channel(1);
        let (reply, result) = oneshot::channel();
        let history = (super::super::RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}),
            Arc::new(SessionStore::open_in_memory().unwrap())).unwrap(), "test".to_string());
        super::super::client_loop(url, "test-token".into(), &mut receiver, Arc::new(|_| Ok(())), None, Some(1),
            Arc::new(AtomicBool::new(false)), &mut Some(reply), Arc::new(|| {}), None, history).await;
        result.await.unwrap().unwrap_err()
    }

    #[test]
    fn proofs_cannot_be_reflected_or_replayed() {
        let client = challenge();
        let server = challenge();
        let candidate = proof("test-token", "server", &client, &server);
        assert!(verified("test-token", "server", &client, &server, &candidate));
        assert!(!verified("test-token", "client", &client, &server, &candidate));
        assert!(!verified("test-token", "server", &challenge(), &server, &candidate));
        assert!(!verified("test-token", "server", &client, &challenge(), &candidate));
    }

    #[tokio::test]
    async fn backend_admits_only_a_desktop_that_answers_its_challenge() {
        let (admitted, mut verdicts) = mpsc::unbounded_channel();
        let app = Router::new().route("/", get(move |upgrade: WebSocketUpgrade, headers: HeaderMap| {
            let admitted = admitted.clone();
            async move {
                let challenge = headers[CHALLENGE_HEADER].to_str().unwrap().to_string();
                upgrade.on_upgrade(move |mut socket| async move {
                    admitted.send(admit_desktop(&mut socket, "test-token", &challenge).await).unwrap();
                })
            }
        }));
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        for forged in [None, Some("forged"), Some("command")] {
            let (request, challenge) = upgrade_request(&format!("ws://{address}/")).unwrap();
            let (mut socket, _) = tokio_tungstenite::connect_async(request).await.unwrap();
            match forged {
                // Signing in also checks the backend's own proof.
                None => sign_in(&mut socket, "test-token", &challenge).await.unwrap(),
                Some(proof) => {
                    socket.next().await.unwrap().unwrap();
                    let frame = if proof == "command" { ClientFrame::Request { id: 1, command: super::super::wire::RemoteCommand::ListSessions } }
                        else { ClientFrame::Authenticate { proof: proof.into() } };
                    send_client_frame(&mut socket, &frame).await.unwrap();
                }
            }
            assert_eq!(verdicts.recv().await.unwrap(), forged.is_none());
        }
        server.abort();
    }

    #[tokio::test]
    async fn desktop_never_answers_a_listener_that_cannot_prove_the_token() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let squatter = tokio::spawn(async move {
            let (stream, _) = listener.accept().await.unwrap();
            let mut headers = String::new();
            let mut socket = tokio_tungstenite::accept_hdr_async(stream, |request: &Request, response| {
                headers = format!("{:?}", request.headers());
                Ok(response)
            }).await.unwrap();
            let ready = ServerFrame::Ready { protocol_version: PROTOCOL_VERSION, proof: "forged".into(), challenge: "0".repeat(64) };
            socket.send(Message::Text(serde_json::to_string(&ready).unwrap().into())).await.unwrap();
            (headers, socket.next().await)
        });
        let error = first_connection_error(format!("ws://{address}/assembly")).await;
        assert!(error.contains("did not prove"), "{error}");
        let (headers, answer) = squatter.await.unwrap();
        assert!(!headers.contains("test-token"), "{headers}");
        assert!(!matches!(answer, Some(Ok(Message::Text(_)))), "the desktop must not answer: {answer:?}");
    }

    #[tokio::test]
    async fn a_backend_that_still_wants_the_token_asks_for_an_update() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let older = tokio::spawn(async move {
            let (mut stream, _) = listener.accept().await.unwrap();
            let _ = stream.read(&mut [0; 4096]).await.unwrap();
            stream.write_all(b"HTTP/1.1 401 Unauthorized\r\ncontent-length: 0\r\n\r\n").await.unwrap();
        });
        let error = first_connection_error(format!("ws://{address}/assembly")).await;
        assert!(error.contains("Remote backend needs an update"), "{error}");
        older.await.unwrap();
    }
}
