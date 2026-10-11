using System.Runtime.InteropServices;

namespace TraductorNWN
{
    // Un fragmento de texto con su color y estilo.
    internal readonly record struct Run(string Text, Color Color, FontStyle Style = FontStyle.Regular);

    // Envuelve un RichTextBox de chat:
    //  - pinta muchos mensajes de golpe sin parpadeo (WM_SETREDRAW) y con una sola actualización;
    //  - reutiliza las fuentes (antes se creaba un Font nuevo por fragmento → fuga de recursos GDI);
    //  - solo baja al final si el usuario ya estaba abajo; si estaba leyendo más arriba, no le mueve;
    //  - recorta el historial antiguo de forma exacta (sabe cuánto ocupa cada mensaje).
    internal sealed class ChatView
    {
        private readonly RichTextBox _box;
        private readonly int _maxMessages;
        private readonly Dictionary<FontStyle, Font> _fonts = new();
        private readonly Queue<int> _lengths = new();

        public ChatView(RichTextBox box, int maxMessages)
        {
            _box = box;
            _maxMessages = maxMessages;
        }

        public void AppendMessages(IReadOnlyList<List<Run>> messages)
        {
            if (messages.Count == 0 || _box.IsDisposed) return;

            IntPtr h = _box.Handle; // fuerza la creación del control aunque su pestaña no esté visible
            bool atBottom = IsAtBottom(h);
            int selStart = _box.SelectionStart;
            int selLen = _box.SelectionLength;
            var scroll = new NativeMethods.POINT();
            if (!atBottom) NativeMethods.SendMessage(h, NativeMethods.EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);

            NativeMethods.SendMessage(h, NativeMethods.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                foreach (var msg in messages)
                {
                    int before = _box.TextLength;
                    foreach (var run in msg) AppendRun(run);
                    AppendRun(new Run("\n", _box.ForeColor));
                    _lengths.Enqueue(_box.TextLength - before);
                }

                int removed = Trim(atBottom);

                if (atBottom)
                {
                    _box.Select(_box.TextLength, 0);
                }
                else
                {
                    selStart = Math.Max(0, selStart - removed);
                    _box.Select(Math.Min(selStart, _box.TextLength), selLen);
                }
            }
            finally
            {
                NativeMethods.SendMessage(h, NativeMethods.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            }

            if (atBottom)
                _box.ScrollToCaret();
            else
                NativeMethods.SendMessage(h, NativeMethods.EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);

            _box.Invalidate();
        }

        private void AppendRun(Run run)
        {
            if (string.IsNullOrEmpty(run.Text)) return;
            _box.Select(_box.TextLength, 0);
            _box.SelectionColor = run.Color;
            _box.SelectionFont = GetFont(run.Style);
            _box.SelectedText = run.Text;
        }

        private Font GetFont(FontStyle style)
        {
            if (!_fonts.TryGetValue(style, out var font))
            {
                font = style == FontStyle.Regular ? _box.Font : new Font(_box.Font, style);
                _fonts[style] = font;
            }
            return font;
        }

        // Si el usuario está leyendo más arriba dejamos crecer el buffer (hasta el doble)
        // para no moverle el texto que está leyendo.
        private int Trim(bool atBottom)
        {
            int limit = atBottom ? _maxMessages : _maxMessages * 2;
            if (_lengths.Count <= limit) return 0;

            // Recortamos un bloque (no mensaje a mensaje) para no hacerlo en cada inserción.
            int target = _maxMessages - Math.Max(1, _maxMessages / 10);
            int remove = 0;
            while (_lengths.Count > target) remove += _lengths.Dequeue();

            remove = Math.Min(remove, _box.TextLength);
            if (remove <= 0) return 0;

            _box.Select(0, remove);
            _box.SelectedText = "";
            return remove;
        }

        private static bool IsAtBottom(IntPtr h)
        {
            var si = new NativeMethods.SCROLLINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.SCROLLINFO>(),
                fMask = NativeMethods.SIF_ALL
            };
            if (!NativeMethods.GetScrollInfo(h, NativeMethods.SB_VERT, ref si)) return true; // sin barra = todo visible
            if (si.nPage == 0 || si.nMax - si.nMin + 1 <= (int)si.nPage) return true;
            return si.nPos + (int)si.nPage >= si.nMax - 24;
        }
    }
}
