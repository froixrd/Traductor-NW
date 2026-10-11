using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraductorNWN
{
    // Configuración persistente en %AppData%\TraductorNWN\config.json.
    // La clave de DeepL se guarda cifrada con DPAPI (solo tu usuario de Windows puede leerla),
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

        public string? DeepLKeyCifrada { get; set; }
        public bool Mayusculas { get; set; }
        public bool FiltrarSistema { get; set; } = true;
        public int RetardoEnvioMs { get; set; } = 2000;
        public int[]? Ventana { get; set; }

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

            s.RetardoEnvioMs = Math.Clamp(s.RetardoEnvioMs, 0, 10_000);
            return s;
        }

        public void Save()
        {
            try
            {
                DeepLKeyCifrada = string.IsNullOrEmpty(DeepLKey) ? null : Protect(DeepLKey);
                Directory.CreateDirectory(Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
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
