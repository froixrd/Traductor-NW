namespace TraductorNWN
{
    // Carpeta de usuario del juego (Documentos\Neverwinter Nights), donde están los logs y settings.tml.
    internal static class GameFolders
    {
        // Ubicaciones habituales, incluida la carpeta Documentos redirigida a OneDrive (también en español).
        public static IEnumerable<string> UserDirectories()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            var list = new List<string>();
            if (!string.IsNullOrEmpty(myDocs))
                list.Add(Path.Combine(myDocs, "Neverwinter Nights"));
            if (!string.IsNullOrEmpty(userProfile))
            {
                list.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Neverwinter Nights"));
                list.Add(Path.Combine(userProfile, "OneDrive", "Documentos", "Neverwinter Nights"));
                list.Add(Path.Combine(userProfile, "Documents", "Neverwinter Nights"));
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        // De varios archivos candidatos, el que se escribió más recientemente.
        public static string? MostRecent(IEnumerable<string> paths)
        {
            string? best = null;
            var bestTime = DateTime.MinValue;
            foreach (var p in paths)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var t = File.GetLastWriteTimeUtc(p);
                    if (best == null || t > bestTime)
                    {
                        best = p;
                        bestTime = t;
                    }
                }
                catch { /* sin permisos, ruta rara… se ignora */ }
            }
            return best;
        }
    }

    internal enum ChatLogging { Enabled, Disabled, Unknown }

    // NWN:EE solo escribe el chat en el log si está activado en settings.tml:
    //   [game.log.chat.all]
    //   enabled = true
    // Si no lo está, el traductor no recibe nada; así se puede avisar al usuario de cómo activarlo.
    internal static class GameSettings
    {
        public const string SectionAll = "[game.log.chat.all]";
        public const string SectionText = "[game.log.chat.text]";

        public static (ChatLogging State, string? Path) CheckChatLogging()
        {
            var path = GameFolders.MostRecent(GameFolders.UserDirectories().Select(d => System.IO.Path.Combine(d, "settings.tml")));
            if (path == null) return (ChatLogging.Unknown, null);
            try
            {
                var text = File.ReadAllText(path);
                return (Parse(text, SectionAll) == true ? ChatLogging.Enabled : ChatLogging.Disabled, path);
            }
            catch
            {
                return (ChatLogging.Unknown, path);
            }
        }

        // Valor de "enabled" en la sección indicada; null si no aparece.
        public static bool? Parse(string toml, string section)
        {
            bool inSection = false;
            foreach (var raw in toml.Split('\n'))
            {
                var line = raw.Trim();
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash).Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith('['))
                {
                    inSection = string.Equals(line, section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!line.Substring(0, eq).Trim().Equals("enabled", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line.Substring(eq + 1).Trim();
                return value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            return null;
        }
    }
}
