using System.Text;
using System.Text.RegularExpressions;

namespace TraductorNWN
{
    // Un mensaje que escribe el usuario, separado en:
    //  - Prefix: el comando de chat que va delante y NO se traduce ("/p ", "/t \"Seraphine\" ");
    //  - Body:   el texto que se traduce;
    //  - Translate: si hay que traducir el cuerpo;
    //  - IsChat: si es un mensaje de chat (aparecerá en el log y se puede confirmar; admite mayúsculas).
    internal readonly record struct OutgoingParts(string Prefix, string Body, bool Translate, bool IsChat)
    {
        public string Compose(string body) => Prefix + body;
    }

    internal static class OutgoingText
    {
        // Comandos de canal del chat de NWN: el resto de la línea es un mensaje normal.
        private static readonly HashSet<string> ChannelCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "s", "shout", "p", "party", "w", "whisper", "dm", "tk", "talk"
        };

        // Mensaje privado: /t "Nombre con espacios" mensaje   ó   /t Nombre mensaje
        private static readonly Regex TellRegex = new(
            @"^(?<prefix>/(?:t|tp|tell)\s+(?:""[^""]*""|\S+)\s+)(?<body>.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CommandRegex = new(@"^/(?<cmd>\S+)(?:\s+(?<body>.*))?$", RegexOptions.Compiled);

        public static OutgoingParts Split(string text)
        {
            text = text.Trim();

            // "!comando": órdenes del servidor; van tal cual.
            if (text.StartsWith('!')) return new OutgoingParts("", text, Translate: false, IsChat: false);

            if (text.StartsWith('/'))
            {
                var tell = TellRegex.Match(text);
                if (tell.Success)
                    return new OutgoingParts(tell.Groups["prefix"].Value, tell.Groups["body"].Value.Trim(), Translate: true, IsChat: true);

                var cmd = CommandRegex.Match(text);
                if (cmd.Success && ChannelCommands.Contains(cmd.Groups["cmd"].Value))
                {
                    var body = cmd.Groups["body"].Value.Trim();
                    if (body.Length == 0) return new OutgoingParts("", text, Translate: false, IsChat: false);
                    return new OutgoingParts("/" + cmd.Groups["cmd"].Value + " ", body, Translate: true, IsChat: true);
                }

                // Cualquier otro comando (/roll, /dice, comandos del servidor…) va tal cual.
                return new OutgoingParts("", text, Translate: false, IsChat: false);
            }

            return new OutgoingParts("", text, Translate: true, IsChat: true);
        }

        // NWN muestra el chat con la página de códigos de Windows (cp1252): las comillas tipográficas,
        // guiones largos, emojis, etc. pueden salir como huecos o símbolos raros en el juego.
        // Se cambian por su equivalente simple y se quitan los que no tienen equivalente.
        public static string ForGame(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '‘':
                    case '’':
                    case '‚':
                    case '′':
                        sb.Append('\'');
                        break;
                    case '“':
                    case '”':
                    case '„':
                    case '″':
                        sb.Append('"');
                        break;
                    case '–':
                    case '—':
                    case '−':
                        sb.Append('-');
                        break;
                    case '…':
                        sb.Append("...");
                        break;
                    case ' ':
                    case ' ':
                    case ' ':
                        sb.Append(' ');
                        break;
                    case '\r':
                    case '\n':
                    case '\t':
                        sb.Append(' ');
                        break;
                    default:
                        if (char.IsControl(c)) break;
                        if (c < 0x100 || c == '€') sb.Append(c); // ASCII, Latin-1 (ñ, tildes, ¿ ¡) y €
                        break;
                }
            }
            return sb.ToString().Trim();
        }
    }
}
