//! Pruebas del motor de traducción contra un servidor HTTP falso que imita a Google, Microsoft y DeepL.

use super::*;
use crate::testutil::*;
use std::sync::atomic::AtomicUsize;

#[tokio::test]
async fn google_agrupa_y_usa_cache() {
    let (base, log) = server(Arc::new(|r: &Req| google_ok(r))).await;
    let t = Translator::new(endpoints(&base));
    t.set_preferred(Provider::Google);
    let texts = vec!["hello".to_string(), "u there?".to_string(), "hello".to_string(), ":)".to_string()];
    let r = t.translate(&texts, "en", "es").await.unwrap();
    // Abreviaturas escritas completas antes de traducir; repetidos y sin letras no viajan.
    assert_eq!(r, vec!["HELLO", "YOU THERE?", "HELLO", ":)"]);
    assert_eq!(log.lock().unwrap().len(), 1);
    assert_eq!(query_param(&log.lock().unwrap()[0].path, "q"), "hello\nyou there?");
    let r = t.translate(&["hello".to_string()], "en", "es").await.unwrap();
    assert_eq!(r, vec!["HELLO"]);
    assert_eq!(log.lock().unwrap().len(), 1, "la segunda vez sale de la caché");
}

#[tokio::test]
async fn palabras_protegidas_viajan_como_marcadores() {
    let (base, log) = server(Arc::new(|r: &Req| google_ok(r))).await;
    let t = Translator::new(endpoints(&base));
    t.set_preferred(Provider::Google);
    t.set_protected_terms(["Seraphine"]);
    let r = t.translate(&["ask seraphine".to_string()], "en", "es").await.unwrap();
    assert_eq!(query_param(&log.lock().unwrap()[0].path, "q"), "ask {0}");
    assert_eq!(r, vec!["ASK seraphine"]);
}

