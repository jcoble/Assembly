//! The window's one web client, for people's pictures on the Pull requests screens and nothing else: public GitHub
//! avatars over HTTPS, read with no credentials, cookies, referrer or proxy. `rules.rs` says what may be asked for;
//! everything else the window tries to load from the web, such as an image in markdown, is refused at once.
//! GPUI keeps each picture's outcome by address until the app restarts, a failure included, so the initials then stay.
//! A picture on a task's page doesn't go through the window: its address can be anywhere and is signed, so GPUI's client,
//! which sees only an address, can't tell it from an image in markdown. Tasks asks `read_page_picture` for it instead.
mod gate;
mod load;
mod page_picture;
mod rules;

pub use page_picture::decode_page_picture;
pub use rules::picture_url;

use gate::Gate;
use gpui_kit::http_client::{self, AsyncBody, HttpClient, Response, Url, http};
use gpui_kit::{App, Global, RenderImage};
use load::{Hop, NoPicture, PAGE, PERSON, load, load_within};
use page_picture::PublicOnly;
use rules::may_ask_first;
use std::future::Future;
use std::pin::Pin;
use std::sync::Arc;
use std::time::{Duration, Instant};
use ureq::Agent;

struct Pictures(Arc<Gate>);
impl Global for Pictures {}

struct PictureClient {
    agent: Agent,
    gate: Arc<Gate>,
}
/// Connections aren't kept, so nothing stays open once the pictures are read. Redirects are followed by `load`, so each
/// is checked. Cookies are a feature this build leaves out.
fn config() -> ureq::config::Config {
    Agent::config_builder()
        .http_status_as_error(false)
        .https_only(true)
        .proxy(None)
        .max_redirects(0)
        .max_idle_connections(0)
        .max_idle_connections_per_host(0)
        .user_agent("Assembly")
        .accept("image/png, image/jpeg, image/gif, image/webp")
        .build()
}
fn agent() -> Agent {
    Agent::new_with_config(config())
}
/// A page's picture can name any host, so only public addresses are connected to.
fn page_agent() -> Agent {
    Agent::with_parts(
        config(),
        ureq::unversioned::transport::DefaultConnector::default(),
        PublicOnly::default(),
    )
}
/// The body in pieces, stopping as soon as nobody wants it.
fn read_body(
    reader: &mut impl std::io::Read,
    wanted: &dyn Fn() -> bool,
) -> Result<Vec<u8>, NoPicture> {
    let (mut bytes, mut piece) = (Vec::new(), vec![0; 64 * 1024]);
    loop {
        if !wanted() {
            return Err(NoPicture::NoLongerWanted);
        }
        match reader.read(&mut piece) {
            Ok(0) => return Ok(bytes),
            Ok(read) => bytes.extend_from_slice(&piece[..read]),
            Err(_) => return Err(NoPicture::Unreachable),
        }
    }
}
fn ask(
    agent: &Agent,
    url: &Url,
    left: Duration,
    largest: u64,
    wanted: &dyn Fn() -> bool,
) -> Result<Hop, NoPicture> {
    let mut response = agent
        .get(url.as_str())
        .config()
        .timeout_global(Some(left))
        .build()
        .call()
        .map_err(|_| NoPicture::Unreachable)?;
    let status = response.status();
    let header = |name| {
        response
            .headers()
            .get(name)
            .and_then(|value| value.to_str().ok())
            .map(str::to_owned)
    };
    if status.is_redirection() {
        return header(ureq::http::header::LOCATION)
            .map(Hop::Redirect)
            .ok_or(NoPicture::BadStatus);
    }
    if status != ureq::http::StatusCode::OK {
        return Err(NoPicture::BadStatus);
    }
    let content_type = header(ureq::http::header::CONTENT_TYPE).unwrap_or_default();
    let mut body = response.body_mut().with_config().limit(largest).reader();
    let bytes = read_body(&mut body, wanted)?;
    Ok(Hop::Answer {
        content_type,
        bytes,
    })
}
impl HttpClient for PictureClient {
    fn user_agent(&self) -> Option<&http::HeaderValue> {
        None
    }
    fn proxy(&self) -> Option<&Url> {
        None
    }
    fn send(
        &self,
        request: http::Request<AsyncBody>,
    ) -> Pin<Box<dyn Future<Output = http_client::Result<Response<AsyncBody>>> + Send + 'static>>
    {
        let asked = (request.method() == http::Method::GET)
            .then(|| request.uri().to_string())
            .filter(|asked| Url::parse(asked).is_ok_and(|url| may_ask_first(&url)));
        let (agent, gate) = (self.agent.clone(), self.gate.clone());
        Box::pin(async move {
            let Some(asked) = asked else {
                return Err(http_client::anyhow!("only a person's picture is read"));
            };
            loop {
                let turn = gate.clone().turn().await;
                let wanted = || gate.wanted();
                let answer = load(&asked, Instant::now, wanted, |url, left| {
                    ask(&agent, url, left, PERSON.largest, &wanted)
                });
                drop(turn);
                // The screen closed meanwhile: throw this away, so no failure is kept for it, and wait for one to open.
                if gate.wanted() && answer != Err(NoPicture::NoLongerWanted) {
                    let bytes = answer.map_err(|_| http_client::anyhow!("no picture came back"))?;
                    return Ok(Response::builder()
                        .status(http::StatusCode::OK)
                        .body(AsyncBody::from(bytes))?);
                }
            }
        })
    }
}
/// Reads a picture on a task's page, with the same care as a person's: no credentials, cookies, referrer or proxy, and
/// each redirect checked. This blocks, so call it off the main thread. `None` means there's no picture to draw; why is
/// never said, since the address is signed and nothing downstream should repeat it.
pub fn read_page_picture(url: &str, wanted: &dyn Fn() -> bool) -> Option<Arc<RenderImage>> {
    let agent = page_agent();
    let bytes = load_within(&PAGE, url, Instant::now, wanted, |url, left| {
        ask(&agent, url, left, PAGE.largest, wanted)
    })
    .ok()?;
    decode_page_picture(&bytes)
}
/// Makes this the window's web client. Call once, before any window opens.
pub fn register(cx: &mut App) {
    let gate = Arc::new(Gate::default());
    cx.set_global(Pictures(gate.clone()));
    cx.set_http_client(Arc::new(PictureClient {
        agent: agent(),
        gate,
    }));
}
/// Held by a screen showing pictures while it's showing: with none held, no load starts and an answer is discarded.
pub struct PictureInterest(Arc<Gate>);
impl PictureInterest {
    pub fn hold(cx: &App) -> Option<Self> {
        let gate = cx.try_global::<Pictures>()?.0.clone();
        gate.watch();
        Some(Self(gate))
    }
}
impl Drop for PictureInterest {
    fn drop(&mut self) {
        self.0.unwatch();
    }
}
#[cfg(test)]
#[path = "client_tests.rs"]
mod tests;
