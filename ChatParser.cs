using System.Text.RegularExpressions;

namespace TraductorNWN
{
    internal enum LineKind { Chat, System }

    // Una línea del log ya interpretada.
    //  Chat:   Time/Speaker/Channel rellenos, Text = mensaje.
    //  System: combate, avisos del servidor, etc. Solo Text (y Time si lo hay).
    internal sealed record ParsedLine(LineKind Kind, string? Time, string? Speaker, string? Channel, string Text);

    internal sealed class ChatParser
    {
        // Formato de NWN:EE:  [CHAT WINDOW TEXT] [Sun Oct 11 14:35:00] Nombre: [Talk] mensaje
        // El segundo corchete (fecha/hora) es opcional; solo se toma como tal si contiene una hora.
        private static readonly Regex PrefixRegex = new(
            @"^\[[^\]]*\]\s*(?:\[(?<ts>[^\]]*\d:\d\d[^\]]*)\]\s*)?(?<rest>.*)$",
            RegexOptions.Compiled);

        private static readonly Regex SpeakerRegex = new(
            @"^(?<speaker>.+?):\s*\[(?<channel>[^\]]+)\]\s?(?<msg>.*)$",
            RegexOptions.Compiled);

        // Etiquetas de color del cliente, tipo <cÌ®þ>texto</c>.
        private static readonly Regex ColorTagRegex = new(@"</?c[^>]*>", RegexOptions.Compiled);

        private static readonly Regex TimeRegex = new(@"(\d{1,2}:\d{2})(?::\d{2})?", RegexOptions.Compiled);

        private ParsedLine? _last;

        public static string StripColorTags(string text) => ColorTagRegex.Replace(text, "").Trim();

        // Devuelve null para líneas vacías o que no interesan (cabeceras del log).
        public ParsedLine? Parse(string rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.Contains("Messages for:")) return null;

            ParsedLine? result;

            if (line[0] != '[')
            {
                // Sin prefijo: es la continuación de un mensaje de varias líneas.
                var text = StripColorTags(line);
                if (text.Length == 0) return null;
                if (_last is { Kind: LineKind.Chat } prev)
                {
                    result = prev with { Text = text };
                }
                else
                {
                    if (text.Contains("----")) return null;
                    result = new ParsedLine(LineKind.System, _last?.Time, null, null, text);
                }
            }
            else
            {
                var prefix = PrefixRegex.Match(line);
                string? time = null;
                string rest = line;
                if (prefix.Success)
                {
                    rest = prefix.Groups["rest"].Value;
                    if (prefix.Groups["ts"].Success)
                    {
                        var tm = TimeRegex.Match(prefix.Groups["ts"].Value);
                        if (tm.Success) time = tm.Groups[1].Value;
                    }
                }

                var sm = SpeakerRegex.Match(rest);
                if (sm.Success)
                {
                    var speaker = StripColorTags(sm.Groups["speaker"].Value);
                    var msg = StripColorTags(sm.Groups["msg"].Value);
                    if (msg.Length == 0) return null;
                    result = new ParsedLine(LineKind.Chat, time, speaker, sm.Groups["channel"].Value.Trim(), msg);
                }
                else
                {
                    var text = StripColorTags(rest);
                    if (text.Length == 0 || text.Contains("----")) return null;
                    result = new ParsedLine(LineKind.System, time, null, null, text);
                }
            }

            _last = result;
            return result;
        }
    }
}
