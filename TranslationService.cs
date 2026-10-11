using System.Net;
using System.Text;
using System.Text.Json;

namespace TraductorNWN
{
    internal enum Proveedor { Google, DeepL }

    internal class TranslationException : Exception
    {
        public TranslationException(string message, Exception? inner = null) : base(message, inner) { }
    }

    // El proveedor nos está limitando (429). RetryAfter = pausa efectiva aplicada.
    internal sealed class RateLimitedException : TranslationException
    {
        public RateLimitedException(Proveedor provider, TimeSpan? retryAfter)
            : base($"{provider} ha limitado las peticiones (429).")
        {
            Provider = provider;
            RetryAfter = retryAfter;
        }

        public Proveedor Provider { get; }
        public TimeSpan? RetryAfter { get; }
    }

    // Fallo pasajero (sin red, timeout, 5xx): merece reintento.
    internal sealed class TransientTranslationException : TranslationException
    {
        public TransientTranslationException(string message, Exception? inner = null) : base(message, inner) { }
    }

    // DeepL no se puede usar (clave inválida o cuota agotada): se cae a Google.
    internal sealed class DeepLUnavailableException : TranslationException
    {
        public DeepLUnavailableException(string message) : base(message) { }
    }

    // La traducción agrupada de Google no devolvió el mismo número de líneas.
    internal sealed class SplitMismatchException : TranslationException
    {
        public SplitMismatchException() : base("La respuesta agrupada no coincide con el número de líneas.") { }
    }

    internal readonly record struct DeepLUsage(long Count, long Limit);

    // Motor de traducción:
    //  - agrupa varias líneas en UNA petición (Google: separadas por salto de línea; DeepL: varios "text");
    //  - caché LRU para no repetir peticiones;
    //  - un RateGate por proveedor con backoff exponencial y respeto del Retry-After;
    //  - si DeepL se queda sin cuota o la clave falla, sigue con Google automáticamente.
    internal sealed class TranslationService : IDisposable
    {
        private const int MaxItemsPorPeticion = 40;
        private const int MaxQueryChars = 6000; // tamaño máximo (ya codificado) del texto en la URL de Google

        private readonly HttpClient _http;
        private readonly LruCache<string, string> _cache = new(5000, StringComparer.Ordinal);
        private readonly RateGate _googleGate = new(TimeSpan.FromMilliseconds(400));
        private readonly RateGate _deeplGate = new(TimeSpan.FromMilliseconds(200));

        private volatile string? _deeplKey;
        private volatile string? _deeplDisabledReason;

        // Avisos para mostrar al usuario (p. ej. "DeepL sin cuota, se usa Google").
        public event Action<string>? Notice;

        public TranslationService() : this(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(8)
        })
        {
        }

        // Permite inyectar otro HttpMessageHandler (pruebas).
        internal TranslationService(HttpMessageHandler handler)
        {
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36");
        }

        public string? DeepLKey
        {
            get => _deeplKey;
            set
            {
                _deeplKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                _deeplDisabledReason = null;
            }
        }

        public bool DeepLUsable => _deeplKey != null && _deeplDisabledReason == null;
        public Proveedor ActiveProvider => ChooseProvider();
        public TimeSpan CooldownRemaining => Gate(ChooseProvider()).CooldownRemaining;
        public TimeSpan TimeUntilReady => Gate(ChooseProvider()).TimeUntilReady;

        public void Dispose() => _http.Dispose();

        private RateGate Gate(Proveedor p) => p == Proveedor.DeepL ? _deeplGate : _googleGate;

        private Proveedor ChooseProvider()
        {
            if (!DeepLUsable) return Proveedor.Google;
            var deeplWait = _deeplGate.CooldownRemaining;
            if (deeplWait <= TimeSpan.FromSeconds(2)) return Proveedor.DeepL;
            // DeepL en pausa larga: mientras tanto tiramos de Google si está libre.
            return _googleGate.CooldownRemaining < deeplWait ? Proveedor.Google : Proveedor.DeepL;
        }

        // ---------------- API pública ----------------

        public bool TryGetCached(string text, string source, string target, out string translated)
        {
            if (!NeedsTranslation(text))
            {
                translated = text;
                return true;
            }
            return _cache.TryGet(CacheKey(text, source, target), out translated);
        }

        // Guarda una traducción conocida (p. ej. el eco en el log de lo que acabamos de enviar,
        // así no se gasta una petición en traducir de vuelta nuestro propio mensaje).
        public void Remember(string text, string translated, string source, string target)
        {
            if (NeedsTranslation(text))
                _cache.Set(CacheKey(text, source, target), translated);
        }

