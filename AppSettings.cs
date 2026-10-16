using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraductorNWN
{
    // Configuración persistente en %AppData%\TraductorNWN\config.json.
    // Las claves de DeepL y Microsoft se guardan cifradas con DPAPI (solo tu usuario de Windows puede leerlas),
    // en vez de en texto plano junto al .exe como antes.
    internal sealed class AppSettings
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TraductorNWN.v1");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraductorNWN");

        private static string FilePath => Path.Combine(Folder, "config.json");

        // Archivo de la versión anterior (clave en texto plano al lado del .exe). Se migra y se borra.
        private static string LegacyFile => Path.Combine(AppContext.BaseDirectory, "config_traductor.txt");

        // Versión del archivo de configuración (para migrar ajustes de versiones anteriores).
        public int Version { get; set; }
        public string? DeepLKeyCifrada { get; set; }
        public bool Mayusculas { get; set; }
        public bool FiltrarSistema { get; set; } = true;
        public int RetardoEnvioMs { get; set; } = 2000;
        public int[]? Ventana { get; set; }
        public string Tema { get; set; } = "taberna";
        public string? Proveedor { get; set; }          // "Microsoft" (por defecto) | "Google" | "DeepL"
        public bool SiempreEncima { get; set; } = true;
        public bool VolverAlTraductor { get; set; } = true;
        public string? MicrosoftKeyCifrada { get; set; }
        public string? MicrosoftRegion { get; set; }
        public bool GoogleDeRespaldo { get; set; } = true;   // usar Google si Microsoft y DeepL no funcionan
        public List<string> NoTraducir { get; set; } = new();
        public bool AvisarPrivados { get; set; } = true;
        public bool TeclaRapida { get; set; } = true;
        public bool Compacto { get; set; }
        public int OpacidadInactiva { get; set; } = 100;   // % cuando la ventana no está activa

        // Contador aproximado de caracteres enviados a Microsoft en el mes en curso ("yyyy-MM").
        public string? MicrosoftMes { get; set; }
        public long MicrosoftCaracteres { get; set; }

        private const int CurrentVersion = 2;
        private readonly object _lock = new();

        // true si en este arranque se pasó a Microsoft como traductor principal (para guiar al usuario).
        [JsonIgnore]
        public bool MigradoAMicrosoft { get; private set; }

        [JsonIgnore]
        public string? MicrosoftKey { get; set; }

        [JsonIgnore]
        public string? DeepLKey { get; set; }

        public static AppSettings Load()
        {
            AppSettings s;
            try
            {
                s = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings()
                    : new AppSettings();
            }
            catch
            {
                s = new AppSettings();
            }

            if (!string.IsNullOrEmpty(s.DeepLKeyCifrada))
                s.DeepLKey = Unprotect(s.DeepLKeyCifrada);
            if (!string.IsNullOrEmpty(s.MicrosoftKeyCifrada))
                s.MicrosoftKey = Unprotect(s.MicrosoftKeyCifrada);

            try
            {
                if (File.Exists(LegacyFile))
                {
                    var legacy = File.ReadAllText(LegacyFile).Trim();
                    if (string.IsNullOrEmpty(s.DeepLKey) && legacy.Length > 0) s.DeepLKey = legacy;
                    s.Save();
                    File.Delete(LegacyFile);
                }
            }
            catch { /* si no se puede borrar, se reintentará en el próximo arranque */ }

            s.RetardoEnvioMs = Math.Clamp(s.RetardoEnvioMs, 0, 5_000);
            s.OpacidadInactiva = Math.Clamp(s.OpacidadInactiva, 40, 100);
            s.NoTraducir ??= new List<string>();
            Migrate(s);
            return s;
        }

        // Microsoft Translator pasa a ser el traductor principal (gratis hasta 2 millones de caracteres
        // al mes) y Google el respaldo. Solo se respeta DeepL si ya tenía una clave guardada.
        internal static void Migrate(AppSettings s)
        {
            if (s.Version < CurrentVersion)
            {
                bool keepDeepL = string.Equals(s.Proveedor, "DeepL", StringComparison.OrdinalIgnoreCase) &&
                                 !string.IsNullOrEmpty(s.DeepLKey);
                if (!keepDeepL && !string.Equals(s.Proveedor, "Microsoft", StringComparison.OrdinalIgnoreCase))
                {
                    s.Proveedor = "Microsoft";
                    s.MigradoAMicrosoft = true;
                }
                s.Version = CurrentVersion;
            }
            s.Proveedor ??= "Microsoft";
        }

        // Suma caracteres al contador del mes (se reinicia solo al cambiar de mes).
        public void AddMicrosoftUsage(int chars, DateTime? now = null)
        {
            var month = (now ?? DateTime.Now).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            lock (_lock)
            {
                if (MicrosoftMes != month)
                {
                    MicrosoftMes = month;
                    MicrosoftCaracteres = 0;
                }
                MicrosoftCaracteres += Math.Max(0, chars);
            }
        }

        public long MicrosoftUsageThisMonth(DateTime? now = null)
        {
            var month = (now ?? DateTime.Now).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            lock (_lock) return MicrosoftMes == month ? MicrosoftCaracteres : 0;
        }

        public void Save()
        {
            try
            {
                DeepLKeyCifrada = string.IsNullOrEmpty(DeepLKey) ? null : Protect(DeepLKey);
                MicrosoftKeyCifrada = string.IsNullOrEmpty(MicrosoftKey) ? null : Protect(MicrosoftKey);
                string json;
                lock (_lock) json = JsonSerializer.Serialize(this, JsonOptions);
                Directory.CreateDirectory(Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { /* no crítico */ }
        }

        private static string Protect(string plain) =>
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

        private static string? Unprotect(string cipher)
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return null;
            }
        }
    }
}
