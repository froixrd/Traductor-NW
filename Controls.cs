using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TraductorNWN
{
    // Controles propios, dibujados a mano con los colores del tema activo. Sustituyen a Guna y
    // FontAwesome: el aspecto es exactamente el del diseño y el .exe pesa menos.

    internal static class Gfx
    {
        public const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d < 1f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Prepare(Graphics g, Color background)
        {
            g.Clear(background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }

        public static void FocusRing(Graphics g, RectangleF r, float radius, Color color)
        {
            using var path = Round(RectangleF.Inflate(r, -1.5f, -1.5f), Math.Max(0f, radius - 1.5f));
            using var pen = new Pen(color, Dpi.S(2f));
            g.DrawPath(pen, path);
        }

        public static Size Measure(string text, Font font) =>
            string.IsNullOrEmpty(text) ? Size.Empty : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFlags);

        public static void ApplyTree(Control root, AppTheme theme)
        {
            if (root is IThemed themed) themed.ApplyTheme(theme);
            foreach (Control child in root.Controls) ApplyTree(child, theme);
        }
    }

    // ------------------------------------------------------------------
    // Paneles y etiquetas que toman su color del tema.
    // ------------------------------------------------------------------

    internal class ThemedPanel : Panel, IThemed
    {
        protected AppTheme? Theme;

        public Surface Back { get; set; } = Surface.Raised;
        public Surface? TopBorder { get; set; }
        public Surface? BottomBorder { get; set; }

        public ThemedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }

        public virtual void ApplyTheme(AppTheme theme)
        {
            Theme = theme;
            BackColor = theme.Back(Back);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var t = Theme;
            if (t == null) return;
            if (TopBorder is Surface top)
            {
                using var b = new SolidBrush(t.Back(top));
                e.Graphics.FillRectangle(b, 0, 0, Width, 1);
            }
            if (BottomBorder is Surface bottom)
            {
                using var b = new SolidBrush(t.Back(bottom));
                e.Graphics.FillRectangle(b, 0, Height - 1, Width, 1);
            }
        }
    }

    internal class ThemedLabel : Label, IThemed
    {
        private AppTheme? _theme;
        private Tone _tone = Tone.Text;

        public Surface Back { get; set; } = Surface.Raised;

        public Tone Tone
        {
            get => _tone;
            set
            {
                _tone = value;
                if (_theme != null) ForeColor = _theme.Fore(value);
            }
        }

        public ThemedLabel()
        {
            AutoSize = false;
            UseMnemonic = false;
            TextAlign = ContentAlignment.MiddleLeft;
        }

        public void ApplyTheme(AppTheme theme)
        {
            _theme = theme;
            ForeColor = theme.Fore(_tone);
            BackColor = theme.Back(Back);
        }
    }

    // Icono suelto (el d20 de la cabecera).
    internal sealed class GlyphBox : Control, IThemed
    {
        private AppTheme? _theme;

        public Glyph Glyph { get; set; }
        public Tone Tone { get; set; } = Tone.Accent;
        public Surface Back { get; set; } = Surface.Raised;
        public float Stroke { get; set; } = 1.4f;

        public GlyphBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        public void ApplyTheme(AppTheme theme)
        {
            _theme = theme;
            BackColor = theme.Back(Back);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_theme == null) return;
            Gfx.Prepare(e.Graphics, _theme.Back(Back));
            Icons.Draw(e.Graphics, Glyph, ClientRectangle, _theme.Fore(Tone), Stroke);
        }
    }

    // Panel con esquinas redondeadas, relleno y borde.
    internal class RoundedPanel : Panel, IThemed
    {
        protected AppTheme? Theme;

        public Surface FillRole { get; set; } = Surface.Field;
        public Surface OuterRole { get; set; } = Surface.Raised;
        public Surface? BorderRole { get; set; } = Surface.Border;
        public int Radius { get; set; } = 8;
        public bool Highlight { get; set; }

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public virtual void ApplyTheme(AppTheme theme)
        {
            Theme = theme;
            BackColor = theme.Back(OuterRole);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme;
            if (t == null)
            {
                base.OnPaint(e);
                return;
            }
            var g = e.Graphics;
            Gfx.Prepare(g, t.Back(OuterRole));
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using var path = Gfx.Round(r, Dpi.S((float)Radius));
            using (var fill = new SolidBrush(t.Back(FillRole))) g.FillPath(fill, path);

            Color? border = Highlight ? t.Accent : BorderRole is Surface b ? t.Back(b) : null;
            if (border is Color bc)
            {
                using var pen = new Pen(bc, Highlight ? Dpi.S(1.5f) : 1f);
                g.DrawPath(pen, path);
            }
        }
    }

    // ------------------------------------------------------------------
    // Botón plano con varias variantes.
    // ------------------------------------------------------------------

    internal enum ButtonKind
    {
        Ghost,    // sin fondo (ajustes)
        Field,    // fondo de campo (mayúsculas); Checked = relleno con el acento
        Accent,   // acción principal (enviar)
        Outline,  // borde de acento (comprobar)
        Tab,      // pestaña con subrayado
        Segment,  // opción de un selector segmentado
        Link      // texto pequeño clicable
    }

    internal class FlatButton : Button, IThemed
    {
        private bool _hover, _pressed, _checked;
        protected AppTheme? Theme;

        public ButtonKind Kind { get; set; } = ButtonKind.Field;
        public Glyph Glyph { get; set; } = Glyph.None;
        public float GlyphSize { get; set; } = 20f;
        public float GlyphStroke { get; set; } = 1.9f;
        public int Radius { get; set; } = 8;
        public Surface OuterRole { get; set; } = Surface.Raised;
        public Color? Dot { get; set; }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
            }
        }

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Font = UiFonts.Ui(9.75f);
        }

        public virtual void ApplyTheme(AppTheme theme)
        {
            Theme = theme;
            BackColor = theme.Back(OuterRole);
            Invalidate();
        }

        private bool UsesStrongFont => Kind is ButtonKind.Accent or ButtonKind.Outline ||
                                       (Checked && Kind is ButtonKind.Tab or ButtonKind.Segment);

        private Font PaintFont => UsesStrongFont ? UiFonts.UiStrong(Font.SizeInPoints) : Font;

        // Ancho del contenido (punto + icono + texto), midiendo en seminegrita para que el ancho
        // no cambie al seleccionar la opción.
        public int ContentWidth()
        {
            var text = Gfx.Measure(Text, UiFonts.UiStrong(Font.SizeInPoints)).Width;
            float gap = Dpi.S(8f);
            float w = text;
            if (Glyph != Glyph.None) w += Dpi.S(GlyphSize) + (text > 0 ? gap : 0);
            if (Dot.HasValue) w += Dpi.S(8f) + gap;
            return (int)Math.Ceiling(w);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
            base.OnMouseDown(mevent);
        }
        protected override void OnMouseUp(MouseEventArgs mevent) { _pressed = false; Invalidate(); base.OnMouseUp(mevent); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        private (Color? Fill, Color? Border, Color Fore) Resolve(AppTheme t)
        {
            bool hover = _hover && Enabled;
            if (!Enabled) return (Kind is ButtonKind.Field or ButtonKind.Accent ? t.Field : null, null, t.Dim);

            return Kind switch
            {
                ButtonKind.Ghost => Checked ? (t.Field, t.Accent, t.Accent)
                                  : hover ? (t.Field, null, t.Text)
                                  : (null, null, t.Dim),
                ButtonKind.Field => Checked ? (hover ? t.AccentHover : t.Accent, t.Accent, t.OnAccent)
                                  : hover ? (t.Field, t.Dim, t.Text)
                                  : (t.Field, t.Border, t.Dim),
                ButtonKind.Accent => (hover || _pressed ? t.AccentHover : t.Accent, null, t.OnAccent),
                ButtonKind.Outline => (hover ? t.Field : null, t.Accent, t.Accent),
                ButtonKind.Tab => (null, null, Checked || hover ? t.Text : t.Dim),
                ButtonKind.Segment => Checked ? (t.Accent, null, t.OnAccent)
                                    : (hover ? t.Back(Surface.Border) : null, null, hover ? t.Text : t.Dim),
                _ => (null, null, hover ? t.Text : t.Dim)
            };
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var t = Theme;
            if (t == null)
            {
                base.OnPaint(pevent);
                return;
            }

            var g = pevent.Graphics;
            Gfx.Prepare(g, t.Back(OuterRole));
            var (fill, border, fore) = Resolve(t);
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = Dpi.S((float)Radius);

            if (fill.HasValue || border.HasValue)
            {
                using var path = Gfx.Round(r, radius);
                if (fill is Color f)
                {
                    using var b = new SolidBrush(f);
                    g.FillPath(b, path);
                }
                if (border is Color bc)
                {
                    using var p = new Pen(bc, 1f);
                    g.DrawPath(p, path);
                }
            }

            // Contenido centrado: [punto] [icono] [texto]
            var font = PaintFont;
            var textSize = Gfx.Measure(Text, font);
            float gap = Dpi.S(8f);
            float glyph = Glyph != Glyph.None ? Dpi.S(GlyphSize) : 0f;
            float dot = Dot.HasValue ? Dpi.S(8f) : 0f;
            float total = textSize.Width
                          + (glyph > 0 ? glyph + (textSize.Width > 0 ? gap : 0) : 0)
                          + (dot > 0 ? dot + gap : 0);
            float x = (Width - total) / 2f;
            float cy = Height / 2f;
            if (Kind == ButtonKind.Tab) cy -= Dpi.S(1f);

            if (dot > 0)
            {
                using var b = new SolidBrush(Dot!.Value);
                g.FillEllipse(b, x, cy - dot / 2f, dot, dot);
                x += dot + gap;
            }
            if (glyph > 0)
            {
                Icons.Draw(g, Glyph, new RectangleF(x, cy - glyph / 2f, glyph, glyph), fore, GlyphStroke);
                x += glyph + (textSize.Width > 0 ? gap : 0);
            }
            if (textSize.Width > 0)
            {
                var pt = new Point((int)Math.Round(x), (int)Math.Round(cy - textSize.Height / 2f));
                TextRenderer.DrawText(g, Text, font, pt, fore, Gfx.TextFlags);
            }

            if (Kind == ButtonKind.Tab && Checked)
            {
                using var b = new SolidBrush(t.Accent);
                g.FillRectangle(b, 0, Height - Dpi.S(2), Width, Dpi.S(2));
            }

            if (Focused && ShowFocusCues)
                Gfx.FocusRing(g, r, Kind is ButtonKind.Tab or ButtonKind.Link ? Dpi.S(4f) : radius, t.Accent);
        }
    }

    // Selector de opciones excluyentes (proveedor, tema).
    internal sealed class SegmentedControl : RoundedPanel
    {
        private readonly List<FlatButton> _buttons = new();
        private int _selected = -1;

        public event EventHandler? SelectedIndexChanged;

        public SegmentedControl()
        {
            FillRole = Surface.Field;
            BorderRole = null;
            Radius = 8;
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value == _selected || value < 0 || value >= _buttons.Count) return;
                SetSelectedSilently(value);
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SetSelectedSilently(int index)
        {
            _selected = index;
            for (int i = 0; i < _buttons.Count; i++) _buttons[i].Checked = i == index;
        }

        public FlatButton AddOption(string text, Color? dot = null)
        {
            int index = _buttons.Count;
            var button = new FlatButton
            {
                Kind = ButtonKind.Segment,
                Text = text,
                OuterRole = Surface.Field,
                Radius = 6,
                Dot = dot,
                AccessibleRole = AccessibleRole.RadioButton,
                Font = UiFonts.Ui(9.75f)
            };
            button.Click += (s, e) => SelectedIndex = index;
            _buttons.Add(button);
            Controls.Add(button);
            LayoutButtons();
            return button;
        }

        private void LayoutButtons()
        {
            int pad = Dpi.S(3), gap = Dpi.S(3), h = Dpi.S(32), x = pad;
            foreach (var b in _buttons)
            {
                int w = b.ContentWidth() + Dpi.S(28);
                b.Bounds = new Rectangle(x, pad, w, h);
                x += w + gap;
            }
            Size = new Size(x - gap + pad, h + 2 * pad);
        }
    }

    // Interruptor de encendido/apagado. Hereda de CheckBox: teclado y accesibilidad incluidos.
    internal sealed class ToggleSwitch : CheckBox, IThemed
    {
        private AppTheme? _theme;

        public Surface OuterRole { get; set; } = Surface.Raised;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Text = "";
            Size = new Size(Dpi.S(44), Dpi.S(26));
            Cursor = Cursors.Hand;
        }

        public void ApplyTheme(AppTheme theme)
        {
            _theme = theme;
            BackColor = theme.Back(OuterRole);
            Invalidate();
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var t = _theme;
            if (t == null)
            {
                base.OnPaint(pevent);
                return;
            }
            var g = pevent.Graphics;
            Gfx.Prepare(g, t.Back(OuterRole));

            float w = Dpi.S(40f), h = Dpi.S(22f);
            var track = new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
            using (var path = Gfx.Round(track, h / 2f))
            {
                using (var b = new SolidBrush(Checked ? t.Accent : t.Field)) g.FillPath(b, path);
                if (!Checked)
                {
                    using var p = new Pen(t.Border, 1f);
                    g.DrawPath(p, path);
                }
            }

            float knob = Dpi.S(16f), pad = Dpi.S(3f);
            float kx = Checked ? track.Right - pad - knob : track.X + pad;
            using (var b = new SolidBrush(Checked ? t.OnAccent : t.Dim))
                g.FillEllipse(b, kx, track.Y + (h - knob) / 2f, knob, knob);

            if (Focused && ShowFocusCues)
            {
                using var path = Gfx.Round(RectangleF.Inflate(track, Dpi.S(1.5f), Dpi.S(1.5f)), h / 2f + Dpi.S(1.5f));
                using var p = new Pen(t.Text, Dpi.S(1.5f));
                g.DrawPath(p, path);
            }
        }
    }

    // Deslizador (espera antes de enviar), con teclado: flechas, Inicio y Fin.
    internal sealed class Slider : Control, IThemed
    {
        private AppTheme? _theme;
        private int _value, _min, _max = 10;
        private bool _dragging;

        public event EventHandler? ValueChanged;

        public Surface OuterRole { get; set; } = Surface.Raised;

        public Slider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.Slider;
        }

        public int Minimum
        {
            get => _min;
            set { _min = value; if (_max < _min) _max = _min; Value = _value; Invalidate(); }
        }

        public int Maximum
        {
            get => _max;
            set { _max = Math.Max(value, _min); Value = _value; Invalidate(); }
        }

        public int Value
        {
            get => _value;
            set
            {
                int v = Math.Clamp(value, _min, _max);
                if (v == _value) return;
                _value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void ApplyTheme(AppTheme theme)
        {
            _theme = theme;
            BackColor = theme.Back(OuterRole);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Down:
                    Value--; e.Handled = true; break;
                case Keys.Right:
                case Keys.Up:
                    Value++; e.Handled = true; break;
                case Keys.Home:
                    Value = _min; e.Handled = true; break;
                case Keys.End:
                    Value = _max; e.Handled = true; break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                _dragging = true;
                Capture = true;
                SetFromX(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging) SetFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        private float Pad => Dpi.S(10f);

        private void SetFromX(int x)
        {
            float frac = (x - Pad) / Math.Max(1f, Width - 2 * Pad);
            Value = _min + (int)Math.Round(Math.Clamp(frac, 0f, 1f) * (_max - _min));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = _theme;
            if (t == null) return;
            var g = e.Graphics;
            Gfx.Prepare(g, t.Back(OuterRole));

            float cy = Height / 2f, th = Dpi.S(6f);
            float frac = _max > _min ? (float)(_value - _min) / (_max - _min) : 0f;
            float x0 = Pad, x1 = Width - Pad, tx = x0 + (x1 - x0) * frac;

            using (var path = Gfx.Round(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2))
            using (var b = new SolidBrush(t.Field))
                g.FillPath(b, path);
            if (tx > x0)
            {
                using var path = Gfx.Round(new RectangleF(x0, cy - th / 2, tx - x0, th), th / 2);
                using var b = new SolidBrush(t.Accent);
                g.FillPath(b, path);
            }

            float r = Dpi.S(8f);
            using (var b = new SolidBrush(t.Accent)) g.FillEllipse(b, tx - r, cy - r, 2 * r, 2 * r);
            using (var p = new Pen(t.Back(OuterRole), Dpi.S(2f))) g.DrawEllipse(p, tx - r, cy - r, 2 * r, 2 * r);

            if (Focused && ShowFocusCues)
            {
                float fr = r + Dpi.S(3f);
                using var p = new Pen(t.Text, Dpi.S(1.5f));
                g.DrawEllipse(p, tx - fr, cy - fr, 2 * fr, 2 * fr);
            }
        }
    }

    // Barra fina de progreso (consumo de DeepL).
    internal sealed class ThinProgress : Control, IThemed
    {
        private AppTheme? _theme;
        private double _value;

        public Surface OuterRole { get; set; } = Surface.Raised;

        public double Value
        {
            get => _value;
            set { _value = Math.Clamp(value, 0, 1); Invalidate(); }
        }

        public ThinProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        public void ApplyTheme(AppTheme theme)
        {
            _theme = theme;
            BackColor = theme.Back(OuterRole);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = _theme;
            if (t == null) return;
            var g = e.Graphics;
            Gfx.Prepare(g, t.Back(OuterRole));
            float h = Math.Min(Height, Dpi.S(6f));
            var track = new RectangleF(0, (Height - h) / 2f, Width, h);
            using (var path = Gfx.Round(track, h / 2))
            using (var b = new SolidBrush(t.Field))
                g.FillPath(b, path);
            if (_value > 0)
            {
                var fill = track with { Width = Math.Max(h, (float)(track.Width * _value)) };
                using var path = Gfx.Round(fill, h / 2);
                using var b = new SolidBrush(_value >= 0.9 ? t.Warn : t.Accent);
                g.FillPath(b, path);
            }
        }
    }

    // TextBox sin borde con texto de ayuda en el color del tema.
    internal sealed class HintTextBox : TextBox
    {
        private const int WM_PAINT = 0x000F;
        private const int EM_SETMARGINS = 0x00D3;
        private string _hint = "";

        public Color HintColor { get; set; } = Color.Gray;

        public string Hint
        {
            get => _hint;
            set { _hint = value ?? ""; Invalidate(); }
        }

        public HintTextBox()
        {
            BorderStyle = BorderStyle.None;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Sin márgenes internos: así el texto de ayuda cae exactamente donde se escribe.
            NativeMethods.SendMessage(Handle, EM_SETMARGINS, (IntPtr)3, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT && TextLength == 0 && _hint.Length > 0 && IsHandleCreated)
            {
                using var g = Graphics.FromHwnd(Handle);
                TextRenderer.DrawText(g, _hint, Font, ClientRectangle, HintColor, BackColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (TextLength <= 1) Invalidate();
        }
    }

    // Campo de texto redondeado: borde de acento cuando tiene el foco.
    internal sealed class FieldBox : RoundedPanel
    {
        public HintTextBox Input { get; } = new();
        public int InsetX { get; set; } = 14;

        public FieldBox()
        {
            FillRole = Surface.Field;
            BorderRole = Surface.Border;
            Radius = 10;
            Cursor = Cursors.IBeam;
            Controls.Add(Input);
            Input.Enter += (s, e) => { Highlight = true; Invalidate(); };
            Input.Leave += (s, e) => { Highlight = false; Invalidate(); };
        }

        public override void ApplyTheme(AppTheme theme)
        {
            base.ApplyTheme(theme);
            Input.BackColor = theme.Field;
            Input.ForeColor = theme.Text;
            Input.HintColor = theme.Placeholder;
            Input.Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Input.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int x = Dpi.S(InsetX);
            Input.SetBounds(x, (Height - Input.Height) / 2, Math.Max(10, Width - 2 * x), Input.Height);
        }
    }

    // Indicador de estado de la cabecera: punto + texto (+ segundo texto tras un separador).
    internal sealed class StatusPill : Button, IThemed
    {
        private AppTheme? _theme;
        private bool _hover;
        private Tone _dot = Tone.Ok, _firstTone = Tone.Text, _secondTone = Tone.Dim;
        private string _first = "", _second = "";
        private bool _warn;

        public StatusPill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Font = UiFonts.Ui(9.75f);
            Height = Dpi.S(36);
        }

        public void ApplyTheme(AppTheme theme)
        {
            _theme = theme;
            BackColor = theme.Raised;
            Invalidate();
        }

        // Devuelve true si cambió el ancho (hay que recolocar la cabecera).
        public bool SetState(Tone dot, string first, Tone firstTone, string second, Tone secondTone, bool warn)
        {
            if (dot == _dot && first == _first && second == _second && firstTone == _firstTone &&
                secondTone == _secondTone && warn == _warn)
                return false;

            _dot = dot;
            _first = first;
            _second = second;
            _firstTone = firstTone;
            _secondTone = secondTone;
            _warn = warn;
            Text = second.Length > 0 ? first + ", " + second : first; // nombre accesible

            int width = PreferredWidth();
            bool resized = width != Width;
            Width = width;
            Invalidate();
            return resized;
        }

        public int PreferredWidth()
        {
            int pad = Dpi.S(14), dot = Dpi.S(8), gap = Dpi.S(8);
            int w = pad + dot + gap + Gfx.Measure(_first, Font).Width + pad;
            if (_second.Length > 0) w += gap + 1 + gap + Gfx.Measure(_second, Font).Width;
            return w;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var t = _theme;
            if (t == null)
            {
                base.OnPaint(pevent);
                return;
            }
            var g = pevent.Graphics;
            Gfx.Prepare(g, t.Raised);

            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = r.Height / 2f;
            using (var path = Gfx.Round(r, radius))
            {
                var fill = _warn ? t.WarnFill : _hover ? t.Back(Surface.Border) : t.Field;
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using var p = new Pen(_warn ? t.WarnBorder : t.Border, 1f);
                g.DrawPath(p, path);
            }

            float x = Dpi.S(14f), cy = Height / 2f, dot = Dpi.S(8f), gap = Dpi.S(8f);
            using (var b = new SolidBrush(t.Fore(_dot))) g.FillEllipse(b, x, cy - dot / 2, dot, dot);
            x += dot + gap;

            var s1 = Gfx.Measure(_first, Font);
            TextRenderer.DrawText(g, _first, Font, new Point((int)x, (int)Math.Round(cy - s1.Height / 2f)), t.Fore(_firstTone), Gfx.TextFlags);
            x += s1.Width;

            if (_second.Length > 0)
            {
                x += gap;
                float h = Dpi.S(14f);
                using (var p = new Pen(_warn ? t.WarnBorder : t.Border, 1f)) g.DrawLine(p, x, cy - h / 2, x, cy + h / 2);
                x += 1 + gap;
                var s2 = Gfx.Measure(_second, Font);
                TextRenderer.DrawText(g, _second, Font, new Point((int)x, (int)Math.Round(cy - s2.Height / 2f)), t.Fore(_secondTone), Gfx.TextFlags);
            }

            if (Focused && ShowFocusCues) Gfx.FocusRing(g, r, radius, t.Accent);
        }
    }
}