        // Traduce varios textos manteniendo el orden. Lanza RateLimitedException /
        // TransientTranslationException para que quien llama decida si reintenta.
        public async Task<string[]> TranslateAsync(IReadOnlyList<string> texts, string source, string target, CancellationToken ct)
        {
            var results = new string[texts.Count];
            var pending = new Dictionary<string, List<int>>(StringComparer.Ordinal);

            for (int i = 0; i < texts.Count; i++)
            {
                var t = texts[i] ?? "";
                if (TryGetCached(t, source, target, out var cached))
                {
                    results[i] = cached;
                    continue;
                }
                if (!pending.TryGetValue(t, out var indices))
                {
                    indices = new List<int>(1);
                    pending[t] = indices;
                }
                indices.Add(i);
            }

            if (pending.Count == 0) return results;

            foreach (var chunk in Chunk(new List<string>(pending.Keys)))
            {
                var translated = await TranslateChunkAsync(chunk, source, target, ct).ConfigureAwait(false);
                for (int j = 0; j < chunk.Count; j++)
                    foreach (var idx in pending[chunk[j]])
                        results[idx] = translated[j];
            }
            return results;
        }

        // Para los mensajes que escribe el usuario: reintenta durante un tiempo máximo.
        public async Task<string> TranslateOneWithRetryAsync(string text, string source, string target, TimeSpan maxWait, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + maxWait;
            while (true)
            {
                try
                {
                    var r = await TranslateAsync(new[] { text }, source, target, ct).ConfigureAwait(false);
                    return r[0];
                }
                catch (Exception ex) when (ex is RateLimitedException or TransientTranslationException)
                {
                    var wait = TimeUntilReady;
                    if (wait < TimeSpan.FromMilliseconds(300)) wait = TimeSpan.FromMilliseconds(300);
                    if (DateTime.UtcNow + wait > deadline) throw;
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
        }

        // Comprueba la clave de DeepL y devuelve el consumo del mes.
        public async Task<DeepLUsage> CheckDeepLKeyAsync(string key, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DeepLBaseUrl(key) + "/v2/usage");
            request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
            using var resp = await SendAsync(request, Proveedor.DeepL, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                long count = root.TryGetProperty("character_count", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
                long limit = root.TryGetProperty("character_limit", out var l) && l.TryGetInt64(out var lv) ? lv : 0;
                return new DeepLUsage(count, limit);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                return new DeepLUsage(0, 0);
            }
        }

        // ---------------- Internos ----------------

        private static bool NeedsTranslation(string text)
        {
            // Sin letras ("...", ":)", "123", "<3") no hay nada que traducir.
            foreach (var c in text)
                if (char.IsLetter(c)) return true;
            return false;
        }

        private static string CacheKey(string text, string source, string target) => source + ">" + target + "\u0001" + text;

        private static IEnumerable<List<string>> Chunk(List<string> items)
        {
            var current = new List<string>();
            int size = 0;
            foreach (var item in items)
            {
                int cost = Uri.EscapeDataString(item).Length + 3; // + "%0A"
                if (current.Count > 0 && (current.Count >= MaxItemsPorPeticion || size + cost > MaxQueryChars))
                {
                    yield return current;
                    current = new List<string>();
                    size = 0;
                }
                current.Add(item);
                size += cost;
            }
            if (current.Count > 0) yield return current;
        }

        private async Task<string[]> TranslateChunkAsync(List<string> chunk, string source, string target, CancellationToken ct)
        {
            var provider = ChooseProvider();
            var gate = Gate(provider);
            await gate.AcquireAsync(ct).ConfigureAwait(false);

            string[] result;
            try
            {
                result = provider == Proveedor.DeepL
                    ? await DeepLAsync(chunk, source, target, ct).ConfigureAwait(false)
                    : await GoogleAsync(chunk, source, target, ct).ConfigureAwait(false);
                gate.ReportSuccess();
            }
            catch (RateLimitedException ex)
            {
                var backoff = gate.ReportRateLimited(ex.RetryAfter);
                throw new RateLimitedException(provider, backoff);
            }
            catch (TransientTranslationException)
            {
                gate.ReportTransientError();
                throw;
            }
            catch (DeepLUnavailableException ex)
            {
                _deeplDisabledReason = ex.Message;
                Notice?.Invoke("DeepL no disponible (" + ex.Message + "). Se usa Google Translate hasta que vuelvas a conectar la clave.");
                return await TranslateChunkAsync(chunk, source, target, ct).ConfigureAwait(false);
            }
            catch (SplitMismatchException) when (chunk.Count > 1)
            {
                // La petición funcionó, solo falló el reparto por líneas: partimos el lote en dos.
                gate.ReportSuccess();
                int half = chunk.Count / 2;
                var a = await TranslateChunkAsync(chunk.GetRange(0, half), source, target, ct).ConfigureAwait(false);
                var b = await TranslateChunkAsync(chunk.GetRange(half, chunk.Count - half), source, target, ct).ConfigureAwait(false);
                return a.Concat(b).ToArray();
            }

            for (int i = 0; i < chunk.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(result[i])) result[i] = chunk[i];
                _cache.Set(CacheKey(chunk[i], source, target), result[i]);
            }
            return result;
        }

        // Endpoint público no oficial (translate.googleapis.com, client=gtx).
        // Varias líneas viajan juntas separadas por "\n" y se vuelven a separar en la respuesta.
        private async Task<string[]> GoogleAsync(List<string> texts, string source, string target, CancellationToken ct)
        {
            var joined = string.Join("\n", texts);
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=" + source +
                      "&tl=" + target + "&dt=t&q=" + Uri.EscapeDataString(joined);

            string body;
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var resp = await SendAsync(request, Proveedor.Google, ct).ConfigureAwait(false))
            {
                // Redirección a la página "sorry" (captcha) = bloqueo temporal.
                if (resp.RequestMessage?.RequestUri?.AbsolutePath.Contains("/sorry", StringComparison.OrdinalIgnoreCase) == true)
                    throw new RateLimitedException(Proveedor.Google, null);
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            if (body.Length == 0 || body[0] != '[')
                throw new TransientTranslationException("Google devolvió una respuesta inesperada.");

            string full;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array)
                    throw new TransientTranslationException("Google devolvió una respuesta sin traducción.");

                var sb = new StringBuilder(joined.Length + 16);
                foreach (var seg in root[0].EnumerateArray())
                {
                    if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                        sb.Append(seg[0].GetString());
                }
                full = sb.ToString();
            }
            catch (JsonException ex)
            {
                throw new TransientTranslationException("Respuesta de Google no válida.", ex);
            }

