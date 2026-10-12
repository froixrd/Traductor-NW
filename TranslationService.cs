using System.Net;
using System.Text;
using System.Text.Json;

namespace TraductorNWN
{
    internal enum Proveedor { Google, DeepL, Microsoft }

    internal class TranslationException : Exception
    {
        public TranslationException(string message, Exception? inner = null) : base(message, inner) { }
    }

    // El proveedor nos está limitando (429 o bloqueo). RetryAfter = pausa efectiva aplicada.
    internal sealed class RateLimitedException : TranslationException
    {
        public RateLimitedException(Proveedor provider, TimeSpan? retryAfter)
            : base($"{TranslationService.Name(provider)} ha limitado las peticiones.")
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

    // Un proveedor con clave no se puede usar: se sigue con Google.
    //  QuotaExhausted = se acabó la cuota gratuita (vuelve sola más adelante);
    //  si no, la clave no es válida (hay que corregirla en Ajustes).
    internal sealed class ProviderUnavailableException : TranslationException
    {
        public ProviderUnavailableException(Proveedor provider, string message, bool quotaExhausted = false) : base(message)
        {
            Provider = provider;
            QuotaExhausted = quotaExhausted;
        }

        public Proveedor Provider { get; }
        public bool QuotaExhausted { get; }
    }

    internal enum ProviderState { Ok, NoKey, InvalidKey, QuotaExhausted }

    // La traducción agrupada de Google no devolvió el mismo número de líneas.
    internal sealed class SplitMismatchException : TranslationException
    {
        public SplitMismatchException() : base("La respuesta agrupada no coincide con el número de líneas.") { }
    }

    internal readonly record struct DeepLUsage(long Count, long Limit);

    // Motor de traducción:
    //  - tres proveedores: Google (gratis, sin clave, no oficial), DeepL y Microsoft Translator (con clave);
    //  - agrupa varias líneas en UNA petición;
    //  - caché LRU para no repetir peticiones;
    //  - un RateGate por proveedor con espera progresiva y respeto del Retry-After;
    //  - si el proveedor elegido está bloqueado o sin cuota, usa Google (y si Google está bloqueado, otro configurado);
    //  - cuando se acaba la cuota gratuita de Microsoft/DeepL, vuelve a probarlo cada hora;
    //  - abreviaturas de chat escritas completas y palabras protegidas que nunca se traducen.
    internal sealed class TranslationService : IDisposable
    {
        private const int MaxItemsPorPeticion = 40;
        private const int MaxQueryChars = 6000; // tamaño máximo (ya codificado) del texto en la URL de Google

        private sealed class KeyState
        {
            public volatile string? Key;
            public volatile string? Region;
            public volatile string? DisabledReason;   // clave no válida: hasta que se compruebe otra
            public long ExhaustedUntilTicks;          // cuota agotada: no se usa hasta esta hora (UTC)
            public int QuotaNotified;                 // 1 = ya se avisó de la cuota agotada
        }

        // Cada cuánto se vuelve a probar un proveedor sin cuota (la cuota gratuita se renueva sola).
        private static readonly TimeSpan QuotaRetry = TimeSpan.FromHours(1);

        // Orden de respaldo: Google primero (gratis y sin clave), luego los que tengan clave.
        private static readonly Proveedor[] FallbackOrder = { Proveedor.Google, Proveedor.Microsoft, Proveedor.DeepL };

        private readonly HttpClient _http;
        private readonly LruCache<string, string> _cache = new(5000, StringComparer.Ordinal);

        // Google es un servicio público no oficial: una petición por segundo como mucho (las líneas que
        // lleguen mientras tanto viajan juntas en la siguiente), para no provocar bloqueos.
        private readonly RateGate _googleGate = new(TimeSpan.FromMilliseconds(1000));
        private readonly RateGate _deeplGate = new(TimeSpan.FromMilliseconds(200));
        private readonly RateGate _microsoftGate = new(TimeSpan.FromMilliseconds(200));

