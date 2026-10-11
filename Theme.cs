namespace TraductorNWN
{
    // Papel de cada color de texto. Los controles piden un "tono" y el tema decide el color,
    // así cambiar de tema es repintar, sin tocar cada control a mano.
    internal enum Tone
    {
        Text, Dim, Accent, OnAccent, Emote, Ok,
        Warn,   // texto de aviso (legible)
        Alert,  // aviso intenso: puntos e indicadores
        Placeholder,
        TagPrivate, TagParty, TagShout, TagDm, TagOther, TagWarn
    }

    // Papel de cada color de fondo.
    internal enum Surface { App, Raised, Field, Border, Line }

    internal interface IThemed
    {
        void ApplyTheme(AppTheme theme);
    }

    internal sealed class AppTheme
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";

        public Color App { get; init; }          // fondo del chat
        public Color Raised { get; init; }       // cabecera, ajustes y barra de escritura
        public Color Field { get; init; }        // campos, botones, pista del interruptor
        public Color Border { get; init; }
        public Color Line { get; init; }         // separadores suaves
        public Color Text { get; init; }
        public Color Dim { get; init; }
        public Color Placeholder { get; init; }
        public Color Accent { get; init; }
        public Color AccentHover { get; init; }
        public Color OnAccent { get; init; }     // texto sobre el acento
        public Color Emote { get; init; }        // acciones *entre asteriscos*
        public Color Ok { get; init; }
        public Color Warn { get; init; }         // intenso (puntos)
        public Color WarnText { get; init; }     // texto de aviso
        public Color WarnFill { get; init; }
        public Color WarnBorder { get; init; }

        // Etiquetas de canal: iguales en todos los temas para que se reconozcan de un vistazo.
        private static readonly Color PrivateFore = H("#b4d892"), PrivateBack = H("#24331d");
        private static readonly Color PartyFore = H("#a3c4ec"), PartyBack = H("#1c2a3b");
        private static readonly Color ShoutFore = H("#f0d36a"), ShoutBack = H("#3b3110");
        private static readonly Color DmFore = H("#9fd0cf"), DmBack = H("#1f2b2f");
        private static readonly Color WarnTagBack = H("#3a1d16");

        public Color Fore(Tone tone) => tone switch
        {
            Tone.Dim => Dim,
            Tone.Accent => Accent,
            Tone.OnAccent => OnAccent,
            Tone.Emote => Emote,
            Tone.Ok => Ok,
            Tone.Warn => WarnText,
            Tone.Alert => Warn,
            Tone.Placeholder => Placeholder,
            Tone.TagPrivate => PrivateFore,
            Tone.TagParty => PartyFore,
            Tone.TagShout => ShoutFore,
            Tone.TagDm => DmFore,
            Tone.TagOther => Dim,
            Tone.TagWarn => WarnText,
            _ => Text
        };

        // Fondo de las etiquetas de canal (privado, grupo, grito…).
        public Color TagBack(Tone tone) => tone switch
        {
            Tone.TagPrivate => PrivateBack,
            Tone.TagParty => PartyBack,
            Tone.TagShout => ShoutBack,
            Tone.TagDm => DmBack,
            Tone.TagWarn => WarnTagBack,
            _ => Field
        };

        public Color Back(Surface s) => s switch
        {
            Surface.Raised => Raised,
            Surface.Field => Field,
            Surface.Border => Border,
            Surface.Line => Line,
            _ => App
        };

        internal static Color H(string hex) => ColorTranslator.FromHtml(hex);
    }

    internal static class Themes
    {
        public static readonly AppTheme Taberna = new()
        {
            Id = "taberna", Name = "Taberna",
            App = AppTheme.H("#15110d"), Raised = AppTheme.H("#1e1813"), Field = AppTheme.H("#2a221a"),
            Border = AppTheme.H("#3a2f24"), Line = AppTheme.H("#2a221a"),
            Text = AppTheme.H("#ede3cf"), Dim = AppTheme.H("#9a8c76"), Placeholder = AppTheme.H("#857864"),
            Accent = AppTheme.H("#d9a441"), AccentHover = AppTheme.H("#e8b95a"), OnAccent = AppTheme.H("#241a08"),
            Emote = AppTheme.H("#b7a6dc"), Ok = AppTheme.H("#8fb36d"),
            Warn = AppTheme.H("#e2674a"), WarnText = AppTheme.H("#f0937a"),
            WarnFill = AppTheme.H("#2b1a14"), WarnBorder = AppTheme.H("#5a2e22")
        };

        public static readonly AppTheme Pizarra = new()
        {
            Id = "pizarra", Name = "Pizarra",
            App = AppTheme.H("#12161b"), Raised = AppTheme.H("#1a2027"), Field = AppTheme.H("#232b34"),
            Border = AppTheme.H("#2f3944"), Line = AppTheme.H("#232b34"),
            Text = AppTheme.H("#e6e9ee"), Dim = AppTheme.H("#8b96a3"), Placeholder = AppTheme.H("#77818d"),
            Accent = AppTheme.H("#f08a4b"), AccentHover = AppTheme.H("#f6a472"), OnAccent = AppTheme.H("#22110a"),
            Emote = AppTheme.H("#b8a9e3"), Ok = AppTheme.H("#7fc28c"),
            Warn = AppTheme.H("#e2674a"), WarnText = AppTheme.H("#f0937a"),
            WarnFill = AppTheme.H("#2b1c18"), WarnBorder = AppTheme.H("#5a3026")
        };

        public static readonly AppTheme Arcano = new()
        {
            Id = "arcano", Name = "Arcano",
            App = AppTheme.H("#0e1120"), Raised = AppTheme.H("#151a2d"), Field = AppTheme.H("#1e2440"),
            Border = AppTheme.H("#2b3258"), Line = AppTheme.H("#1e2440"),
            Text = AppTheme.H("#e4e6f3"), Dim = AppTheme.H("#8e94b8"), Placeholder = AppTheme.H("#7a80a3"),
            Accent = AppTheme.H("#5ec9b8"), AccentHover = AppTheme.H("#82d8ca"), OnAccent = AppTheme.H("#04201b"),
            Emote = AppTheme.H("#e3a6c9"), Ok = AppTheme.H("#a6d68a"),
            Warn = AppTheme.H("#e86a5a"), WarnText = AppTheme.H("#f29a8c"),
            WarnFill = AppTheme.H("#2a1726"), WarnBorder = AppTheme.H("#5a2b3c")
        };

        public static readonly AppTheme Bosque = new()
        {
            Id = "bosque", Name = "Bosque",
            App = AppTheme.H("#0f1612"), Raised = AppTheme.H("#16201a"), Field = AppTheme.H("#1f2b23"),
            Border = AppTheme.H("#2c3b30"), Line = AppTheme.H("#1f2b23"),
            Text = AppTheme.H("#ebe6d4"), Dim = AppTheme.H("#919c8a"), Placeholder = AppTheme.H("#7e8a78"),
            Accent = AppTheme.H("#e0ad4c"), AccentHover = AppTheme.H("#ebc172"), OnAccent = AppTheme.H("#211806"),
            Emote = AppTheme.H("#c2b2e6"), Ok = AppTheme.H("#a3d07e"),
            Warn = AppTheme.H("#e2674a"), WarnText = AppTheme.H("#f0937a"),
            WarnFill = AppTheme.H("#2b1b14"), WarnBorder = AppTheme.H("#5a2e22")
        };

        public static readonly AppTheme[] All = { Taberna, Pizarra, Arcano, Bosque };

        public static AppTheme Get(string? id) =>
            All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Taberna;

        public static int IndexOf(AppTheme theme) => Array.IndexOf(All, theme);
    }
}
