using System.Runtime.InteropServices;

namespace TraductorNWN
{
    // ---------- Modelo de un mensaje del chat ----------
    // Los mensajes se guardan con "tonos" y "caras" (no con colores ni fuentes concretas),
    // así al cambiar de tema se vuelven a pintar con los colores nuevos.

    internal enum Face
    {
        Small,        // hora, avisos, etiquetas
        Name,         // nombre de quien habla
        Body,         // mensaje (serif)
        BodyItalic,   // acción/emote
        Original,     // texto original, más pequeño
        Spacer        // separación entre mensajes
    }

    internal enum ParaKind
    {
        Header,       // "14:31  Nombre  [grupo]" (las líneas siguientes quedan sangradas)
        Body,         // sangrado bajo el nombre
        Notice,       // centrado: avisos del programa
        Own           // tus mensajes, alineados a la derecha
    }

    internal readonly record struct Run(string Text, Tone Tone, Face Face, bool IsTag = false);

    internal sealed class Para
    {
        public ParaKind Kind { get; init; }
        public List<Run> Runs { get; } = new(4);
    }

    internal sealed class ChatMessage
    {
        public List<Para> Paras { get; } = new(3);
    }

    internal sealed class MessageBuilder
    {
        private readonly ChatMessage _message = new();
        private Para? _para;

        public MessageBuilder Para(ParaKind kind)
        {
            _para = new Para { Kind = kind };
            _message.Paras.Add(_para);
            return this;
        }

        public MessageBuilder Add(string text, Tone tone, Face face)
        {
            if (string.IsNullOrEmpty(text)) return this;
            if (_para == null) Para(ParaKind.Body);
            _para!.Runs.Add(new Run(text, tone, face));
            return this;
        }

        public MessageBuilder Tag(string label, Tone tone)
        {
            if (_para == null) Para(ParaKind.Body);
            _para!.Runs.Add(new Run(" " + label + " ", tone, Face.Small, IsTag: true));
            return this;
        }

        public ChatMessage Build() => _message;
    }

    // ---------- Vista: pinta los mensajes en un RichTextBox ----------
    //  - pinta muchos mensajes de golpe sin parpadeo (WM_SETREDRAW);
    //  - solo baja al final si ya estabas abajo; si estabas leyendo más arriba, no te mueve;
    //  - recorta el historial antiguo de forma exacta;
    //  - guarda los mensajes para repintarlos al cambiar de tema.
    internal sealed class ChatView
    {
        private sealed class Entry
        {
            public required ChatMessage Message { get; init; }
            public int Length { get; set; }
        }

        private readonly RichTextBox _box;
        private readonly int _maxMessages;
        private readonly Queue<Entry> _entries = new();
        private AppTheme _theme;

        public ChatView(RichTextBox box, int maxMessages, AppTheme theme)
        {
            _box = box;
            _maxMessages = maxMessages;
            _theme = theme;
            _box.BackColor = theme.App;
            _box.ForeColor = theme.Text;
            if (_box.IsHandleCreated) UseDarkScrollbar();
            _box.HandleCreated += (s, e) => UseDarkScrollbar();
        }

        private void UseDarkScrollbar()
        {
            try { NativeMethods.SetWindowTheme(_box.Handle, "DarkMode_Explorer", null); }
            catch { /* Windows antiguo: barra clara */ }
        }

