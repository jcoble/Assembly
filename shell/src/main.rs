// Assembly's shell: one window showing app/build, and the page's native jobs (the plugins below and dropped
// paths). Everything else is the .NET backend, which the shell starts as a child, names to the page before it
// loads (window.__ASSEMBLY__ = { port, token }) and stops by its pid on quit. Its port and folders are the
// worktree's, from backend/lane.json in the bundle (shell/assembly.sh writes it), so lanes run side by side.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::fs::{self, File};
use std::net::TcpStream;
use std::path::{Path, PathBuf};
use std::process::{Child, Command};
use std::sync::Mutex;
use std::thread::sleep;
use std::time::{Duration, Instant};
use serde_json::Value;
use tauri::{Manager, RunEvent, WebviewUrl, WebviewWindowBuilder};

struct Backend(Mutex<Option<Child>>);

// The worktree's values: { port, home, tokenFile, remotePort, remoteTokenFile }.
struct Lane {
    port: u16,
    home: PathBuf,
    token_file: PathBuf,
    remote_port: String,
    remote_token_file: String,
}

fn read_lane(file: &Path) -> Result<Lane, String> {
    let lane: Value = serde_json::from_str(&fs::read_to_string(file).map_err(|e| format!("{}: {e}", file.display()))?).map_err(|e| e.to_string())?;
    let text = |name: &str| lane[name].as_str().map(str::to_owned).ok_or(format!("{} has no {name}", file.display()));
    Ok(Lane {
        port: lane["port"].as_u64().ok_or("lane.json has no port")? as u16,
        home: text("home")?.into(),
        token_file: text("tokenFile")?.into(),
        remote_port: lane["remotePort"].as_u64().ok_or("lane.json has no remotePort")?.to_string(),
        remote_token_file: text("remoteTokenFile")?,
    })
}

fn port_open(port: u16) -> bool {
    TcpStream::connect_timeout(&([127, 0, 0, 1], port).into(), Duration::from_millis(200)).is_ok()
}

// ASSEMBLY_REMOTE=workbox-test adds that machine (the backend's --remote).
fn start_backend(exe: &Path, lane: &Lane) -> Result<Child, String> {
    if port_open(lane.port) {
        return Err(format!("port {} is in use; is another Assembly running?", lane.port));
    }
    let data = &lane.home;
    fs::create_dir_all(data).map_err(|e| e.to_string())?;
    let log = File::options().create(true).append(true).open(data.join("backend.log")).map_err(|e| e.to_string())?;
    let mut command = Command::new(exe);
    let port = lane.port.to_string();
    command.args(["serve", "--port", &port, "--home"]).arg(data).arg("--token-file").arg(&lane.token_file);
    if let Ok(remote) = std::env::var("ASSEMBLY_REMOTE") {
        command.args(["--remote", &remote, "--remote-port", &lane.remote_port, "--remote-token-file", &lane.remote_token_file]);
    }
    let mut child = command.stdout(log.try_clone().map_err(|e| e.to_string())?).stderr(log).spawn().map_err(|e| e.to_string())?;
    let started = Instant::now();
    while !port_open(lane.port) {
        if child.try_wait().map_err(|e| e.to_string())?.is_some() || started.elapsed() > Duration::from_secs(20) {
            let _ = child.kill();
            return Err(format!("the backend didn't start; see {}", data.join("backend.log").display()));
        }
        sleep(Duration::from_millis(100));
    }
    Ok(child)
}

// The one ASSEMBLY_TOKEN=<hex> line the backend made on its first start.
fn read_token(file: &Path) -> Result<String, String> {
    let text = fs::read_to_string(file).map_err(|e| e.to_string())?;
    text.lines()
        .find_map(|line| line.trim().strip_prefix("ASSEMBLY_TOKEN="))
        .map(str::to_owned)
        .ok_or_else(|| format!("no ASSEMBLY_TOKEN in {}", file.display()))
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
            let backend = app.path().resource_dir()?.join("backend");
            let lane = read_lane(&backend.join("lane.json"))?;
            let child = start_backend(&backend.join("Assembly.Mac"), &lane)?;
            app.manage(Backend(Mutex::new(Some(child))));
            let token = read_token(&lane.token_file)?;
            let script = format!("window.__ASSEMBLY__ = {{ port: {}, token: {} }};", lane.port, serde_json::to_string(&token)?);
            // ASSEMBLY_QUERY is the page's query: counters=1 for the lag meter, soak=1 for the soak.
            let page = std::env::var("ASSEMBLY_QUERY").map_or("index.html".to_owned(), |query| format!("index.html?{query}"));
            WebviewWindowBuilder::new(app, "main", WebviewUrl::App(page.into()))
                .title(app.config().product_name.as_deref().unwrap_or("Assembly"))
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