        private readonly KeyState _deepl = new();
        private readonly KeyState _microsoft = new();
        private volatile Proveedor _preferred = Proveedor.Google;
        private volatile ProtectedTerms _terms = ProtectedTerms.Empty;

        // Avisos para mostrar al usuario: (texto, esAviso). P. ej. "Microsoft sin cuota, se usa Google".
        public event Action<string, bool>? Notice;

        // Caracteres enviados a un proveedor en una traducción correcta (para el contador de uso).
        public event Action<Proveedor, int>? CharactersUsed;

        // Reloj inyectable para las pruebas.
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

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

        public static string Name(Proveedor p) => p switch
        {
            Proveedor.DeepL => "DeepL",
            Proveedor.Microsoft => "Microsoft",
            _ => "Google"
        };

        // ---------------- Configuración ----------------

        public Proveedor Preferred
        {
            get => _preferred;
            set => _preferred = value;
        }

        public void SetKey(Proveedor provider, string? key, string? region = null)
        {
            if (provider == Proveedor.Google) return;
            var st = State(provider);
            st.Key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            st.Region = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
            st.DisabledReason = null;
            Interlocked.Exchange(ref st.ExhaustedUntilTicks, 0);
            Interlocked.Exchange(ref st.QuotaNotified, 0);
        }

        public string? GetKey(Proveedor provider) => provider == Proveedor.Google ? null : State(provider).Key;

        public bool IsUsable(Proveedor provider) => GetState(provider) == ProviderState.Ok;

        public ProviderState GetState(Proveedor provider)
        {
            if (provider == Proveedor.Google) return ProviderState.Ok;
            var st = State(provider);
            if (st.Key == null) return ProviderState.NoKey;
            if (st.DisabledReason != null) return ProviderState.InvalidKey;
            if (UtcNow().Ticks < Interlocked.Read(ref st.ExhaustedUntilTicks)) return ProviderState.QuotaExhausted;
            return ProviderState.Ok;
        }

        // ¿El proveedor está en pausa por exceso de peticiones (429)?
        public bool IsPaused(Proveedor provider) => Gate(provider).CooldownRemaining > TimeSpan.FromSeconds(2);

        // Compatibilidad: poner una clave de DeepL la convierte en el proveedor preferido.
        public string? DeepLKey
        {
            get => _deepl.Key;
            set
            {
                SetKey(Proveedor.DeepL, value);
                if (_deepl.Key != null) _preferred = Proveedor.DeepL;
                else if (_preferred == Proveedor.DeepL) _preferred = Proveedor.Google;
            }
        }

        public bool DeepLUsable => IsUsable(Proveedor.DeepL);

        public void SetProtectedTerms(IEnumerable<string> terms)
        {
            _terms = new ProtectedTerms(terms);
            _cache.Clear(); // las traducciones guardadas pueden haber traducido esas palabras
        }

        public Proveedor ActiveProvider => ChooseProvider();
        public TimeSpan CooldownRemaining => Gate(ChooseProvider()).CooldownRemaining;
        public TimeSpan TimeUntilReady => Gate(ChooseProvider()).TimeUntilReady;

        public void Dispose() => _http.Dispose();

        private KeyState State(Proveedor p) => p == Proveedor.Microsoft ? _microsoft : _deepl;

        private RateGate Gate(Proveedor p) => p switch
        {
            Proveedor.DeepL => _deeplGate,
            Proveedor.Microsoft => _microsoftGate,
            _ => _googleGate
        };