        public void SetTheme(AppTheme theme)
        {
            _theme = theme;
            if (_box.IsDisposed) return;
            _box.BackColor = theme.App;
            _box.ForeColor = theme.Text;
            if (!_box.IsHandleCreated || _entries.Count == 0) return;

            IntPtr h = _box.Handle;
            NativeMethods.SendMessage(h, NativeMethods.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                _box.Clear();
                foreach (var entry in _entries)
                {
                    int before = _box.TextLength;
                    Render(entry.Message);
                    entry.Length = _box.TextLength - before;
                }
                _box.Select(_box.TextLength, 0);
            }
            finally
            {
                NativeMethods.SendMessage(h, NativeMethods.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            }
            _box.ScrollToCaret();
            _box.Invalidate();
        }

        public void Append(IReadOnlyList<ChatMessage> messages)
        {
            if (messages.Count == 0 || _box.IsDisposed) return;

            IntPtr h = _box.Handle;
            bool atBottom = IsAtBottom(h);
            int selStart = _box.SelectionStart;
            int selLen = _box.SelectionLength;
            var scroll = new NativeMethods.POINT();
            if (!atBottom) NativeMethods.SendMessage(h, NativeMethods.EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);

            NativeMethods.SendMessage(h, NativeMethods.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                foreach (var message in messages)
                {
                    int before = _box.TextLength;
                    Render(message);
                    _entries.Enqueue(new Entry { Message = message, Length = _box.TextLength - before });
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

        private void Render(ChatMessage message)
        {
            foreach (var para in message.Paras)
            {
                BeginParagraph(para.Kind);
                foreach (var run in para.Runs) Write(run);
                Write(new Run("\n", Tone.Text, Face.Small));
            }
            // Pequeña separación entre mensajes.
            BeginParagraph(ParaKind.Body);
            Write(new Run("\n", Tone.Text, Face.Spacer));
        }

        private void BeginParagraph(ParaKind kind)
        {
            _box.Select(_box.TextLength, 0);
            int gutter = Dpi.S(48);
            switch (kind)
            {
                case ParaKind.Header:
                    _box.SelectionAlignment = HorizontalAlignment.Left;
                    _box.SelectionIndent = 0;
                    _box.SelectionHangingIndent = gutter;
                    _box.SelectionRightIndent = Dpi.S(12);
                    _box.SelectionTabs = new[] { gutter };
                    break;
                case ParaKind.Notice:
                    _box.SelectionAlignment = HorizontalAlignment.Center;
                    _box.SelectionIndent = Dpi.S(40);
                    _box.SelectionHangingIndent = 0;
                    _box.SelectionRightIndent = Dpi.S(40);
                    break;
                case ParaKind.Own:
                    _box.SelectionAlignment = HorizontalAlignment.Right;
                    _box.SelectionIndent = Dpi.S(140);
                    _box.SelectionHangingIndent = 0;
                    _box.SelectionRightIndent = Dpi.S(12);
                    break;
                default:
                    _box.SelectionAlignment = HorizontalAlignment.Left;
                    _box.SelectionIndent = gutter;
                    _box.SelectionHangingIndent = 0;
                    _box.SelectionRightIndent = Dpi.S(12);
                    break;
            }
        }

        private void Write(Run run)
        {
            _box.Select(_box.TextLength, 0);
            _box.SelectionFont = FontFor(run.Face);
            _box.SelectionColor = _theme.Fore(run.Tone);
            _box.SelectionBackColor = run.IsTag ? _theme.TagBack(run.Tone) : _theme.App;
            _box.SelectedText = run.Text;
        }

        private static Font FontFor(Face face) => face switch
        {
            Face.Name => UiFonts.UiStrong(10f),
            Face.Body => UiFonts.Serif(11.25f),
            Face.BodyItalic => UiFonts.Serif(11.25f, FontStyle.Italic),
            Face.Original => UiFonts.Serif(9.5f),
            Face.Spacer => UiFonts.Ui(5f),
            _ => UiFonts.Ui(9f)
        };

        // Si estás leyendo más arriba se deja crecer el historial (hasta el doble) para no moverte el texto.
        private int Trim(bool atBottom)
        {
            int limit = atBottom ? _maxMessages : _maxMessages * 2;
            if (_entries.Count <= limit) return 0;

            int target = _maxMessages - Math.Max(1, _maxMessages / 10);
            int remove = 0;
            while (_entries.Count > target) remove += _entries.Dequeue().Length;

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
            if (!NativeMethods.GetScrollInfo(h, NativeMethods.SB_VERT, ref si)) return true;
            if (si.nPage == 0 || si.nMax - si.nMin + 1 <= (int)si.nPage) return true;
            return si.nPos + (int)si.nPage >= si.nMax - Dpi.S(24);
        }
    }
}
