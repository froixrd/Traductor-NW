using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TraductorNWN
{
    // Sigue el log del cliente de NWN (nwclientLog1.txt) y entrega las líneas nuevas completas.
    //
    // Mejoras respecto a la versión anterior:
    //  - Lee el tamaño desde nuestro propio handle (el tamaño que da FileInfo puede ir desfasado
    //    mientras el juego escribe, y eso hacía releer el log entero desde el principio).
    //  - Detecta la rotación del log al arrancar una partida nueva comparando el ID del archivo,
    //    en vez de volver a leer (y traducir) toda la sesión anterior.
    //  - Solo entrega líneas completas: una línea a medio escribir se guarda hasta que llegue el resto.
    internal sealed class LogWatcher
    {
        private const string LogFileName = "nwclientLog1.txt";
        private const int PollMs = 150;
        private const int IdentityCheckMs = 1000;
        private const int PartialFlushMs = 600;
        // Tras una rotación solo releemos el log nuevo desde el principio si es pequeño
        // (una sesión que acaba de empezar). Si es grande, saltamos al final.
        private const long MaxReplayBytes = 64 * 1024;

        private static readonly Encoding LogEncoding = CreateEncoding();

        private readonly Action<IReadOnlyList<string>> _onLines;
        private readonly Action<string, bool> _onStatus;

        private readonly record struct FileId(uint Volume, ulong Index)
        {
            public bool IsEmpty => Volume == 0 && Index == 0;
        }

        public LogWatcher(Action<IReadOnlyList<string>> onLines, Action<string, bool> onStatus)
        {
            _onLines = onLines;
            _onStatus = onStatus;
        }

        private static Encoding CreateEncoding()
        {
            // Windows-1252 = Latin1 + comillas tipográficas, €, …  (lo que escribe el cliente en Windows).
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1252);
            }
            catch
            {
                return Encoding.Latin1;
            }
        }

        // Busca el log en las ubicaciones habituales (incluida la carpeta Documentos redirigida
        // a OneDrive, aunque esté en español) y se queda con el que se escribió más recientemente.
        public static string? FindLogPath() =>
            GameFolders.MostRecent(GameFolders.UserDirectories().Select(d => Path.Combine(d, "logs", LogFileName)));

        public async Task RunAsync(CancellationToken ct)
        {
            _onStatus("Buscando el log de Neverwinter Nights…", false);

            FileStream? fs = null;
            string? path = null;
            var fileId = default(FileId);
            long pos = 0;
            bool firstOpen = true;
            var buffer = new byte[64 * 1024];
            var assembler = new LineAssembler();
            long lastIdentityCheck = Environment.TickCount64;
            long lastDataAt = Environment.TickCount64;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // ---------- (Re)abrir el log ----------
                    if (fs == null)
                    {
                        path = FindLogPath();
                        if (path == null)
                        {
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                            continue;
                        }

                        try
                        {
                            fs = OpenShared(path);
                            var newId = GetFileId(fs.SafeFileHandle);
                            long len = fs.Length;

                            if (firstOpen)
                            {
                                pos = len; // al arrancar no traducimos el historial
                                _onStatus("Log encontrado. Traduciendo en tiempo real.", true);
                            }
                            else if (!newId.IsEmpty && newId == fileId)
                            {
                                pos = Math.Min(pos, len); // mismo archivo tras un error de E/S: seguimos donde íbamos
                            }
                            else
                            {
                                pos = len <= MaxReplayBytes ? 0 : len;
                                assembler.Reset();
                                _onStatus("Partida nueva detectada; siguiendo el log nuevo.", false);
                            }

                            fileId = newId;
                            firstOpen = false;
                            lastIdentityCheck = Environment.TickCount64;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            fs?.Dispose();
                            fs = null;
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                            continue;
                        }
                    }

                    // ---------- Leer lo nuevo ----------
                    bool readSomething = false;
                    try
                    {
                        long len = fs.Length;
                        if (len < pos)
                        {
                            // Truncado en el sitio: empezamos desde el principio del contenido nuevo.
                            pos = len <= MaxReplayBytes ? 0 : len;
                            assembler.Reset();
                        }

                        if (len > pos)
                        {
                            fs.Seek(pos, SeekOrigin.Begin);
                            var lines = new List<string>();
                            while (pos < len)
                            {
                                int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, len - pos));
                                if (n <= 0) break;
                                pos += n;
                                assembler.Feed(buffer.AsSpan(0, n), lines);
                            }
                            if (lines.Count > 0) _onLines(lines);
                            readSomething = true;
                            lastDataAt = Environment.TickCount64;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                    {
                        fs.Dispose();
                        fs = null;
                        continue;
                    }

                    if (readSomething) continue; // puede haber más; volvemos a mirar sin esperar

                    long now = Environment.TickCount64;

                    // Una línea sin salto final que lleva un rato quieta se entrega igualmente
                    // (por si el juego escribe el salto de línea al principio del siguiente mensaje).
                    if (assembler.HasPartial && now - lastDataAt >= PartialFlushMs)
                    {
                        var partial = assembler.FlushPartial();
                        if (partial != null) _onLines(new[] { partial });
                    }

                    // ¿Sigue siendo el mismo archivo el que hay en la ruta? (rotación del log)
                    if (now - lastIdentityCheck >= IdentityCheckMs)
                    {
                        lastIdentityCheck = now;
                        if (path != null && HasBeenReplaced(path, fileId))
                        {
                            fs.Dispose();
                            fs = null;
                            continue;
                        }
                    }

                    await Task.Delay(PollMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // cierre de la aplicación
            }
            finally
            {
                fs?.Dispose();
            }
        }

        private static FileStream OpenShared(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);

        private static FileId GetFileId(SafeFileHandle handle)
        {
            if (NativeMethods.GetFileInformationByHandle(handle, out var info))
                return new FileId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            return default;
        }

        private static bool HasBeenReplaced(string path, FileId current)
        {
            if (current.IsEmpty) return false; // no pudimos identificarlo: mejor no arriesgarse a releer
            try
            {
                using var probe = OpenShared(path);
                var id = GetFileId(probe.SafeFileHandle);
                return !id.IsEmpty && id != current;
            }
            catch
            {
                return false; // en plena rotación puede no existir un instante; lo veremos en la próxima comprobación
            }
        }

        // Junta los bytes leídos en líneas completas (separadas por \n).
        private sealed class LineAssembler
        {
            private const int MaxLineBytes = 1024 * 1024;
            private byte[] _buf = new byte[4096];
            private int _len;

            public bool HasPartial => _len > 0;

            public void Reset() => _len = 0;

            public void Feed(ReadOnlySpan<byte> data, List<string> lines)
            {
                while (data.Length > 0)
                {
                    int nl = data.IndexOf((byte)'\n');
                    if (nl < 0)
                    {
                        Append(data);
                        return;
                    }
                    Append(data.Slice(0, nl));
                    lines.Add(Decode());
                    _len = 0;
                    data = data.Slice(nl + 1);
                }
            }

            public string? FlushPartial()
            {
                if (_len == 0) return null;
                var s = Decode();
                _len = 0;
                return s;
            }

            private void Append(ReadOnlySpan<byte> s)
            {
                if (_len + s.Length > MaxLineBytes)
                {
                    _len = 0; // basura sin saltos de línea: se descarta
                    return;
                }
                if (_len + s.Length > _buf.Length)
                    Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + s.Length));
                s.CopyTo(_buf.AsSpan(_len));
                _len += s.Length;
            }

            private string Decode()
            {
                var span = _buf.AsSpan(0, _len);
                if (span.Length > 0 && span[span.Length - 1] == (byte)'\r')
                    span = span.Slice(0, span.Length - 1);
                return LogEncoding.GetString(span);
            }
        }
    }
}
