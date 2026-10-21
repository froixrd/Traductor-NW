//! Controla el ritmo de peticiones a un traductor:
//!  - separa las peticiones un mínimo de tiempo (más cuanto más nos haya limitado el traductor);
//!  - tras un 429 abre una pausa con espera progresiva (o el Retry-After que mande el servidor).
//! Se puede usar desde varias tareas a la vez (traducción de entrada y de salida).

use rand::Rng;
use std::sync::Mutex;
use std::time::{Duration, Instant};

const MAX_BACKOFF: Duration = Duration::from_secs(90);

struct State {
    next_slot: Option<Instant>,
    cooldown_until: Option<Instant>,
    strikes: u32,
}

pub struct RateGate {
    min_interval: Duration,
    state: Mutex<State>,
}

fn remaining(until: Option<Instant>, now: Instant) -> Duration {
    until.map_or(Duration::ZERO, |u| u.saturating_duration_since(now))
}

fn later(a: Option<Instant>, b: Option<Instant>) -> Option<Instant> {
    match (a, b) {
        (Some(a), Some(b)) => Some(a.max(b)),
        (a, None) => a,
        (None, b) => b,
    }
}

impl RateGate {
    pub fn new(min_interval: Duration) -> Self {
        Self { min_interval, state: Mutex::new(State { next_slot: None, cooldown_until: None, strikes: 0 }) }
    }

    /// Lo que queda de pausa por límite de peticiones (cero si no hay pausa).
    pub fn cooldown_remaining(&self) -> Duration {
        remaining(self.state.lock().unwrap().cooldown_until, Instant::now())
    }

    /// Tiempo hasta que se pueda lanzar la siguiente petición (pausa o separación mínima).
    pub fn time_until_ready(&self) -> Duration {
        let s = self.state.lock().unwrap();
        remaining(later(s.next_slot, s.cooldown_until), Instant::now())
    }

    pub async fn acquire(&self) {
        loop {
            let wait = {
                let mut s = self.state.lock().unwrap();
                let now = Instant::now();
                let wait = remaining(later(s.next_slot, s.cooldown_until), now);
                if wait.is_zero() {
                    s.next_slot = Some(now + self.min_interval * (1 + s.strikes.min(5)));
                    return;
                }
                wait
            };
            tokio::time::sleep(wait).await;
        }
    }

    /// Devuelve la pausa aplicada.
    pub fn report_rate_limited(&self, retry_after: Option<Duration>) -> Duration {
        let mut s = self.state.lock().unwrap();
        s.strikes = (s.strikes + 1).min(10);
        let mut backoff = retry_after.unwrap_or_else(|| Duration::from_secs(1u64 << s.strikes)); // 2, 4, 8, 16…
        backoff = backoff.clamp(Duration::from_secs(1), MAX_BACKOFF);
        backoff += Duration::from_millis(rand::rng().random_range(0..750)); // para no reintentar todos a la vez
        let until = Instant::now() + backoff;
        s.cooldown_until = later(s.cooldown_until, Some(until));
        backoff
    }

    /// Error de red o del servidor: pausa para no insistir (corta por defecto; más larga si hay
    /// otro traductor que pueda encargarse mientras tanto).
    pub fn report_transient_error(&self, pause: Option<Duration>) {
        let mut s = self.state.lock().unwrap();
        let until = Instant::now() + pause.unwrap_or(Duration::from_secs(2));
        s.cooldown_until = later(s.cooldown_until, Some(until));
    }

    /// Cada éxito relaja poco a poco la separación extra entre peticiones.
    pub fn report_success(&self) {
        let mut s = self.state.lock().unwrap();
        s.strikes = s.strikes.saturating_sub(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn pausa_y_separacion() {
        let g = RateGate::new(Duration::from_millis(50));
        g.acquire().await;
        assert!(g.time_until_ready() > Duration::from_millis(30));
        let b = g.report_rate_limited(Some(Duration::from_secs(3)));
        assert!(b >= Duration::from_secs(3) && b < Duration::from_secs(4));
        assert!(g.cooldown_remaining() > Duration::from_secs(2));
        let b = g.report_rate_limited(Some(Duration::from_secs(600)));
        assert!(b <= MAX_BACKOFF + Duration::from_secs(1));
    }
}