        private Proveedor ChooseProvider()
        {
            // El elegido, si tiene clave válida y cuota; si no, Google.
            var preferred = IsUsable(_preferred) ? _preferred : Proveedor.Google;
            var wait = Gate(preferred).CooldownRemaining;
            if (wait <= TimeSpan.FromSeconds(2)) return preferred;

            // En pausa larga (bloqueo o exceso de peticiones): mientras tanto, el primero libre en el
            // orden de respaldo (Google primero).
            var best = preferred;
            foreach (var p in FallbackOrder)
            {
                if (p == preferred || !IsUsable(p)) continue;
                var w = Gate(p).CooldownRemaining;
                if (w <= TimeSpan.FromSeconds(2)) return p;
                if (w < wait)
                {
                    best = p;
                    wait = w;
                }
            }
            return best;
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

        // Comprueba una clave de Microsoft traduciendo una palabra.
        public async Task CheckMicrosoftKeyAsync(string key, string? region, CancellationToken ct)
        {
            await MicrosoftRequestAsync(new List<string> { "Hello" }, "en", "es", key.Trim(), region, ct).ConfigureAwait(false);
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
            // Lo que se manda al traductor: abreviaturas completas (solo en inglés) y palabras protegidas.
            var terms = _terms;
            var prepared = new List<string>(chunk.Count);
            var tokens = new List<string>?[chunk.Count];
            for (int i = 0; i < chunk.Count; i++)
            {
                var t = source.Equals("en", StringComparison.OrdinalIgnoreCase) ? ChatSlang.Expand(chunk[i]) : chunk[i];
                (var p, tokens[i]) = terms.Protect(t);
                prepared.Add(p);
            }

            var provider = ChooseProvider();
            var gate = Gate(provider);
            await gate.AcquireAsync(ct).ConfigureAwait(false);

            string[] result;
            try
            {
                result = provider switch
                {
                    Proveedor.DeepL => await DeepLAsync(prepared, source, target, ct).ConfigureAwait(false),
                    Proveedor.Microsoft => await MicrosoftAsync(prepared, source, target, ct).ConfigureAwait(false),
                    _ => await GoogleAsync(prepared, source, target, ct).ConfigureAwait(false)
                };
                gate.ReportSuccess();
                OnProviderWorked(provider);
                CharactersUsed?.Invoke(provider, prepared.Sum(t => t.Length));
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
            catch (ProviderUnavailableException ex)
            {
                MarkUnavailable(ex);
                // El mismo lote se traduce enseguida con el siguiente (Google): no se pierde ningún mensaje.
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
                var r = ProtectedTerms.Restore(result[i] ?? "", tokens[i]);
                if (string.IsNullOrWhiteSpace(r)) r = chunk[i];
                result[i] = r;
                _cache.Set(CacheKey(chunk[i], source, target), r);
            }
            return result;
        }

        private void MarkUnavailable(ProviderUnavailableException ex)
        {
            var st = State(ex.Provider);
            var name = Name(ex.Provider);
            if (ex.QuotaExhausted)
            {
                Interlocked.Exchange(ref st.ExhaustedUntilTicks, (UtcNow() + QuotaRetry).Ticks);
                if (Interlocked.Exchange(ref st.QuotaNotified, 1) == 0)
                    Notice?.Invoke($"Se ha acabado la cuota gratuita de {name}. Mientras tanto se traduce con Google; " +
                                   $"cada hora se vuelve a probar {name} y se usa en cuanto se renueve.", true);
            }
            else
            {
                st.DisabledReason = ex.Message;
                Notice?.Invoke($"{name} no acepta la clave ({ex.Message}). Se traduce con Google hasta que la corrijas en Ajustes.", true);
            }
        }

        private void OnProviderWorked(Proveedor provider)
        {
            if (provider == Proveedor.Google) return;
            var st = State(provider);
            Interlocked.Exchange(ref st.ExhaustedUntilTicks, 0);
            if (Interlocked.Exchange(ref st.QuotaNotified, 0) == 1)
                Notice?.Invoke($"{Name(provider)} vuelve a estar disponible: se traduce otra vez con {Name(provider)}.", false);
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

            var trimmed = body.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '[')
            {
                // Una página HTML en vez de datos es el aviso de "tráfico inusual": bloqueo, no un fallo puntual.
                // Antes se reintentaba a los 2 s y eso alargaba el bloqueo.
                if (trimmed.StartsWith('<') || trimmed.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase))
                    throw new RateLimitedException(Proveedor.Google, null);
                throw new TransientTranslationException("Google devolvió una respuesta inesperada.");
            }

            string full;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
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
            var key = _deepl.Key ?? throw new ProviderUnavailableException(Proveedor.DeepL, "sin clave");

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

        private Task<string[]> MicrosoftAsync(List<string> texts, string source, string target, CancellationToken ct)
        {
            var key = _microsoft.Key ?? throw new ProviderUnavailableException(Proveedor.Microsoft, "sin clave");
            return MicrosoftRequestAsync(texts, source, target, key, _microsoft.Region, ct);
        }

        // Microsoft Translator v3: una lista de textos en JSON y una traducción por texto, en el mismo
        // orden (sin trucos de saltos de línea). Lo que llega del juego se deja con detección automática
        // por mensaje; lo que escribes tú se marca como español.
        private async Task<string[]> MicrosoftRequestAsync(List<string> texts, string source, string target,
            string key, string? region, CancellationToken ct)
        {
            var url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + target;
            if (source.Equals("es", StringComparison.OrdinalIgnoreCase)) url += "&from=es";

            var payload = JsonSerializer.Serialize(texts.Select(t => new { Text = t }));
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
            if (!string.IsNullOrWhiteSpace(region) && !region.Equals("global", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", region.Trim());

            using var resp = await SendAsync(request, Proveedor.Microsoft, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != texts.Count)
                    throw new TransientTranslationException("Microsoft devolvió un número de traducciones distinto.");

                var result = new string[texts.Count];
                for (int i = 0; i < texts.Count; i++)
                    result[i] = root[i].GetProperty("translations")[0].GetProperty("text").GetString() ?? texts[i];
                return result;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                throw new TransientTranslationException("Respuesta de Microsoft no válida.", ex);
            }
        }

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

            if (status == 429) throw new RateLimitedException(provider, retryAfter);

            switch (provider)
            {
                case Proveedor.Google:
                    // Google también bloquea con 403 o 503 (antes un 403 no hacía esperar y se seguía insistiendo).
                    if (status is 403 or 503) throw new RateLimitedException(provider, retryAfter);
                    break;
                case Proveedor.DeepL:
                    if (status == 456) throw new ProviderUnavailableException(provider, "cuota de caracteres agotada", quotaExhausted: true);
                    if (status is 401 or 403) throw new ProviderUnavailableException(provider, "clave no válida");
                    break;
                case Proveedor.Microsoft:
                    if (status == 401) throw new ProviderUnavailableException(provider, "clave o región no válidas");
                    if (status == 403)
                    {
                        // 403001 = se acabó la cuota gratuita (plan F0): no cobra, solo deja de traducir hasta que se renueva.
                        var code = MicrosoftErrorCode(detail);
                        bool quota = code == 403001 || (code == null && detail.Contains("quota", StringComparison.OrdinalIgnoreCase));
                        throw quota
                            ? new ProviderUnavailableException(provider, "cuota gratuita agotada", quotaExhausted: true)
                            : new ProviderUnavailableException(provider, "la clave no tiene permiso o la suscripción está desactivada");
                    }
                    break;
            }

            if (status >= 500) throw new TransientTranslationException($"el servidor respondió {status}");
            throw new TranslationException($"{Name(provider)} respondió {status}: {Shorten(detail)}");
        }

        // Código de error de Microsoft: {"error":{"code":403001,"message":"..."}}
        private static int? MicrosoftErrorCode(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.TryGetProperty("code", out var code) && code.TryGetInt32(out var value))
                    return value;
            }
            catch (JsonException) { /* sin cuerpo JSON */ }
            return null;
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
