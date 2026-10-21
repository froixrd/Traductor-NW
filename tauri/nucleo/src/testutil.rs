//! Servidor HTTP falso para las pruebas: imita a Google, Microsoft y DeepL.

use crate::translate::Endpoints;
use serde_json::Value;
use std::sync::{Arc, Mutex};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

#[derive(Clone, Debug)]
pub struct Req {
    #[allow(dead_code)]
    pub method: String,
    pub path: String,
    pub headers: String,
    pub body: String,
}

pub type Handler = Arc<dyn Fn(&Req) -> (u16, String, String) + Send + Sync>;

/// Servidor HTTP mínimo: responde con lo que diga `handler` (estado, cabeceras extra, cuerpo).
pub async fn server(handler: Handler) -> (String, Arc<Mutex<Vec<Req>>>) {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let log = Arc::new(Mutex::new(Vec::new()));
    let log2 = log.clone();
    tokio::spawn(async move {
        loop {
            let Ok((mut sock, _)) = listener.accept().await else { return };
            let handler = handler.clone();
            let log = log2.clone();
            tokio::spawn(async move {
                let mut buf = Vec::new();
                let mut tmp = [0u8; 4096];
                loop {
                    let n = sock.read(&mut tmp).await.unwrap_or(0);
                    if n == 0 {
                        return;
                    }
                    buf.extend_from_slice(&tmp[..n]);
                    let Some(end) = buf.windows(4).position(|w| w == b"\r\n\r\n") else { continue };
                    let head = String::from_utf8_lossy(&buf[..end]).to_string();
                    let len = head
                        .lines()
                        .find_map(|l| l.to_ascii_lowercase().strip_prefix("content-length:").map(|v| v.trim().parse::<usize>().unwrap_or(0)))
                        .unwrap_or(0);
                    while buf.len() < end + 4 + len {
                        let n = sock.read(&mut tmp).await.unwrap_or(0);
                        if n == 0 {
                            return;
                        }
                        buf.extend_from_slice(&tmp[..n]);
                    }
                    let body = String::from_utf8_lossy(&buf[end + 4..end + 4 + len]).to_string();
                    let mut first = head.lines().next().unwrap_or("").split(' ');
                    let req = Req {
                        method: first.next().unwrap_or("").to_string(),
                        path: first.next().unwrap_or("").to_string(),
                        headers: head.clone(),
                        body,
                    };
                    buf.drain(..end + 4 + len);
                    log.lock().unwrap().push(req.clone());
                    let (status, extra, body) = handler(&req);
                    let resp = format!(
                        "HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {}\r\n{extra}\r\n{body}",
                        body.len()
                    );
                    if sock.write_all(resp.as_bytes()).await.is_err() {
                        return;
                    }
                }
            });
        }
    });
    (format!("http://{addr}"), log)
}

pub fn endpoints(base: &str) -> Endpoints {
    Endpoints {
        google: format!("{base}/google"),
        microsoft: format!("{base}/microsoft"),
        deepl_free: format!("{base}/deeplfree"),
        deepl_pro: format!("{base}/deeplpro"),
    }
}

pub fn query_param(path: &str, name: &str) -> String {
    let url = url::Url::parse(&format!("http://x{path}")).unwrap();
    url.query_pairs().find(|(k, _)| k == name).map(|(_, v)| v.into_owned()).unwrap_or_default()
}

/// Google falso: "traduce" poniendo el texto en mayúsculas, línea a línea.
pub fn google_ok(req: &Req) -> (u16, String, String) {
    let q = query_param(&req.path, "q");
    let segs: Vec<Value> = q.split('\n').map(|l| serde_json::json!([format!("{}\n", l.to_uppercase()), l])).collect();
    let mut segs = segs;
    if let Some(last) = segs.last_mut() {
        let s = last[0].as_str().unwrap().trim_end().to_string();
        last[0] = Value::String(s);
    }
    (200, String::new(), serde_json::json!([segs]).to_string())
}

/// Microsoft falso: "traduce" añadiendo "ms:".
pub fn microsoft_ok(req: &Req) -> (u16, String, String) {
    let items: Vec<Value> = serde_json::from_str(&req.body).unwrap();
    let out: Vec<Value> = items
        .iter()
        .map(|i| serde_json::json!({ "translations": [{ "text": format!("ms:{}", i["Text"].as_str().unwrap()), "to": "es" }] }))
        .collect();
    (200, String::new(), Value::Array(out).to_string())
}

