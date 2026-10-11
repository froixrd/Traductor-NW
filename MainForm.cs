using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace TraductorNWN
{
    internal enum ChatTarget { Ambos, Ingles, Espanol }

    public sealed class MainForm : Form
    {
        private const int MaxMensajesPorPanel = 200;

        private static readonly Regex EmoteRegex = new(@"\*([^*]+)\*", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
        private static readonly NumberFormatInfo EsNumbers = new() { NumberGroupSeparator = ".", NumberDecimalSeparator = "," };
        private static readonly string[] TabNames = { "Ambos", "Inglés", "Español" };

        // ---------- Servicios ----------
        private readonly AppSettings _settings = AppSettings.Load();
        private readonly TranslationService _translator = new();
        private readonly ChatParser _parser = new();
        private readonly LogWatcher _watcher;
        private readonly IncomingPipeline _incoming;
        private readonly CancellationTokenSource _cts = new();

        // Mensajes que escribe el usuario (se traducen y envían de uno en uno, en orden).
        private readonly Channel<OutgoingItem> _outgoing = Channel.CreateUnbounded<OutgoingItem>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // Mensajes escritos que aún no han salido (se pueden cancelar con Esc mientras esperan).
        private readonly List<OutgoingItem> _pendingOut = new();

        // Lo que hay que pintar: los hilos de fondo lo dejan aquí y un timer de la UI lo vuelca por lotes.
        private readonly ConcurrentQueue<(ChatTarget Target, ChatMessage Message)> _display = new();

        // Mensajes enviados hace poco: su eco en el log no se repite en la pestaña Ambos.
        private readonly List<(string Text, long At)> _recentSent = new();

        // Mensajes enviados que esperan aparecer en el log del juego (confirmación de que salieron).
        private readonly List<(string Key, TaskCompletionSource<bool> Seen)> _awaitingEcho = new();

        private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 60 };
        private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 700 };

        private AppTheme _theme;
        private volatile bool _mayusculas;
        private volatile bool _filtrarSistema;
        private volatile bool _logFound;
        private IntPtr _ownHandle;
        private string? _lastRawLine;
        private long _lastRawAt;
        private int _statusTicks;
        private bool _suppressEvents;

        // ---------- Controles ----------
        private ThemedPanel _header = null!;
        private GlyphBox _logo = null!;
        private ThemedLabel _title = null!;
        private ThemedLabel _subtitle = null!;
        private StatusPill _pill = null!;
        private FlatButton _settingsBtn = null!;

        private ThemedPanel _settingsPanel = null!;
        private TableLayoutPanel _settingsTable = null!;
        private SegmentedControl _providerSeg = null!;
        private SegmentedControl _themeSeg = null!;
        private FieldBox _keyBox = null!;
        private FlatButton _checkBtn = null!;
        private ThemedLabel _keyStatus = null!;
        private ThinProgress _usageBar = null!;
        private ThemedLabel _usageLabel = null!;
        private Slider _delaySlider = null!;
        private ThemedLabel _delayValue = null!;
        private ToggleSwitch _filterSwitch = null!;
        private ToggleSwitch _topSwitch = null!;
        private ToggleSwitch _returnSwitch = null!;
        private Control[] _deeplRowControls = Array.Empty<Control>();

        private ThemedPanel _tabRow = null!;
        private FlatButton[] _tabButtons = Array.Empty<FlatButton>();
        private FlatButton _filterLink = null!;

        private ThemedPanel _chatHost = null!;
        private RichTextBox[] _boxes = Array.Empty<RichTextBox>();
        private ChatView[] _views = Array.Empty<ChatView>();

        private ThemedPanel _footer = null!;
        private FieldBox _input = null!;
        private FlatButton _capsBtn = null!;
        private FlatButton _sendBtn = null!;
        private ThemedLabel _hint = null!;

        public MainForm()
        {
            _theme = Themes.Get(_settings.Tema);

            Text = "Traductor NWN";
            AutoScaleMode = AutoScaleMode.None;
            Font = UiFonts.Ui(9.75f);
            Size = new Size(Dpi.S(720), Dpi.S(780));
            MinimumSize = new Size(Dpi.S(560), Dpi.S(560));
            BackColor = _theme.App;

            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { /* si falla, se usa el icono genérico */ }

            BuildHeader();
            BuildSettings();
            BuildTabs();
            BuildChat();
            BuildFooter();

            // Dock: el último en añadirse queda más arriba.
            Controls.Add(_chatHost);
            Controls.Add(_footer);
            Controls.Add(_tabRow);
            Controls.Add(_settingsPanel);
            Controls.Add(_header);

            _watcher = new LogWatcher(OnLogLines, (msg, found) =>
            {
                if (found) _logFound = true;
                Notify(msg, found ? Tone.Accent : Tone.Dim);
            });
            _incoming = new IncomingPipeline(_translator, ShowTranslated, ShowUntranslated,
                (msg, warn) => Notify(msg, warn ? Tone.Warn : Tone.Ok));
            _translator.Notice += msg => Notify(msg, Tone.Warn);

            LoadSettingsIntoUi();
            ApplyTheme(_theme);
            RestoreWindowBounds();

            _uiTimer.Tick += UiTimer_Tick;
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                _settings.Save();
            };

            Load += MainForm_Load;
            Shown += (s, e) => _input.Input.Focus();
            FormClosing += MainForm_FormClosing;
        }

        // ======================================================================
        //  Construcción de la interfaz
        // ======================================================================

        private void BuildHeader()
        {
            _header = new ThemedPanel { Dock = DockStyle.Top, Height = Dpi.S(68), Back = Surface.Raised, BottomBorder = Surface.Border };
            _logo = new GlyphBox { Glyph = Glyph.D20, Tone = Tone.Accent, Stroke = 1.4f, Size = new Size(Dpi.S(30), Dpi.S(30)) };
            _title = new ThemedLabel { Text = "Traductor de chat", Font = UiFonts.Heading(15.75f), Tone = Tone.Text, AutoEllipsis = true };
            _subtitle = new ThemedLabel { Text = "Neverwinter Nights, del inglés al español", Font = UiFonts.Ui(9.5f), Tone = Tone.Dim, AutoEllipsis = true };

            _pill = new StatusPill { AccessibleDescription = "Estado del traductor. Pulsa para abrir los ajustes." };
            _pill.Click += (s, e) => ToggleSettings();

            _settingsBtn = new FlatButton
            {
                Kind = ButtonKind.Ghost,
                Glyph = Glyph.Sliders,
                GlyphStroke = 1.8f,
                Size = new Size(Dpi.S(44), Dpi.S(44)),
                AccessibleName = "Ajustes"
            };
            _settingsBtn.Click += (s, e) => ToggleSettings();

            _header.Controls.AddRange(new Control[] { _logo, _title, _subtitle, _pill, _settingsBtn });
            _header.Resize += (s, e) => LayoutHeader();
        }

        private void LayoutHeader()
        {
            if (_header == null) return;
            int w = _header.ClientSize.Width, h = _header.ClientSize.Height - 1;
            _logo.Location = new Point(Dpi.S(20), (h - _logo.Height) / 2);
            _settingsBtn.Location = new Point(w - Dpi.S(14) - _settingsBtn.Width, (h - _settingsBtn.Height) / 2);
            _pill.Location = new Point(_settingsBtn.Left - Dpi.S(8) - _pill.Width, (h - _pill.Height) / 2);

            int x = _logo.Right + Dpi.S(14);
            int width = Math.Max(0, _pill.Left - Dpi.S(14) - x);
            int th = _title.PreferredHeight, sh = _subtitle.PreferredHeight;
            int top = (h - th - sh) / 2;
            _title.SetBounds(x, top, width, th);
            _subtitle.SetBounds(x, top + th, width, sh);
        }

        private void BuildSettings()
        {
            _settingsPanel = new ThemedPanel
            {
                Dock = DockStyle.Top,
                Back = Surface.Raised,
                BottomBorder = Surface.Border,
                Visible = false,
                Padding = new Padding(Dpi.S(20), Dpi.S(18), Dpi.S(20), Dpi.S(19))
            };

            _settingsTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(170)));
            _settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _settingsTable.SizeChanged += (s, e) => UpdateSettingsHeight();

            // --- Traductor ---
            _providerSeg = new SegmentedControl { Anchor = AnchorStyles.Left | AnchorStyles.Top };
            _providerSeg.AddOption("Google Translate");
            _providerSeg.AddOption("DeepL");
            _providerSeg.SelectedIndexChanged += ProviderSeg_SelectedIndexChanged;
            AddSettingsRow("Traductor", _providerSeg, _providerSeg.Height);

            // --- Clave de DeepL ---
            var keyCell = NewCell(Dpi.S(66));
            _keyBox = new FieldBox { Radius = 8, InsetX = 12 };
            _keyBox.Input.UseSystemPasswordChar = true;
            _keyBox.Input.Font = UiFonts.Ui(10.5f);
            _keyBox.Input.Hint = "Pega aquí tu clave de DeepL";
            _keyBox.Input.AccessibleName = "Clave de DeepL";
            _keyBox.Input.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                if (_checkBtn.Enabled) CheckKey_Click(_checkBtn, EventArgs.Empty);
            };
            _checkBtn = new FlatButton { Kind = ButtonKind.Outline, Text = "Comprobar" };
            _checkBtn.Click += CheckKey_Click;
            _keyStatus = new ThemedLabel { Font = UiFonts.Ui(9f), Tone = Tone.Dim, AutoEllipsis = true };
            keyCell.Controls.AddRange(new Control[] { _keyBox, _checkBtn, _keyStatus });
            keyCell.Resize += (s, e) =>
            {
                int w = keyCell.ClientSize.Width;
                int bw = Gfx.Measure("Comprobando…", UiFonts.UiStrong(9.75f)).Width + Dpi.S(32);
                _checkBtn.SetBounds(w - bw, 0, bw, Dpi.S(40));
                _keyBox.SetBounds(0, 0, Math.Max(40, w - bw - Dpi.S(10)), Dpi.S(40));
                _keyStatus.SetBounds(0, Dpi.S(46), w, Dpi.S(20));
            };
            var keyLabel = AddSettingsRow("Clave de DeepL", keyCell, Dpi.S(40));

            // --- Uso este mes ---
            var usageCell = NewCell(Dpi.S(38));
            _usageBar = new ThinProgress { AccessibleName = "Caracteres de DeepL usados este mes" };
            _usageLabel = new ThemedLabel { Font = UiFonts.Ui(9f), Tone = Tone.Dim, AutoEllipsis = true };
            usageCell.Controls.AddRange(new Control[] { _usageBar, _usageLabel });
            usageCell.Resize += (s, e) =>
            {
                int w = usageCell.ClientSize.Width;
                _usageBar.SetBounds(0, Dpi.S(5), w, Dpi.S(8));
                _usageLabel.SetBounds(0, Dpi.S(17), w, Dpi.S(20));
            };
            var usageLabel = AddSettingsRow("Uso este mes", usageCell, Dpi.S(18));
            _deeplRowControls = new Control[] { keyLabel, keyCell, usageLabel, usageCell };

            // --- Espera antes de enviar ---
            var delayCell = NewCell(Dpi.S(28));
            _delaySlider = new Slider { Minimum = 0, Maximum = 10, AccessibleName = "Espera antes de enviar al juego" };
            _delaySlider.ValueChanged += DelaySlider_ValueChanged;
            _delayValue = new ThemedLabel { Font = UiFonts.Ui(9.75f), Tone = Tone.Text, TextAlign = ContentAlignment.MiddleRight };
            delayCell.Controls.AddRange(new Control[] { _delaySlider, _delayValue });
            delayCell.Resize += (s, e) =>
            {
                int w = delayCell.ClientSize.Width;
                _delayValue.SetBounds(w - Dpi.S(52), 0, Dpi.S(52), Dpi.S(28));
                _delaySlider.SetBounds(0, 0, Math.Max(40, w - Dpi.S(60)), Dpi.S(28));
            };
            AddSettingsRow("Esperar antes de enviar", delayCell, Dpi.S(28));

            // --- Qué se traduce ---
            var filterCell = NewCell(Dpi.S(62));
            _filterSwitch = new ToggleSwitch { AccessibleName = "Traducir solo el chat de jugadores" };
            _filterSwitch.CheckedChanged += FilterSwitch_CheckedChanged;
            var filterTitle = new ThemedLabel { Text = "Solo el chat de jugadores", Font = UiFonts.Ui(10f), Tone = Tone.Text };
            var filterDesc = new ThemedLabel
            {
                Text = "El combate y los avisos del sistema aparecen sin traducir en la pestaña Inglés. Así no se gastan peticiones en las peleas.",
                Font = UiFonts.Ui(9f),
                Tone = Tone.Dim,
                TextAlign = ContentAlignment.TopLeft
            };
            filterCell.Controls.AddRange(new Control[] { _filterSwitch, filterTitle, filterDesc });
            filterCell.Resize += (s, e) =>
            {
                int w = filterCell.ClientSize.Width, x = Dpi.S(56);
                _filterSwitch.Location = Point.Empty;
                filterTitle.SetBounds(x, 0, Math.Max(10, w - x), Dpi.S(26));
                filterDesc.SetBounds(x, Dpi.S(26), Math.Max(10, w - x), Dpi.S(36));
            };
            AddSettingsRow("Qué se traduce", filterCell, Dpi.S(26));

            // --- Colores ---
            _themeSeg = new SegmentedControl { Anchor = AnchorStyles.Left | AnchorStyles.Top };
            foreach (var theme in Themes.All) _themeSeg.AddOption(theme.Name, theme.Accent);
            _themeSeg.SelectedIndexChanged += ThemeSeg_SelectedIndexChanged;
            AddSettingsRow("Colores", _themeSeg, _themeSeg.Height);

            // --- Después de enviar ---
            var returnCell = NewCell(Dpi.S(26));
            _returnSwitch = new ToggleSwitch { AccessibleName = "Volver al traductor después de enviar" };
            _returnSwitch.CheckedChanged += ReturnSwitch_CheckedChanged;
            var returnLabel = new ThemedLabel { Text = "Volver al traductor para seguir escribiendo", Font = UiFonts.Ui(10f), Tone = Tone.Text };
            returnCell.Controls.AddRange(new Control[] { _returnSwitch, returnLabel });
            returnCell.Resize += (s, e) =>
            {
                _returnSwitch.Location = Point.Empty;
                returnLabel.SetBounds(Dpi.S(56), 0, Math.Max(10, returnCell.ClientSize.Width - Dpi.S(56)), Dpi.S(26));
            };
            AddSettingsRow("Después de enviar", returnCell, Dpi.S(26));

            // --- Ventana ---
            var windowCell = NewCell(Dpi.S(26));
            _topSwitch = new ToggleSwitch { AccessibleName = "Mantener la ventana siempre encima del juego" };
            _topSwitch.CheckedChanged += TopSwitch_CheckedChanged;
            var topLabel = new ThemedLabel { Text = "Siempre encima del juego", Font = UiFonts.Ui(10f), Tone = Tone.Text };
            windowCell.Controls.AddRange(new Control[] { _topSwitch, topLabel });
            windowCell.Resize += (s, e) =>
            {
                _topSwitch.Location = Point.Empty;
                topLabel.SetBounds(Dpi.S(56), 0, Math.Max(10, windowCell.ClientSize.Width - Dpi.S(56)), Dpi.S(26));
            };
            var lastLabel = AddSettingsRow("Ventana", windowCell, Dpi.S(26));
            lastLabel.Margin = new Padding(0, 0, Dpi.S(12), 0);
            windowCell.Margin = Padding.Empty;

            _settingsPanel.Controls.Add(_settingsTable);
        }

        private static ThemedPanel NewCell(int height) => new()
        {
            Back = Surface.Raised,
            Height = height,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
        };

        private ThemedLabel AddSettingsRow(string text, Control value, int labelHeight)
        {
            int gap = Dpi.S(16);
            var label = new ThemedLabel
            {
                Text = text,
                Font = UiFonts.Ui(9.75f),
                Tone = Tone.Dim,
                Size = new Size(Dpi.S(158), labelHeight),
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 0, Dpi.S(12), gap)
            };
            value.Margin = new Padding(0, 0, 0, gap);
            int row = _settingsTable.RowCount++;
            _settingsTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _settingsTable.Controls.Add(label, 0, row);
            _settingsTable.Controls.Add(value, 1, row);
            return label;
        }

        private void UpdateSettingsHeight()
        {
            int h = _settingsPanel.Padding.Vertical + _settingsTable.Height + 1;
            if (_settingsPanel.Height != h) _settingsPanel.Height = h;
        }

        private void BuildTabs()
        {
            _tabRow = new ThemedPanel { Dock = DockStyle.Top, Height = Dpi.S(46), Back = Surface.App, BottomBorder = Surface.Line };
            _tabButtons = new FlatButton[TabNames.Length];
            for (int i = 0; i < TabNames.Length; i++)
            {
                int index = i;
                var tab = new FlatButton
                {
                    Kind = ButtonKind.Tab,
                    Text = TabNames[i],
                    OuterRole = Surface.App,
                    Font = UiFonts.Ui(10.5f),
                    AccessibleRole = AccessibleRole.PageTab
                };
                tab.Click += (s, e) => SelectTab(index);
                _tabButtons[i] = tab;
                _tabRow.Controls.Add(tab);
            }
            _tabButtons[0].Checked = true;

            _filterLink = new FlatButton
            {
                Kind = ButtonKind.Link,
                OuterRole = Surface.App,
                Glyph = Glyph.Filter,
                GlyphSize = 13f,
                GlyphStroke = 2f,
                Text = "Solo chat de jugadores",
                Font = UiFonts.Ui(9.5f),
                AccessibleDescription = "Abre los ajustes"
            };
            _filterLink.Click += (s, e) => ToggleSettings(true);
            _tabRow.Controls.Add(_filterLink);
            _tabRow.Resize += (s, e) => LayoutTabs();
        }

        private void LayoutTabs()
        {
            if (_tabRow == null) return;
            int x = Dpi.S(20), h = _tabRow.ClientSize.Height - 1;
            foreach (var tab in _tabButtons)
            {
                int w = tab.ContentWidth() + Dpi.S(8);
                tab.SetBounds(x, 0, w, h);
                x += w + Dpi.S(16);
            }
            int lw = _filterLink.ContentWidth() + Dpi.S(16);
            _filterLink.SetBounds(_tabRow.ClientSize.Width - Dpi.S(12) - lw, Dpi.S(7), lw, h - Dpi.S(14));
        }

        private void BuildChat()
        {
            _chatHost = new ThemedPanel
            {
                Dock = DockStyle.Fill,
                Back = Surface.App,
                Padding = new Padding(Dpi.S(22), Dpi.S(12), Dpi.S(4), Dpi.S(6))
            };
            _boxes = new RichTextBox[TabNames.Length];
            _views = new ChatView[TabNames.Length];
            for (int i = 0; i < TabNames.Length; i++)
            {
                var box = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    DetectUrls = false,
                    HideSelection = false,
                    ScrollBars = RichTextBoxScrollBars.Vertical,
                    TabStop = false,
                    Font = UiFonts.Serif(11.25f),
                    AccessibleName = "Chat: " + TabNames[i]
                };
                _boxes[i] = box;
                _views[i] = new ChatView(box, MaxMensajesPorPanel, _theme);
                _chatHost.Controls.Add(box);
            }
            _boxes[0].BringToFront();
        }

        private void BuildFooter()
        {
            _footer = new ThemedPanel { Dock = DockStyle.Bottom, Height = Dpi.S(98), Back = Surface.Raised, TopBorder = Surface.Border };

            _input = new FieldBox { Radius = 10, InsetX = 14 };
            _input.Input.Font = UiFonts.Serif(11.25f);
            _input.Input.Hint = "Escribe en español y pulsa Enter";
            _input.Input.AccessibleName = "Mensaje en español";
            _input.Input.KeyDown += Input_KeyDown;

            _capsBtn = new FlatButton
            {
                Kind = ButtonKind.Field,
                Glyph = Glyph.Caps,
                Size = new Size(Dpi.S(44), Dpi.S(44)),
                AccessibleName = "Enviar en mayúsculas"
            };
            _capsBtn.Click += CapsBtn_Click;

            _sendBtn = new FlatButton
            {
                Kind = ButtonKind.Accent,
                Glyph = Glyph.Send,
                GlyphSize = 19f,
                GlyphStroke = 2f,
                Size = new Size(Dpi.S(44), Dpi.S(44)),
                AccessibleName = "Enviar al juego"
            };
            _sendBtn.Click += (s, e) =>
            {
                SendCurrent();
                _input.Input.Focus();
            };

            _hint = new ThemedLabel { Font = UiFonts.Ui(9f), Tone = Tone.Dim, AutoEllipsis = true };

            _footer.Controls.AddRange(new Control[] { _input, _capsBtn, _sendBtn, _hint });
            _footer.Resize += (s, e) =>
            {
                int w = _footer.ClientSize.Width, pad = Dpi.S(20), top = Dpi.S(14), size = Dpi.S(44), gap = Dpi.S(10);
                _sendBtn.Location = new Point(w - pad - size, top);
                _capsBtn.Location = new Point(_sendBtn.Left - gap - size, top);
                _input.SetBounds(pad, top, Math.Max(40, _capsBtn.Left - gap - pad), size);
                _hint.SetBounds(pad, top + size + Dpi.S(8), Math.Max(10, w - 2 * pad), Dpi.S(20));
            };
        }

        // ======================================================================
        //  Tema, ajustes y estado
        // ======================================================================

        private void LoadSettingsIntoUi()
        {
            _suppressEvents = true;
            try
            {
                bool deepl = string.Equals(_settings.Proveedor, "DeepL", StringComparison.OrdinalIgnoreCase);
                _providerSeg.SetSelectedSilently(deepl ? 1 : 0);
                _keyBox.Input.Text = _settings.DeepLKey ?? "";
                _translator.DeepLKey = deepl ? _settings.DeepLKey : null;
                SetDeepLRowsVisible(deepl);
                SetKeyStatus(string.IsNullOrEmpty(_settings.DeepLKey)
                    ? "Pega tu clave y pulsa Comprobar. El plan gratuito incluye 500.000 caracteres al mes."
                    : "Clave guardada.", Tone.Dim);
                _usageLabel.Text = "Pulsa Comprobar para ver el consumo de este mes.";

                _delaySlider.Value = (int)Math.Round(_settings.RetardoEnvioMs / 500.0);
                _delayValue.Text = FormatSeconds(_delaySlider.Value * 500);

                _filtrarSistema = _settings.FiltrarSistema;
                _filterSwitch.Checked = _filtrarSistema;
                _filterLink.Visible = _filtrarSistema;

                _themeSeg.SetSelectedSilently(Math.Max(0, Themes.IndexOf(_theme)));

                _topSwitch.Checked = _settings.SiempreEncima;
                TopMost = _settings.SiempreEncima;
                _returnSwitch.Checked = _settings.VolverAlTraductor;

                _mayusculas = _settings.Mayusculas;
                _capsBtn.Checked = _mayusculas;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void ApplyTheme(AppTheme theme)
        {
            _theme = theme;
            BackColor = theme.App;
            Gfx.ApplyTree(this, theme);
            _settingsTable.BackColor = theme.Raised;
            foreach (var view in _views) view.SetTheme(theme);
            ApplyTitleBar();
            UpdateStatus();
            UpdateHint();
            LayoutHeader();
            LayoutTabs();
        }

        // Barra de título del color del tema (Windows 11) u oscura (Windows 10).
        private void ApplyTitleBar()
        {
            if (!IsHandleCreated) return;
            try
            {
                int on = 1;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                int caption = ToColorRef(_theme.Raised);
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                int text = ToColorRef(_theme.Dim);
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_TEXT_COLOR, ref text, sizeof(int));
                int border = ToColorRef(_theme.Border);
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch { /* Windows antiguo: barra estándar */ }
        }

        private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _ownHandle = Handle;
            ApplyTitleBar();
        }

        private void ToggleSettings(bool? open = null)
        {
            bool show = open ?? !_settingsPanel.Visible;
            if (show == _settingsPanel.Visible) return;
            _settingsPanel.Visible = show;
            _settingsBtn.Checked = show;
            _settingsBtn.AccessibleName = show ? "Cerrar ajustes" : "Ajustes";
            if (show)
            {
                _settingsTable.PerformLayout();
                UpdateSettingsHeight();
            }
            else
            {
                _input.Input.Focus();
            }
        }

        private void SetDeepLRowsVisible(bool visible)
        {
            foreach (var c in _deeplRowControls) c.Visible = visible;
            _settingsTable.PerformLayout();
            UpdateSettingsHeight();
        }

        private void SelectTab(int index)
        {
            for (int i = 0; i < _tabButtons.Length; i++) _tabButtons[i].Checked = i == index;
            _boxes[index].BringToFront();
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void UpdateStatus()
        {
            if (_pill == null || _incoming == null) return;
            bool deepl = _translator.ActiveProvider == Proveedor.DeepL;
            var cooldown = _translator.CooldownRemaining;
            int pending = _incoming.Pending;

            bool resized;
            if (cooldown.TotalSeconds >= 1)
            {
                resized = _pill.SetState(Tone.Alert, $"Pausa {Math.Ceiling(cooldown.TotalSeconds)} s", Tone.Warn,
                    pending > 0 ? $"{pending} en cola" : "", Tone.Dim, warn: true);
            }
            else if (!_logFound)
            {
                resized = _pill.SetState(Tone.Dim, "Esperando al juego", Tone.Dim, deepl ? "DeepL" : "Google", Tone.Dim, warn: false);
            }
            else if (pending >= 3)
            {
                resized = _pill.SetState(Tone.Accent, deepl ? "DeepL" : "Google", Tone.Text, $"{pending} en cola", Tone.Dim, warn: false);
            }
            else
            {
                resized = _pill.SetState(Tone.Ok, deepl ? "DeepL" : "Google Translate", Tone.Text, "", Tone.Dim, warn: false);
            }
            if (resized) LayoutHeader();
        }

        private void UpdateHint()
        {
            if (_hint == null) return;
            OutgoingItem? last = null;
            int pending;
            lock (_pendingOut)
            {
                pending = _pendingOut.Count;
                if (pending > 0) last = _pendingOut[^1];
            }

            string text;
            Tone tone;
            if (last != null)
            {
                bool more = pending > 1;
                if (_translator.CooldownRemaining.TotalSeconds >= 1)
                {
                    var provider = _translator.ActiveProvider == Proveedor.DeepL ? "DeepL" : "Google";
                    text = $"«{Shorten(last.Text)}» está en cola: sale en cuanto {provider} vuelva a responder. Esc para cancelarlo.";
                    tone = Tone.Warn;
                }
                else if (last.Committed)
                {
                    text = more ? $"Escribiendo en el juego… ({pending} mensajes)" : "Escribiendo en el juego…";
                    tone = Tone.Accent;
                }
                else
                {
                    text = more
                        ? $"«{Shorten(last.Text)}» y {pending - 1} más a punto de salir. Esc para cancelar el último."
                        : $"«{Shorten(last.Text)}» a punto de salir. Esc para cancelarlo y corregirlo.";
                    tone = Tone.Accent;
                }
            }
            else
            {
                text = _settings.RetardoEnvioMs == 0
                    ? "Enter traduce y envía al juego. Ctrl+Enter lo envía sin traducir."
                    : $"Enter traduce y envía al juego en {FormatSeconds(_settings.RetardoEnvioMs)}. Ctrl+Enter lo envía sin traducir.";
                tone = Tone.Dim;
            }

            if (_hint.Text != text) _hint.Text = text;
            if (_hint.Tone != tone) _hint.Tone = tone;
        }

        private static string Shorten(string s) => s.Length <= 40 ? s : s.Substring(0, 39) + "…";

        private static string FormatSeconds(int ms) => (ms / 1000.0).ToString("0.#", EsNumbers) + " s";

        private static string FormatCount(long n) => n.ToString("#,0", EsNumbers);

        private void RestoreWindowBounds()
        {
            var v = _settings.Ventana;
            if (v is not { Length: 4 }) return;
            var rect = new Rectangle(v[0], v[1], Math.Max(v[2], MinimumSize.Width), Math.Max(v[3], MinimumSize.Height));
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = rect;
        }

        private void SaveWindowBounds()
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.Ventana = new[] { r.X, r.Y, r.Width, r.Height };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.D1:
                    SelectTab(0);
                    return true;
                case Keys.Control | Keys.D2:
                    SelectTab(1);
                    return true;
                case Keys.Control | Keys.D3:
                    SelectTab(2);
                    return true;
                case Keys.Escape when _settingsPanel.Visible:
                    ToggleSettings(false);
                    return true;
                case Keys.Escape when CancelLastPending():
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ======================================================================
        //  Ciclo de vida
        // ======================================================================

        private void MainForm_Load(object? sender, EventArgs e)
        {
            _uiTimer.Start();
            LayoutHeader();
            LayoutTabs();

            var ct = _cts.Token;
            RunBackground(() => _watcher.RunAsync(ct), "lector del log");
            RunBackground(() => _incoming.RunAsync(ct), "traductor");
            RunBackground(() => OutgoingWorkerAsync(ct), "envío al juego");

            if (_translator.DeepLKey is string key) _ = RefreshUsageAsync(key);
            CheckGameChatLogging();
        }

        // NWN:EE no guarda el chat en el log salvo que se active en settings.tml: sin eso no llega nada que traducir.
        private void CheckGameChatLogging()
        {
            var (state, path) = GameSettings.CheckChatLogging();
            if (state != ChatLogging.Disabled || path == null) return;
            Notify("El juego no está guardando el chat en su log, así que no llegará nada que traducir. " +
                   $"Cierra el juego, abre {path} y en la sección {GameSettings.SectionAll} pon «enabled = true». " +
                   $"Deja «enabled = false» en {GameSettings.SectionText} para que no salga cada mensaje dos veces.", Tone.Warn);
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cts.Cancel();
            _uiTimer.Stop();
            _saveTimer.Stop();
            _incoming.Complete();
            _outgoing.Writer.TryComplete();
            SaveWindowBounds();
            _settings.Save();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _uiTimer.Dispose();
                _saveTimer.Dispose();
                _translator.Dispose();
            }
            base.Dispose(disposing);
        }

        private void RunBackground(Func<Task> work, string nombre)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // cierre normal
                }
                catch (Exception ex)
                {
                    ErrorLog.Write(ex);
                    Notify($"Error interno en el {nombre}: {ex.Message}", Tone.Warn);
                }
            });
        }

        // Vuelca a los paneles todo lo pendiente de pintar, agrupado por panel.
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            if (!_display.IsEmpty)
            {
                var batches = new List<ChatMessage>?[TabNames.Length];
                int count = 0;
                while (count < 500 && _display.TryDequeue(out var item))
                {
                    count++;
                    (batches[(int)item.Target] ??= new List<ChatMessage>()).Add(item.Message);
                }
                for (int i = 0; i < batches.Length; i++)
                    if (batches[i] is { } list) _views[i].Append(list);
            }

            if (++_statusTicks >= 8)
            {
                _statusTicks = 0;
                UpdateStatus();
                UpdateHint();
            }
        }

        // ======================================================================
        //  Entrada: log del juego → traducción → paneles
        // ======================================================================

        // Llamado desde el hilo del lector del log.
        private void OnLogLines(IReadOnlyList<string> lines)
        {
            foreach (var raw in lines)
            {
                // La misma línea dos veces seguidas en el mismo instante: el juego la registró por partida
                // doble (p. ej. con dos opciones de log activas). Se ignora la repetición.
                long now = Environment.TickCount64;
                if (raw == _lastRawLine && now - _lastRawAt < 1000 && raw.Trim().Length > 0) continue;
                _lastRawLine = raw;
                _lastRawAt = now;

                ParsedLine? line;
                try { line = _parser.Parse(raw); }
                catch { continue; }
                if (line == null) continue;
                if (line.Time == null) line = line with { Time = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) };

                bool translate = line.Kind == LineKind.Chat || !_filtrarSistema;
                if (line.Kind == LineKind.Chat) SignalEcho(line.Text);

                // La pestaña Inglés no espera a nadie: se pinta al instante.
                var b = new MessageBuilder();
                if (line.Kind == LineKind.Chat)
                {
                    Header(b, line);
                    Body(b, line.Text);
                }
                else
                {
                    SystemLine(b, line.Time, line.Text, translate ? Tone.Text : Tone.Dim);
                }
                Enqueue(ChatTarget.Ingles, b.Build());

                if (translate) _incoming.Enqueue(line);
            }
        }

        // Llamado desde el hilo del traductor.
        private void ShowTranslated(ParsedLine line, string translated)
        {
            bool same = Same(translated, line.Text);

            if (line.Kind == LineKind.Chat)
            {
                if (!ConsumeEcho(line.Text))
                {
                    var ambos = new MessageBuilder();
                    Header(ambos, line);
                    Body(ambos, translated);
                    if (!same) ambos.Para(ParaKind.Body).Add(line.Text, Tone.Dim, Face.Original);
                    Enqueue(ChatTarget.Ambos, ambos.Build());
                }

                var es = new MessageBuilder();
                Header(es, line);
                Body(es, translated);
                Enqueue(ChatTarget.Espanol, es.Build());
            }
            else
            {
                var ambos = new MessageBuilder();
                SystemLine(ambos, line.Time, translated, Tone.Text);
                if (!same) ambos.Para(ParaKind.Body).Add(line.Text, Tone.Dim, Face.Original);
                Enqueue(ChatTarget.Ambos, ambos.Build());

                var es = new MessageBuilder();
                SystemLine(es, line.Time, translated, Tone.Text);
                Enqueue(ChatTarget.Espanol, es.Build());
            }
        }

        // Llamado desde el hilo del traductor cuando no se pudo traducir a tiempo.
        private void ShowUntranslated(ParsedLine line, string reason)
        {
            if (line.Kind == LineKind.Chat)
            {
                if (!ConsumeEcho(line.Text))
                {
                    var ambos = new MessageBuilder();
                    Header(ambos, line, untranslated: true);
                    Body(ambos, line.Text);
                    ambos.Para(ParaKind.Body).Add(reason, Tone.Dim, Face.Small);
                    Enqueue(ChatTarget.Ambos, ambos.Build());
                }

                var es = new MessageBuilder();
                Header(es, line, untranslated: true);
                Body(es, line.Text);
                Enqueue(ChatTarget.Espanol, es.Build());
            }
            else
            {
                var ambos = new MessageBuilder();
                SystemLine(ambos, line.Time, line.Text, Tone.Dim);
                ambos.Add("  ", Tone.Dim, Face.Small).Tag("sin traducir", Tone.TagWarn);
                Enqueue(ChatTarget.Ambos, ambos.Build());

                var es = new MessageBuilder();
                SystemLine(es, line.Time, line.Text, Tone.Dim);
                Enqueue(ChatTarget.Espanol, es.Build());
            }
        }

        // ======================================================================
        //  Salida: lo que escribe el usuario → inglés → juego
        // ======================================================================

        private sealed class OutgoingItem
        {
            public required string Text { get; init; }
            public required bool Raw { get; init; }       // Ctrl+Enter: sin traducir
            public CancellationTokenSource Cts { get; } = new();
            public bool Committed { get; set; }           // ya se está escribiendo en el juego: no se puede cancelar
        }

        private void Input_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendCurrent(raw: e.Control);
            }
        }

        private void SendCurrent(bool raw = false)
        {
            var text = _input.Input.Text.Trim();
            if (text.Length == 0) return;
            _input.Input.Clear();

            // No se traduce aquí: la ventana nunca se congela esperando a la red.
            var item = new OutgoingItem { Text = text, Raw = raw };
            lock (_pendingOut) _pendingOut.Add(item);
            _outgoing.Writer.TryWrite(item);
            UpdateHint();
        }

        // Esc: cancela el último mensaje que aún no ha empezado a escribirse en el juego
        // y lo devuelve al campo de texto para poder corregirlo.
        private bool CancelLastPending()
        {
            OutgoingItem? cancelled = null;
            lock (_pendingOut)
            {
                for (int i = _pendingOut.Count - 1; i >= 0; i--)
                {
                    var it = _pendingOut[i];
                    if (!it.Committed && !it.Cts.IsCancellationRequested)
                    {
                        it.Cts.Cancel();
                        cancelled = it;
                        break;
                    }
                }
            }
            if (cancelled == null) return false;

            if (_input.Input.TextLength == 0)
            {
                _input.Input.Text = cancelled.Text;
                _input.Input.SelectionStart = _input.Input.TextLength;
            }
            Notify($"Cancelado: «{Shorten(cancelled.Text)}».", Tone.Dim);
            UpdateHint();
            _input.Input.Focus();
            return true;
        }

        private async Task OutgoingWorkerAsync(CancellationToken ct)
        {
            await foreach (var item in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (!await SendOneAsync(item, ct).ConfigureAwait(false)) break;
                }
                finally
                {
                    lock (_pendingOut) _pendingOut.Remove(item);
                    item.Cts.Dispose();
                }
            }
        }

        // Devuelve false solo si la aplicación se está cerrando.
        private async Task<bool> SendOneAsync(OutgoingItem item, CancellationToken appToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(appToken, item.Cts.Token);
            var token = linked.Token;

            var parts = OutgoingText.Split(item.Text);
            string body = parts.Body;
            bool translated = false;

            try
            {
                if (parts.Translate && !item.Raw)
                {
                    try
                    {
                        body = await _translator.TranslateOneWithRetryAsync(parts.Body, "es", "en", TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
                        translated = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Notify($"No se pudo traducir «{Shorten(item.Text)}»: {ex.Message}. Pulsa Ctrl+Enter para enviarlo sin traducir.", Tone.Warn);
                        RestoreToInput(item.Text);
                        return true;
                    }
                }

                if (_mayusculas && parts.IsChat) body = body.ToUpperInvariant();
                body = OutgoingText.ForGame(body);

                // Cuando el juego escriba el mensaje en el log se mostrará tu español original sin otra petición.
                if (translated) _translator.Remember(body, parts.Body, "en", "es");

                await Task.Delay(_settings.RetardoEnvioMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return !appToken.IsCancellationRequested; // cancelado con Esc: ya se avisó al cancelar
            }

            // A partir de aquí ya no se puede cancelar.
            lock (_pendingOut)
            {
                if (item.Cts.IsCancellationRequested) return true;
                item.Committed = true;
            }

            var textForGame = parts.Compose(body);
            var echoKey = parts.IsChat ? Normalize(body) : null;

            // Se apunta antes de enviar para no perder un eco que llegue muy rápido.
            var seen = echoKey != null && _logFound ? ExpectEcho(echoKey) : null;
            if (echoKey != null) RememberSent(echoKey);

            string? error;
            try { error = GameSender.Send(textForGame, _settings.VolverAlTraductor ? _ownHandle : IntPtr.Zero); }
            catch (Exception ex) { error = "Error al enviar al juego: " + ex.Message; }

            var status = SendStatus.Failed;
            if (error == null)
            {
                status = SendStatus.Sent;
                if (seen != null)
                {
                    // El juego escribe tus propios mensajes en el log: si aparece, salió de verdad.
                    var done = await Task.WhenAny(seen.Task, Task.Delay(5000, appToken)).ConfigureAwait(false);
                    if (appToken.IsCancellationRequested) return false;
                    status = done == seen.Task ? SendStatus.Confirmed : SendStatus.Unconfirmed;
                }
            }
            if (seen != null) ForgetEcho(seen);
            if (status == SendStatus.Failed && echoKey != null) ForgetSent(echoKey);

            Enqueue(ChatTarget.Ambos, BuildOwn(textForGame, item.Text, status));
            if (error != null)
            {
                Notify(error, Tone.Warn);
                RestoreToInput(item.Text);
            }
            else if (status == SendStatus.Unconfirmed)
            {
                Notify("Tu mensaje no aparece en el log del juego. Si se quedó escrito en el chat del juego, pulsa Enter allí.", Tone.Warn);
            }
            return true;
        }

        // Devuelve un mensaje que no pudo salir al campo de texto (si está vacío) para reintentarlo.
        private void RestoreToInput(string text)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (_input.Input.TextLength != 0) return;
                    _input.Input.Text = text;
                    _input.Input.SelectionStart = _input.Input.TextLength;
                });
            }
            catch (InvalidOperationException) { /* ventana cerrándose */ }
        }

        // Tu mensaje en el log puede llevar algo delante (p. ej. el servidor añade una etiqueta):
        // basta con que lo contenga entero.
        private static bool EchoMatches(string logKey, string sentKey) =>
            logKey == sentKey || (sentKey.Length >= 4 && logKey.Contains(sentKey, StringComparison.Ordinal));

        private void RememberSent(string key)
        {
            long now = Environment.TickCount64;
            lock (_recentSent)
            {
                _recentSent.RemoveAll(x => now - x.At > 60_000);
                _recentSent.Add((key, now));
            }
        }

        private void ForgetSent(string key)
        {
            lock (_recentSent)
            {
                int i = _recentSent.FindLastIndex(x => x.Text == key);
                if (i >= 0) _recentSent.RemoveAt(i);
            }
        }

        private TaskCompletionSource<bool> ExpectEcho(string key)
        {
            var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_awaitingEcho) _awaitingEcho.Add((key, seen));
            return seen;
        }

        private void ForgetEcho(TaskCompletionSource<bool> seen)
        {
            lock (_awaitingEcho) _awaitingEcho.RemoveAll(x => x.Seen == seen);
        }

        // Llamado desde el hilo del lector del log con cada línea de chat.
        private void SignalEcho(string text)
        {
            TaskCompletionSource<bool>? seen = null;
            var key = Normalize(text);
            lock (_awaitingEcho)
            {
                int i = _awaitingEcho.FindIndex(x => EchoMatches(key, x.Key));
                if (i >= 0)
                {
                    seen = _awaitingEcho[i].Seen;
                    _awaitingEcho.RemoveAt(i);
                }
            }
            seen?.TrySetResult(true);
        }

        private bool ConsumeEcho(string text)
        {
            var key = Normalize(text);
            long now = Environment.TickCount64;
            lock (_recentSent)
            {
                _recentSent.RemoveAll(x => now - x.At > 60_000);
                int i = _recentSent.FindIndex(x => EchoMatches(key, x.Text));
                if (i < 0) return false;
                _recentSent.RemoveAt(i);
                return true;
            }
        }

        private static string Normalize(string s) => Spaces.Replace(s.Trim(), " ").ToUpperInvariant();

        // ======================================================================
        //  Botones y ajustes
        // ======================================================================

        private void CapsBtn_Click(object? sender, EventArgs e)
        {
            _mayusculas = !_mayusculas;
            _capsBtn.Checked = _mayusculas;
            _settings.Mayusculas = _mayusculas;
            ScheduleSave();
        }

        private void FilterSwitch_CheckedChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            _filtrarSistema = _filterSwitch.Checked;
            _filterLink.Visible = _filtrarSistema;
            _settings.FiltrarSistema = _filtrarSistema;
            ScheduleSave();
        }

        private void ReturnSwitch_CheckedChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            _settings.VolverAlTraductor = _returnSwitch.Checked;
            ScheduleSave();
        }

        private void TopSwitch_CheckedChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            TopMost = _topSwitch.Checked;
            _settings.SiempreEncima = _topSwitch.Checked;
            ScheduleSave();
        }

        private void ThemeSeg_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var theme = Themes.All[_themeSeg.SelectedIndex];
            _settings.Tema = theme.Id;
            ApplyTheme(theme);
            ScheduleSave();
        }

        private void DelaySlider_ValueChanged(object? sender, EventArgs e)
        {
            int ms = _delaySlider.Value * 500;
            _delayValue.Text = FormatSeconds(ms);
            if (_suppressEvents) return;
            _settings.RetardoEnvioMs = ms;
            UpdateHint();
            ScheduleSave();
        }

        private void ProviderSeg_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            bool deepl = _providerSeg.SelectedIndex == 1;
            _settings.Proveedor = deepl ? "DeepL" : "Google";
            SetDeepLRowsVisible(deepl);

            if (!deepl)
            {
                _translator.DeepLKey = null;
            }
            else if (!string.IsNullOrEmpty(_settings.DeepLKey))
            {
                _translator.DeepLKey = _settings.DeepLKey;
            }
            else
            {
                _keyBox.Input.Focus();
                SetKeyStatus("Pega tu clave y pulsa Comprobar. Mientras tanto se sigue usando Google.", Tone.Dim);
            }

            ScheduleSave();
            UpdateStatus();
        }

        private async void CheckKey_Click(object? sender, EventArgs e)
        {
            var key = _keyBox.Input.Text.Trim();
            if (key.Length == 0)
            {
                SetKeyStatus("Pega tu clave de DeepL y pulsa Comprobar.", Tone.Warn);
                return;
            }

            _checkBtn.Enabled = false;
            _checkBtn.Text = "Comprobando…";
            SetKeyStatus("Comprobando la clave…", Tone.Dim);
            try
            {
                var usage = await _translator.CheckDeepLKeyAsync(key, _cts.Token);
                ApplyDeepLKey(key);
                SetKeyStatus(key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
                    ? "✓ Clave válida, plan gratuito"
                    : "✓ Clave válida, plan Pro", Tone.Ok);
                ShowUsage(usage);
            }
            catch (DeepLUnavailableException ex)
            {
                SetKeyStatus("DeepL rechazó la clave: " + ex.Message + ". Se sigue usando el traductor anterior.", Tone.Warn);
            }
            catch (OperationCanceledException)
            {
                return; // cerrando la ventana
            }
            catch (Exception ex)
            {
                // Sin conexión para comprobarla: se guarda igualmente.
                ApplyDeepLKey(key);
                SetKeyStatus("No se pudo comprobar (" + ex.Message + "). Se usará igualmente.", Tone.Dim);
            }
            finally
            {
                if (!IsDisposed)
                {
                    _checkBtn.Text = "Comprobar";
                    _checkBtn.Enabled = true;
                    UpdateStatus();
                }
            }
        }

        private async Task RefreshUsageAsync(string key)
        {
            try
            {
                ShowUsage(await _translator.CheckDeepLKeyAsync(key, _cts.Token));
            }
            catch (DeepLUnavailableException ex)
            {
                SetKeyStatus("DeepL no acepta la clave guardada: " + ex.Message + ".", Tone.Warn);
            }
            catch
            {
                // sin conexión al arrancar: se verá al pulsar Comprobar
            }
        }

        private void ApplyDeepLKey(string key)
        {
            _settings.DeepLKey = key;
            _settings.Proveedor = "DeepL";
            _translator.DeepLKey = key;
            _providerSeg.SetSelectedSilently(1);
            _settings.Save();
        }

        private void SetKeyStatus(string text, Tone tone)
        {
            _keyStatus.Text = text;
            _keyStatus.Tone = tone;
        }

        private void ShowUsage(DeepLUsage usage)
        {
            if (IsDisposed) return;
            if (usage.Limit > 0)
            {
                _usageBar.Value = (double)usage.Count / usage.Limit;
                _usageLabel.Text = $"{FormatCount(usage.Count)} de {FormatCount(usage.Limit)} caracteres";
                _usageLabel.Tone = usage.Count >= usage.Limit * 0.9 ? Tone.Warn : Tone.Text;
            }
            else
            {
                _usageBar.Value = 0;
                _usageLabel.Text = "DeepL no ha indicado el consumo.";
                _usageLabel.Tone = Tone.Dim;
            }
        }

        // ======================================================================
        //  Construcción de mensajes
        // ======================================================================

        private void Enqueue(ChatTarget target, ChatMessage message) => _display.Enqueue((target, message));

        private void Notify(string text, Tone tone) =>
            Enqueue(ChatTarget.Ambos, new MessageBuilder().Para(ParaKind.Notice).Add(text, tone, Face.Small).Build());

        private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        // "14:31  Kael Ironfist  [grupo]"
        private static void Header(MessageBuilder b, ParsedLine line, bool untranslated = false)
        {
            b.Para(ParaKind.Header)
             .Add((line.Time ?? "") + "\t", Tone.Dim, Face.Small)
             .Add(line.Speaker ?? "?", Tone.Accent, Face.Name);
            if (ChannelTag(line.Channel) is { } tag) b.Add("  ", Tone.Dim, Face.Small).Tag(tag.Label, tag.Tone);
            if (untranslated) b.Add("  ", Tone.Dim, Face.Small).Tag("sin traducir", Tone.TagWarn);
        }

        private static void Body(MessageBuilder b, string text)
        {
            b.Para(ParaKind.Body);
            AddMessageParts(b, text, Tone.Text);
        }

        // Combate y avisos del servidor: "14:35  texto", más pequeño.
        private static void SystemLine(MessageBuilder b, string? time, string text, Tone tone)
        {
            b.Para(ParaKind.Header)
             .Add((time ?? "") + "\t", Tone.Dim, Face.Small)
             .Add(text, tone, Face.Original);
        }

        private enum SendStatus { Confirmed, Sent, Unconfirmed, Failed }

        // Tus mensajes, alineados a la derecha.
        private static ChatMessage BuildOwn(string textEn, string textEs, SendStatus status)
        {
            var b = new MessageBuilder().Para(ParaKind.Own);
            switch (status)
            {
                case SendStatus.Confirmed:
                case SendStatus.Sent:
                    b.Add("✓ enviado", Tone.Ok, Face.Small);
                    break;
                case SendStatus.Unconfirmed:
                    b.Add("sin confirmar", Tone.Warn, Face.Small);
                    break;
                default:
                    b.Add("no enviado", Tone.Warn, Face.Small);
                    break;
            }
            b.Add("   " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "   ", Tone.Dim, Face.Small)
             .Add("Tú", Tone.Text, Face.Name);

            b.Para(ParaKind.Own);
            AddMessageParts(b, textEn, Tone.Text);
            if (!Same(textEn, textEs)) b.Para(ParaKind.Own).Add(textEs, Tone.Dim, Face.Original);
            return b.Build();
        }

        private static (string Label, Tone Tone)? ChannelTag(string? channel)
        {
            if (string.IsNullOrEmpty(channel) || channel.Equals("Talk", StringComparison.OrdinalIgnoreCase)) return null;
            return channel.ToLowerInvariant() switch
            {
                "tell" => ("privado", Tone.TagPrivate),
                "whisper" => ("susurro", Tone.TagPrivate),
                "party" => ("grupo", Tone.TagParty),
                "shout" => ("grito", Tone.TagShout),
                "dm" => ("DM", Tone.TagDm),
                "server" => ("servidor", Tone.TagOther),
                _ => (channel.ToLowerInvariant(), Tone.TagOther)
            };
        }

        // Separa el mensaje en texto normal y *acciones*, que se muestran en cursiva y en otro color.
        private static void AddMessageParts(MessageBuilder b, string message, Tone tone)
        {
            int last = 0;
            foreach (Match m in EmoteRegex.Matches(message))
            {
                if (m.Index > last) b.Add(message.Substring(last, m.Index - last), tone, Face.Body);
                b.Add(m.Groups[1].Value.Trim(), Tone.Emote, Face.BodyItalic);
                last = m.Index + m.Length;
            }
            if (last < message.Length) b.Add(message.Substring(last), tone, Face.Body);
        }
    }
}
