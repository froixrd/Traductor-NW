using System.Drawing.Text;

namespace TraductorNWN
{
    // Fuentes de la interfaz. Todas vienen con Windows 10/11; si alguna falta se usa otra parecida.
    //  - Sitka Text: el chat (serif hecha para leer en pantalla).
    //  - Sitka Heading: el título.
    //  - Bahnschrift: la interfaz (estrecha y clara en tamaños pequeños).
    // Las fuentes se reutilizan (caché) en vez de crear una nueva cada vez: así no se gastan recursos GDI.
    internal static class UiFonts
    {
        private static readonly HashSet<string> Installed = LoadInstalled();
        private static readonly Dictionary<(string, float, FontStyle), Font> Cache = new();
        private static readonly object Lock = new();

        public static readonly string UiFamily = Pick("Bahnschrift", "Segoe UI");
        public static readonly string SerifFamily = Pick("Sitka Text", "Georgia", "Segoe UI");
        public static readonly string HeadingFamily = Pick("Sitka Heading", "Sitka Text", "Georgia", "Segoe UI");
        private static readonly string? StrongFamily = PickOptional("Bahnschrift SemiBold", "Segoe UI Semibold");

        private static HashSet<string> LoadInstalled()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var fonts = new InstalledFontCollection();
                foreach (var family in fonts.Families) set.Add(family.Name);
            }
            catch { /* sin acceso a las fuentes: se usarán las de respaldo */ }
            return set;
        }

        private static string Pick(params string[] names) => PickOptional(names) ?? "Segoe UI";

        private static string? PickOptional(params string[] names)
        {
            foreach (var n in names)
                if (Installed.Contains(n)) return n;
            return null;
        }

        public static Font Get(string family, float points, FontStyle style = FontStyle.Regular)
        {
            var key = (family, points, style);
            lock (Lock)
            {
                if (!Cache.TryGetValue(key, out var font))
                {
                    font = new Font(family, points, style, GraphicsUnit.Point);
                    Cache[key] = font;
                }
                return font;
            }
        }

        public static Font Ui(float points) => Get(UiFamily, points);

        // Seminegrita para nombres, pestañas y botones.
        public static Font UiStrong(float points) =>
            StrongFamily != null ? Get(StrongFamily, points) : Get(UiFamily, points, FontStyle.Bold);

        public static Font Serif(float points, FontStyle style = FontStyle.Regular) => Get(SerifFamily, points, style);

        public static Font Heading(float points) => Get(HeadingFamily, points, FontStyle.Bold);
    }

    // Escalado para pantallas con zoom (125 %, 150 %…): todas las medidas en píxeles pasan por aquí.
    internal static class Dpi
    {
        public static readonly float Factor = ReadFactor();

        private static float ReadFactor()
        {
            try
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                return Math.Max(1f, g.DpiX / 96f);
            }
            catch
            {
                return 1f;
            }
        }

        public static int S(int px) => (int)Math.Round(px * Factor);
        public static float S(float px) => px * Factor;
    }
}
