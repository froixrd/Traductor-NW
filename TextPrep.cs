using System.Text.RegularExpressions;

namespace TraductorNWN
{
    // Abreviaturas típicas del chat en inglés. Los traductores automáticos las dejan tal cual o las
    // traducen mal ("u" → "u", "ty" → "ty"), así que antes de traducir se escriben completas.
    // Solo afecta al texto que se envía al traductor; en pantalla se sigue viendo el original.
    // Términos de juego que se entienden igual en español (afk, lol, gg, ooc, rp, pvp, hp…) no se tocan.
    internal static class ChatSlang
    {
        private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["u"] = "you", ["ur"] = "your", ["r"] = "are", ["ya"] = "you", ["yr"] = "your",
            ["pls"] = "please", ["plz"] = "please", ["plox"] = "please",
            ["thx"] = "thanks", ["thnx"] = "thanks", ["tnx"] = "thanks", ["ty"] = "thank you", ["tyvm"] = "thank you very much",
            ["np"] = "no problem", ["yw"] = "you're welcome",
            ["idk"] = "I don't know", ["dunno"] = "don't know", ["idc"] = "I don't care",
            ["imo"] = "in my opinion", ["imho"] = "in my opinion", ["btw"] = "by the way", ["tbh"] = "to be honest",
            ["omw"] = "on my way", ["brb"] = "be right back", ["bbl"] = "be back later", ["bbiab"] = "be back in a bit",
            ["gtg"] = "got to go", ["g2g"] = "got to go", ["cya"] = "see you", ["cu"] = "see you",
            ["ofc"] = "of course", ["rly"] = "really", ["ppl"] = "people", ["b4"] = "before", ["nvm"] = "never mind",
            ["wb"] = "welcome back", ["gl"] = "good luck", ["hf"] = "have fun", ["gj"] = "good job", ["wp"] = "well played",
            ["k"] = "okay", ["kk"] = "okay", ["msg"] = "message", ["lvl"] = "level", ["sry"] = "sorry", ["srry"] = "sorry",
            ["gonna"] = "going to", ["wanna"] = "want to", ["gotta"] = "got to", ["lemme"] = "let me", ["gimme"] = "give me",
            ["im"] = "I'm", ["ive"] = "I've", ["dont"] = "don't", ["cant"] = "can't", ["didnt"] = "didn't",
            ["doesnt"] = "doesn't", ["isnt"] = "isn't", ["wasnt"] = "wasn't", ["thats"] = "that's", ["whats"] = "what's",
            ["youre"] = "you're", ["theyre"] = "they're", ["wouldnt"] = "wouldn't", ["couldnt"] = "couldn't", ["shouldnt"] = "shouldn't"
        };

        // Palabra completa: ni letras/dígitos/apóstrofo pegados, ni un punto detrás (para no tocar "U.S.").
        private static readonly Regex Word = new(
            @"(?<![\p{L}\p{N}'’])(" + string.Join("|", Map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}'’]|\.\p{L})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string Expand(string text) =>
            Word.Replace(text, m => Map.TryGetValue(m.Value, out var full) ? full : m.Value);
    }

    // Palabras que el usuario quiere que no se traduzcan nunca (nombres, lugares, jerga del servidor).
    // Antes de traducir se cambian por marcadores {0}, {1}… que los traductores respetan, y después
    // se vuelven a poner tal cual.
    internal sealed class ProtectedTerms
    {
        public static readonly ProtectedTerms Empty = new(Array.Empty<string>());

        private static readonly Regex Placeholder = new(@"\{\s*(\d{1,2})\s*\}", RegexOptions.Compiled);

        private readonly Regex? _regex;

        public ProtectedTerms(IEnumerable<string> terms)
        {
            var list = terms.Select(t => t.Trim())
                            .Where(t => t.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(t => t.Length)
                            .ToList();
            Count = list.Count;
            if (list.Count > 0)
            {
                _regex = new Regex(
                    @"(?<![\p{L}\p{N}_])(?:" + string.Join("|", list.Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        public int Count { get; }

        // Separa una lista escrita por el usuario: "Seraphine, Kael; Aguas Profundas".
        public static List<string> ParseList(string text) =>
            text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public (string Text, List<string>? Tokens) Protect(string text)
        {
            if (_regex == null) return (text, null);
            List<string>? tokens = null;
            var result = _regex.Replace(text, m =>
            {
                tokens ??= new List<string>();
                if (tokens.Count >= 99) return m.Value;
                tokens.Add(m.Value);
                return "{" + (tokens.Count - 1) + "}";
            });
            return (result, tokens);
        }

        public static string Restore(string text, List<string>? tokens)
        {
            if (tokens == null) return text;
            return Placeholder.Replace(text, m =>
                int.TryParse(m.Groups[1].Value, out var i) && i < tokens.Count ? tokens[i] : m.Value);
        }
    }
}
