using System.Threading.Channels;

namespace TraductorNWN
{
    // Cola de traducción de las líneas que llegan del juego.
    //
    //  - Un único consumidor, así los mensajes salen SIEMPRE en el orden en que llegaron.
    //  - Mientras el proveedor está en pausa (429) no se bloquea nada: las líneas se siguen
    //    acumulando y, al acabar la pausa, van todas juntas en una sola petición.
    //  - Si una línea lleva demasiado esperando (API caída o bloqueada mucho rato), se entrega
    //    sin traducir para que el chat no se quede atrás respecto al juego.
    internal sealed class IncomingPipeline
    {
        private readonly TranslationService _translator;
        private readonly Action<ParsedLine, string> _onTranslated;
        private readonly Action<ParsedLine, string> _onUntranslated;
        private readonly Action<string, bool> _onNotice; // (texto, esAviso)
        private readonly long _maxWaitMs;
        private readonly int _maxBatch;

        private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        private int _pending;

        private readonly record struct Item(ParsedLine Line, long EnqueuedAt);

        public IncomingPipeline(
            TranslationService translator,
            Action<ParsedLine, string> onTranslated,
            Action<ParsedLine, string> onUntranslated,
            Action<string, bool> onNotice,
            long maxWaitMs = 15_000,
            int maxBatch = 60)
        {
            _translator = translator;
            _onTranslated = onTranslated;
            _onUntranslated = onUntranslated;
            _onNotice = onNotice;
            _maxWaitMs = maxWaitMs;
            _maxBatch = maxBatch;
        }

        // Mensajes recibidos que aún no se han mostrado en Ambos/Español.
        public int Pending => Volatile.Read(ref _pending);

        // Llamar siempre desde el mismo hilo (el lector del log).
        public void Enqueue(ParsedLine line)
        {
            Interlocked.Increment(ref _pending);
            _channel.Writer.TryWrite(new Item(line, Environment.TickCount64));
        }

        public void Complete() => _channel.Writer.TryComplete();

        public async Task RunAsync(CancellationToken ct)
        {
            var reader = _channel.Reader;
            var batch = new List<Item>();
            int consecutiveFailures = 0;
            bool limited = false;

            while (!ct.IsCancellationRequested)
            {
                if (batch.Count == 0 && !await reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;
                DrainInto(reader, batch);

                // Esperar a que el proveedor acepte peticiones, sin dejar de recoger líneas
                // ni de soltar las que llevan demasiado esperando.
                while (batch.Count > 0)
                {
                    FlushStale(batch);
                    var wait = _translator.TimeUntilReady;
                    if (wait <= TimeSpan.Zero) break;
                    var step = TimeSpan.FromMilliseconds(250);
                    await Task.Delay(wait < step ? wait : step, ct).ConfigureAwait(false);
                    DrainInto(reader, batch);
                }
                if (batch.Count == 0) continue;

                // Pequeña ventana para que una ráfaga de líneas vaya en la misma petición.
                if (batch.Count < 4)
                {
                    await Task.Delay(40, ct).ConfigureAwait(false);
                    DrainInto(reader, batch);
                }

                int take = Math.Min(batch.Count, _maxBatch);
                var texts = new string[take];
                for (int i = 0; i < take; i++) texts[i] = batch[i].Line.Text;

                try
                {
                    var results = await _translator.TranslateAsync(texts, "en", "es", ct).ConfigureAwait(false);
                    for (int i = 0; i < take; i++) _onTranslated(batch[i].Line, results[i]);
                    Remove(batch, take);

                    consecutiveFailures = 0;
                    if (limited)
                    {
                        limited = false;
                        _onNotice("Traducción reanudada.", false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (RateLimitedException ex)
                {
                    // El lote se queda en la cola y se reintenta al acabar la pausa.
                    if (!limited)
                    {
                        limited = true;
                        var secs = Math.Ceiling((ex.RetryAfter ?? _translator.TimeUntilReady).TotalSeconds);
                        _onNotice($"{ex.Provider} está limitando las peticiones. Reintento en ~{secs} s; " +
                                  "mientras tanto los mensajes se acumulan y se traducirán juntos.", true);
                    }
                }
                catch (TransientTranslationException ex)
                {
                    if (++consecutiveFailures >= 3)
                    {
                        FlushUntranslated(batch, take, "error de conexión");
                        _onNotice("No se pudo traducir: " + ex.Message, true);
                        consecutiveFailures = 0;
                    }
                }
                catch (Exception ex)
                {
                    FlushUntranslated(batch, take, "error");
                    _onNotice("Error de traducción: " + ex.Message, true);
                    consecutiveFailures = 0;
                }
            }
        }

        private static void DrainInto(ChannelReader<Item> reader, List<Item> batch)
        {
            while (reader.TryRead(out var item)) batch.Add(item);
        }

        private void FlushStale(List<Item> batch)
        {
            long now = Environment.TickCount64;
            int n = 0;
            while (n < batch.Count && now - batch[n].EnqueuedAt > _maxWaitMs) n++;
            if (n > 0) FlushUntranslated(batch, n, "la API va con retraso");
        }

        // Entrega los primeros 'count' sin traducir (o traducidos si ya estaban en caché).
        private void FlushUntranslated(List<Item> batch, int count, string reason)
        {
            for (int i = 0; i < count; i++)
            {
                var line = batch[i].Line;
                if (_translator.TryGetCached(line.Text, "en", "es", out var cached))
                    _onTranslated(line, cached);
                else
                    _onUntranslated(line, reason);
            }
            Remove(batch, count);
        }

        private void Remove(List<Item> batch, int count)
        {
            batch.RemoveRange(0, count);
            Interlocked.Add(ref _pending, -count);
        }
    }
}
