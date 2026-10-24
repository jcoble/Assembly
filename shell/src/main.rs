// Assembly's shell: one window showing app/build, and the page's native jobs (the plugins below and dropped
// paths). Everything else is the .NET backend, which the shell starts as a child, names to the page before it
// loads (window.__ASSEMBLY__ = { port, token }) and stops by its pid on quit.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::fs::{self, File};
use std::net::TcpStream;
use std::path::Path;
use std::process::{Child, Command};
use std::sync::Mutex;
use std::thread::sleep;
use std::time::{Duration, Instant};
use tauri::{Manager, RunEvent, WebviewUrl, WebviewWindowBuilder};

const PORT: u16 = 18797;

struct Backend(Mutex<Option<Child>>);

fn port_open() -> bool {
    TcpStream::connect_timeout(&([127, 0, 0, 1], PORT).into(), Duration::from_millis(200)).is_ok()
}

// ASSEMBLY_REMOTE=workbox-test adds that machine (the backend's --remote).
fn start_backend(exe: &Path, home: &Path) -> Result<Child, String> {
    if port_open() {
        return Err(format!("port {PORT} is in use; is another Assembly running?"));
    }
    let data = home.join("assembly-dotnet/mac");
    fs::create_dir_all(&data).map_err(|e| e.to_string())?;
    let log = File::options().create(true).append(true).open(data.join("backend.log")).map_err(|e| e.to_string())?;
    let mut command = Command::new(exe);
    command.args(["serve", "--port", &PORT.to_string()]);
    if let Ok(remote) = std::env::var("ASSEMBLY_REMOTE") {
        command.args(["--remote", &remote]);
    }
    let mut child = command.stdout(log.try_clone().map_err(|e| e.to_string())?).stderr(log).spawn().map_err(|e| e.to_string())?;
    let started = Instant::now();
    while !port_open() {
        if child.try_wait().map_err(|e| e.to_string())?.is_some() || started.elapsed() > Duration::from_secs(20) {
            let _ = child.kill();
            return Err(format!("the backend didn't start; see {}", data.join("backend.log").display()));
        }
        sleep(Duration::from_millis(100));
    }
    Ok(child)
}

// The one ASSEMBLY_TOKEN=<hex> line the backend made on its first start.
fn read_token(home: &Path) -> Result<String, String> {
    let text = fs::read_to_string(home.join(".config/assembly-dotnet/env")).map_err(|e| e.to_string())?;
    text.lines()
        .find_map(|line| line.trim().strip_prefix("ASSEMBLY_TOKEN="))
        .map(str::to_owned)
        .ok_or_else(|| "no ASSEMBLY_TOKEN in ~/.config/assembly-dotnet/env".into())
}

// SIGTERM, then up to 10 s for it to stop its sessions and its tunnel, then SIGKILL.
fn stop_backend(mut child: Child) {
    unsafe { libc::kill(child.id() as i32, libc::SIGTERM) };
    let started = Instant::now();
    while started.elapsed() < Duration::from_secs(10) {
        if let Ok(Some(_)) = child.try_wait() {
            return;
        }
        sleep(Duration::from_millis(100));
    }
    let _ = child.kill();
    let _ = child.wait();
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_clipboard_manager::init())
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let home = app.path().home_dir()?;
            let exe = app.path().resource_dir()?.join("backend/Assembly.Mac");
            let child = start_backend(&exe, &home)?;
            app.manage(Backend(Mutex::new(Some(child))));
            let token = read_token(&home)?;
            let script = format!("window.__ASSEMBLY__ = {{ port: {PORT}, token: {} }};", serde_json::to_string(&token)?);
            // ASSEMBLY_SOAK=1 opens the page in soak mode (?soak=1); any other value is the page's whole query.
            let query = std::env::var("ASSEMBLY_SOAK").map(|soak| if soak == "1" { "soak=1".to_owned() } else { soak });
            let page = query.map_or("index.html".to_owned(), |query| format!("index.html?{query}"));
            WebviewWindowBuilder::new(app, "main", WebviewUrl::App(page.into()))
                .title("Assembly")
                .inner_size(1710.0, 990.0)
                .initialization_script(&script)
                .build()?;
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building the shell")
        .run(|app, event| {
            if let RunEvent::Exit = event
                && let Some(child) = app.state::<Backend>().0.lock().unwrap().take()
            {
                stop_backend(child);
            }
        });
}
