using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TraductorNWN
{
    internal enum Glyph { None, D20, Sliders, Send, Caps, Filter, Check }

    // Iconos de línea propios (caja de 24×24, como un SVG con trazo), dibujados con GDI+.
    // Se escalan sin perder nitidez y toman el color del tema, sin depender de librerías externas.
    internal static class Icons
    {
        private static readonly Dictionary<Glyph, string> Paths = new()
        {
            [Glyph.D20] = "M12 2l8.5 5v10L12 22l-8.5-5V7z M12 7.5l5 8H7z " +
                          "M3.5 7L12 7.5L20.5 7 M3.5 7L7 15.5L3.5 17 M7 15.5L12 22L17 15.5L20.5 17 M20.5 7L17 15.5",
            [Glyph.Sliders] = "M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6",
            [Glyph.Send] = "M22 2L11 13M22 2l-7 20-4-9-9-4z",
            [Glyph.Caps] = "M3 5h10M8 5v14M18 5v14M15 8l3-3 3 3M15 16l3 3 3-3",
            [Glyph.Filter] = "M3 4h18l-7 8.5V19l-4 2v-8.5z",
            [Glyph.Check] = "M5 12.5l4.5 4.5L19 7.5"
        };

        private static readonly Dictionary<Glyph, List<(PointF[] Points, bool Closed)>> Parsed = new();
        private static readonly Regex Tokens = new(@"[MmLlHhVvZz]|-?(?:\d+\.?\d*|\.\d+)", RegexOptions.Compiled);

        // stroke: grosor en unidades de la caja de 24 (como stroke-width en SVG).
        public static void Draw(Graphics g, Glyph glyph, RectangleF bounds, Color color, float stroke)
        {
            if (glyph == Glyph.None) return;
            List<(PointF[] Points, bool Closed)> figures;
            lock (Parsed)
            {
                if (!Parsed.TryGetValue(glyph, out figures!))
                {
                    figures = Parse(Paths[glyph]);
                    Parsed[glyph] = figures;
                }
            }

            float scale = Math.Min(bounds.Width, bounds.Height) / 24f;
            float ox = bounds.X + (bounds.Width - 24f * scale) / 2f;
            float oy = bounds.Y + (bounds.Height - 24f * scale) / 2f;

            var oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, Math.Max(1f, stroke * scale))
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            foreach (var (points, closed) in figures)
            {
                var pts = new PointF[points.Length];
                for (int i = 0; i < points.Length; i++)
                    pts[i] = new PointF(ox + points[i].X * scale, oy + points[i].Y * scale);
                if (closed) g.DrawPolygon(pen, pts);
                else g.DrawLines(pen, pts);
            }
            g.SmoothingMode = oldMode;
        }

        // Intérprete mínimo de trazos SVG: M/L/H/V/Z, absolutos y relativos.
        private static List<(PointF[] Points, bool Closed)> Parse(string d)
        {
            var tokens = Tokens.Matches(d);
            var figures = new List<(PointF[] Points, bool Closed)>();
            var current = new List<PointF>();
            float x = 0, y = 0, startX = 0, startY = 0;
            char cmd = 'M';
            int i = 0;

            float Next() => float.Parse(tokens[i++].Value, CultureInfo.InvariantCulture);

            void Flush(bool closed)
            {
                if (current.Count > 1) figures.Add((current.ToArray(), closed));
                current = new List<PointF>();
            }

            while (i < tokens.Count)
            {
                var token = tokens[i].Value;
                if (char.IsLetter(token[0]))
                {
                    cmd = token[0];
                    i++;
                    if (cmd is 'Z' or 'z')
                    {
                        Flush(true);
                        x = startX;
                        y = startY;
                    }
                    continue;
                }

                switch (cmd)
                {
                    case 'M':
                        Flush(false);
                        x = Next(); y = Next();
                        startX = x; startY = y;
                        current.Add(new PointF(x, y));
                        cmd = 'L';
                        break;
                    case 'm':
                        Flush(false);
                        x += Next(); y += Next();
                        startX = x; startY = y;
                        current.Add(new PointF(x, y));
                        cmd = 'l';
                        break;
                    case 'L':
                        x = Next(); y = Next();
                        current.Add(new PointF(x, y));
                        break;
                    case 'l':
                        x += Next(); y += Next();
                        current.Add(new PointF(x, y));
                        break;
                    case 'H':
                        x = Next();
                        current.Add(new PointF(x, y));
                        break;
                    case 'h':
                        x += Next();
                        current.Add(new PointF(x, y));
                        break;
                    case 'V':
                        y = Next();
                        current.Add(new PointF(x, y));
                        break;
                    case 'v':
                        y += Next();
                        current.Add(new PointF(x, y));
                        break;
                    default:
                        i++;
                        break;
                }
            }
            Flush(false);
            return figures;
        }
    }
}
