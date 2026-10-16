namespace TraductorNWN
{
    // Controla el ritmo de peticiones a un proveedor de traducción:
    //  - separa las peticiones un mínimo de tiempo (más cuanto más nos haya limitado el proveedor);
    //  - tras un 429 abre una "pausa" con backoff exponencial (o el Retry-After que mande el servidor).
    // Es seguro usarlo desde varios hilos a la vez (traducción de entrada y de salida).
    internal sealed class RateGate
    {
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(90);

        private readonly object _lock = new();
        private readonly TimeSpan _minInterval;
        private DateTime _nextSlotUtc = DateTime.MinValue;
        private DateTime _cooldownUntilUtc = DateTime.MinValue;
        private int _strikes;

        public RateGate(TimeSpan minInterval) => _minInterval = minInterval;

        // Tiempo que queda de pausa por límite de peticiones (0 si no hay pausa).
        public TimeSpan CooldownRemaining
        {
            get { lock (_lock) return Positive(_cooldownUntilUtc - DateTime.UtcNow); }
        }

        // Tiempo hasta que se pueda lanzar la siguiente petición (pausa o separación mínima).
        public TimeSpan TimeUntilReady
        {
            get { lock (_lock) return Positive(Max(_nextSlotUtc, _cooldownUntilUtc) - DateTime.UtcNow); }
        }

        public async Task AcquireAsync(CancellationToken ct)
        {
            while (true)
            {
                TimeSpan wait;
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    var ready = Max(_nextSlotUtc, _cooldownUntilUtc);
                    if (ready <= now)
                    {
                        _nextSlotUtc = now + _minInterval * (1 + Math.Min(_strikes, 5));
                        return;
                    }
                    wait = ready - now;
                }
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }

        // Devuelve la pausa aplicada.
        public TimeSpan ReportRateLimited(TimeSpan? retryAfter)
        {
            lock (_lock)
            {
                _strikes = Math.Min(_strikes + 1, 10);
                var backoff = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, _strikes)); // 2, 4, 8, 16, 32, 64…
                if (backoff > MaxBackoff) backoff = MaxBackoff;
                if (backoff < TimeSpan.FromSeconds(1)) backoff = TimeSpan.FromSeconds(1);
                backoff += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 750)); // jitter

                var until = DateTime.UtcNow + backoff;
                if (until > _cooldownUntilUtc) _cooldownUntilUtc = until;
                return backoff;
            }
        }

        // Error de red / 5xx: pausa para no martillear (corta por defecto; más larga si hay otro
        // traductor que pueda encargarse mientras tanto).
        public void ReportTransientError(TimeSpan? pause = null)
        {
            lock (_lock)
            {
                var until = DateTime.UtcNow + (pause ?? TimeSpan.FromSeconds(2));
                if (until > _cooldownUntilUtc) _cooldownUntilUtc = until;
            }
        }

        // Cada éxito relaja poco a poco la separación extra entre peticiones.
        public void ReportSuccess()
        {
            lock (_lock)
            {
                if (_strikes > 0) _strikes--;
            }
        }

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
        private static TimeSpan Positive(TimeSpan t) => t > TimeSpan.Zero ? t : TimeSpan.Zero;
    }
}