            if (texts.Count == 1) return new[] { full.Trim() };

            var parts = full.TrimEnd('\r', '\n').Split('\n');
            if (parts.Length != texts.Count) throw new SplitMismatchException();
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        private async Task<string[]> DeepLAsync(List<string> texts, string source, string target, CancellationToken ct)
        {
            var key = _deeplKey ?? throw new DeepLUnavailableException("sin clave");

            var form = new List<KeyValuePair<string, string>>(texts.Count + 2);
            foreach (var t in texts) form.Add(new KeyValuePair<string, string>("text", t));
            // Lo que escribes tú está siempre en español: indicarlo evita que DeepL confunda frases
            // cortas ("vale", "venga") con otro idioma. Lo que llega del juego se deja en automático
            // porque cada jugador puede escribir en un idioma distinto.
            if (source.Equals("es", StringComparison.OrdinalIgnoreCase))
                form.Add(new KeyValuePair<string, string>("source_lang", "ES"));
            form.Add(new KeyValuePair<string, string>("target_lang",
                target.Equals("es", StringComparison.OrdinalIgnoreCase) ? "ES" : "EN-US"));

            using var request = new HttpRequestMessage(HttpMethod.Post, DeepLBaseUrl(key) + "/v2/translate")
            {
                Content = new FormUrlEncodedContent(form)
            };
            request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);

            using var resp = await SendAsync(request, Proveedor.DeepL, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var arr = doc.RootElement.GetProperty("translations");
                if (arr.GetArrayLength() != texts.Count)
                    throw new TransientTranslationException("DeepL devolvió un número de traducciones distinto.");

                var result = new string[texts.Count];
                for (int i = 0; i < texts.Count; i++)
                    result[i] = arr[i].GetProperty("text").GetString() ?? texts[i];
                return result;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new TransientTranslationException("Respuesta de DeepL no válida.", ex);
            }
        }

        // Las claves gratuitas terminan en ":fx" y van a api-free; las de pago, a api.deepl.com.
        private static string DeepLBaseUrl(string key) =>
            key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase) ? "https://api-free.deepl.com" : "https://api.deepl.com";

        // Envía la petición y traduce los códigos HTTP a nuestras excepciones.
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Proveedor provider, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TransientTranslationException("tiempo de espera agotado");
            }
            catch (HttpRequestException ex)
            {
                throw new TransientTranslationException("sin conexión (" + ex.Message + ")", ex);
            }

            if (resp.IsSuccessStatusCode) return resp;

            int status = (int)resp.StatusCode;
            var retryAfter = GetRetryAfter(resp);
            string detail = "";
            try { detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { /* da igual */ }
            resp.Dispose();

            if (status == 429 || (provider == Proveedor.Google && status == 503))
                throw new RateLimitedException(provider, retryAfter);

            if (provider == Proveedor.DeepL)
            {
                if (status == 456) throw new DeepLUnavailableException("cuota de caracteres agotada");
                if (status == 401 || status == 403) throw new DeepLUnavailableException("clave no válida");
            }

            if (status >= 500) throw new TransientTranslationException($"el servidor respondió {status}");
            throw new TranslationException($"{provider} respondió {status}: {Shorten(detail)}");
        }

        private static TimeSpan? GetRetryAfter(HttpResponseMessage resp)
        {
            var ra = resp.Headers.RetryAfter;
            if (ra == null) return null;

            TimeSpan? value = null;
            if (ra.Delta is TimeSpan delta) value = delta;
            else if (ra.Date is DateTimeOffset date) value = date - DateTimeOffset.UtcNow;

            if (value == null) return null;
            if (value < TimeSpan.Zero) return TimeSpan.Zero;
            return value > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : value;
        }

        private static string Shorten(string s) => s.Length <= 200 ? s : s.Substring(0, 200) + "…";
    }
}