#[tokio::test]
async fn microsoft_sin_cuota_pasa_a_google_y_avisa_una_vez() {
    let (base, _log) = server(Arc::new(|r: &Req| {
        if r.path.starts_with("/microsoft") {
            (403, String::new(), r#"{"error":{"code":403001,"message":"Out of call volume quota"}}"#.into())
        } else {
            google_ok(r)
        }
    }))
    .await;
    let t = Translator::new(endpoints(&base));
    t.set_key(Provider::Microsoft, Some("clave"), None);
    t.set_preferred(Provider::Microsoft);
    let notices = Arc::new(AtomicUsize::new(0));
    let n2 = notices.clone();
    t.on_notice(move |m, warn| {
        assert!(warn && m.contains("cuota gratuita de Microsoft"));
        n2.fetch_add(1, Ordering::SeqCst);
    });
    let r = t.translate(&["hi".to_string()], "en", "es").await.unwrap();
    assert_eq!(r, vec!["HI"]);
    assert_eq!(t.state(Provider::Microsoft), ProviderState::QuotaExhausted);
    assert_eq!(t.active_provider(), Some(Provider::Google));
    t.translate(&["bye".to_string()], "en", "es").await.unwrap();
    assert_eq!(notices.load(Ordering::SeqCst), 1);
}

#[tokio::test]
async fn microsoft_principal_y_contador() {
    let (base, log) = server(Arc::new(|r: &Req| microsoft_ok(r))).await;
    let t = Translator::new(endpoints(&base));
    t.set_key(Provider::Microsoft, Some("clave"), Some("westeurope"));
    let used = Arc::new(AtomicUsize::new(0));
    let u2 = used.clone();
    t.on_characters_used(move |p, n| {
        assert_eq!(p, Provider::Microsoft);
        u2.fetch_add(n, Ordering::SeqCst);
    });
    let r = t.translate(&["one".to_string(), "two".to_string()], "en", "es").await.unwrap();
    assert_eq!(r, vec!["ms:one", "ms:two"]);
    assert_eq!(used.load(Ordering::SeqCst), 6);
    let req = log.lock().unwrap()[0].clone();
    assert!(req.headers.to_ascii_lowercase().contains("ocp-apim-subscription-region: westeurope"));
    assert!(!req.path.contains("from=es"));
    t.translate(&["hola".to_string()], "es", "en").await.unwrap();
    assert!(log.lock().unwrap()[1].path.contains("from=es"));
}

#[tokio::test]
async fn sin_respaldo_y_sin_claves_no_hay_traductor() {
    let t = Translator::new(endpoints("http://127.0.0.1:9"));
    t.set_preferred(Provider::Microsoft);
    t.set_google_fallback(false);
    assert_eq!(t.active_provider(), None);
    match t.translate(&["hi".to_string()], "en", "es").await {
        Err(TrError::NoProvider(m)) => assert!(m.contains("Microsoft: sin clave")),
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn google_limitado_hace_pausa() {
    let (base, _log) = server(Arc::new(|_r: &Req| (429, "Retry-After: 7\r\n".into(), "{}".into()))).await;
    let t = Translator::new(endpoints(&base));
    t.set_preferred(Provider::Google);
    match t.translate(&["hi".to_string()], "en", "es").await {
        Err(TrError::RateLimited { provider: Provider::Google, retry_after: Some(d) }) => assert!(d >= Duration::from_secs(7)),
        other => panic!("{other:?}"),
    }
    assert!(t.cooldown_remaining() > Duration::from_secs(5));
    assert!(t.is_paused(Provider::Google));
}

#[tokio::test]
async fn google_parte_el_lote_si_no_cuadran_las_lineas() {
    // La primera respuesta agrupada pierde una línea; partido en dos, cuadra.
    let calls = Arc::new(AtomicUsize::new(0));
    let c2 = calls.clone();
    let (base, _log) = server(Arc::new(move |r: &Req| {
        if c2.fetch_add(1, Ordering::SeqCst) == 0 {
            (200, String::new(), r#"[[["SOLO UNA",""]]]"#.into())
        } else {
            google_ok(r)
        }
    }))
    .await;
    let t = Translator::new(endpoints(&base));
    t.set_preferred(Provider::Google);
    let r = t.translate(&["a b".to_string(), "c d".to_string()], "en", "es").await.unwrap();
    assert_eq!(r, vec!["A B", "C D"]);
}

#[tokio::test]
async fn deepl_clave_gratuita_y_uso() {
    let (base, log) = server(Arc::new(|r: &Req| {
        if r.path.ends_with("/v2/usage") {
            (200, String::new(), r#"{"character_count":1200,"character_limit":500000}"#.into())
        } else {
            (200, String::new(), r#"{"translations":[{"text":"Hola"}]}"#.into())
        }
    }))
    .await;
    let t = Translator::new(endpoints(&base));
    assert_eq!(t.check_deepl_key("abc:fx").await.unwrap(), (1200, 500000));
    assert!(log.lock().unwrap()[0].path.starts_with("/deeplfree/"));
    t.set_key(Provider::DeepL, Some("abc"), None);
    t.set_preferred(Provider::DeepL);
    assert_eq!(t.translate(&["Hello".to_string()], "es", "en").await.unwrap(), vec!["Hola"]);
    let req = log.lock().unwrap()[1].clone();
    assert!(req.path.starts_with("/deeplpro/"));
    assert!(req.body.contains("source_lang=ES") && req.body.contains("target_lang=EN-US"));
}

#[test]
fn lotes() {
    let items: Vec<String> = (0..95).map(|i| format!("line {i}")).collect();
    let c = chunk(&items);
    assert_eq!(c.iter().map(|c| c.len()).collect::<Vec<_>>(), vec![40, 40, 15]);
    let big: Vec<String> = (0..3).map(|_| "x".repeat(2500)).collect();
    assert_eq!(chunk(&big).len(), 2);
}
