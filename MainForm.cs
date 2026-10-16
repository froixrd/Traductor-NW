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

        // Orden de las opciones del selector de traductor (Microsoft primero: es el recomendado).
        private static readonly Proveedor[] SegProviders = { Proveedor.Microsoft, Proveedor.Google, Proveedor.DeepL };

        // Caracteres gratis al mes del plan F0 de Microsoft Translator.
        private const long MicrosoftFreeChars = 2_000_000;

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
        private readonly System.Windows.Forms.Timer _glossaryTimer = new() { Interval = 800 };
        private readonly System.Windows.Forms.Timer _opacityPreviewTimer = new() { Interval = 1000 };

        // Tecla rápida global (Ctrl+Mayús+Espacio).
        private const int HotkeyId = 0x4E57;
        private bool _hotkeyRegistered;
        private IntPtr _hotkeyReturnTo;   // ventana desde la que se llamó con la tecla rápida (el juego)
        private long _lastAlertAt;
        private volatile bool _usageDirty;
        private DeepLUsage? _deeplUsage;

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
        private ToggleSwitch _hotkeySwitch = null!;
        private ToggleSwitch _alertSwitch = null!;
        private ToggleSwitch _compactSwitch = null!;
        private Slider _opacitySlider = null!;
        private ThemedLabel _opacityValue = null!;
        private ThemedLabel _keyLabel = null!;
        private ThemedPanel _keyCell = null!;
        private FieldBox _regionBox = null!;
        private ThemedLabel _usageRowLabel = null!;
        private ThemedPanel _usageCell = null!;
        private FieldBox _glossaryBox = null!;
        private ToggleSwitch _fallbackSwitch = null!;
        private ThemedLabel _fallbackOrder = null!;
        private ThemedLabel _fallbackRowLabel = null!;
        private ThemedPanel _fallbackCell = null!;

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

            if (LoadAppIcon() is System.Drawing.Icon appIcon) Icon = appIcon;

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
            _translator.Notice += (msg, warn) => Notify(msg, warn ? Tone.Warn : Tone.Ok);
            _translator.CharactersUsed += (provider, chars) =>
            {
                if (provider != Proveedor.Microsoft) return;
                _settings.AddMicrosoftUsage(chars);
                _usageDirty = true;
            };

            LoadSettingsIntoUi();
            ApplyTheme(_theme);
            RestoreWindowBounds();

            _uiTimer.Tick += UiTimer_Tick;
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                _settings.Save();
            };

            _glossaryTimer.Tick += (s, e) => ApplyGlossary();
            _opacityPreviewTimer.Tick += (s, e) =>
            {
                _opacityPreviewTimer.Stop();
                if (ActiveForm == this) Opacity = 1.0;
            };

            // Transparencia solo mientras no estás usando el traductor.
            Activated += (s, e) =>
            {
                if (Opacity < 1.0) Opacity = 1.0;
            };
            Deactivate += (s, e) =>
            {
                _hotkeyReturnTo = IntPtr.Zero;
                if (_settings.OpacidadInactiva < 100) Opacity = _settings.OpacidadInactiva / 100.0;
            };
            Resize += (s, e) => LayoutSettings();

            Load += MainForm_Load;
            Shown += (s, e) =>
            {
                if (_settingsPanel.Visible && _keyCell.Visible && _keyBox.Input.TextLength == 0) _keyBox.Input.Focus();
                else _input.Input.Focus();
            };
            FormClosing += MainForm_FormClosing;
        }

        // ======================================================================
        //  Construcción de la interfaz
        // ======================================================================

        // El icono con todos sus tamaños (16 a 256 px): Windows elige el adecuado para la barra de título,
        // la barra de tareas y Alt+Tab, también con zoom de pantalla.
        private static System.Drawing.Icon? LoadAppIcon()
        {
            try
            {
                using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("TraductorNWN.app.ico");
                if (stream != null) return new System.Drawing.Icon(stream);
            }
            catch { /* se prueba con el del .exe */ }
            try
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                return null; // icono genérico de Windows
            }
        }

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
                AutoScroll = true,
                Padding = new Padding(Dpi.S(20), Dpi.S(18), Dpi.S(20), Dpi.S(19))
            };
            _settingsPanel.HandleCreated += (s, e) =>
            {
                try { NativeMethods.SetWindowTheme(_settingsPanel.Handle, "DarkMode_Explorer", null); }
                catch { /* Windows antiguo: barra clara */ }
            };

            _settingsTable = new TableLayoutPanel
            {
                AutoSize = false,
                ColumnCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Location = new Point(Dpi.S(20), Dpi.S(18))
            };
            _settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(170)));
            _settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // --- Traductor ---
            _providerSeg = new SegmentedControl { Anchor = AnchorStyles.Left | AnchorStyles.Top };
            foreach (var provider in SegProviders)
                _providerSeg.AddOption(provider == Proveedor.Google ? "Google Translate" : TranslationService.Name(provider));
            _providerSeg.SelectedIndexChanged += ProviderSeg_SelectedIndexChanged;
            AddSettingsRow("Traductor", _providerSeg, _providerSeg.Height);

            // --- Clave (DeepL o Microsoft) ---
            _keyCell = NewCell(Dpi.S(66));
            _keyBox = new FieldBox { Radius = 8, InsetX = 12 };
            _keyBox.Input.UseSystemPasswordChar = true;
            _keyBox.Input.Font = UiFonts.Ui(10.5f);
            _keyBox.Input.KeyDown += KeyField_KeyDown;
            _regionBox = new FieldBox { Radius = 8, InsetX = 10 };
            _regionBox.Input.Font = UiFonts.Ui(10.5f);
            _regionBox.Input.Hint = "Región";
            _regionBox.Input.AccessibleName = "Región del recurso de Microsoft Translator (vacío si es global)";
            _regionBox.Input.KeyDown += KeyField_KeyDown;
            _checkBtn = new FlatButton { Kind = ButtonKind.Outline, Text = "Comprobar" };
            _checkBtn.Click += CheckKey_Click;
            _keyStatus = new ThemedLabel { Font = UiFonts.Ui(9f), Tone = Tone.Dim, AutoEllipsis = true };
            _keyCell.Controls.AddRange(new Control[] { _keyBox, _regionBox, _checkBtn, _keyStatus });
            _keyCell.Resize += (s, e) => LayoutKeyCell();
            _keyLabel = AddSettingsRow("Clave", _keyCell, Dpi.S(40));

            // --- Uso este mes (DeepL) ---
            _usageCell = NewCell(Dpi.S(38));
            _usageBar = new ThinProgress { AccessibleName = "Caracteres de DeepL usados este mes" };
            _usageLabel = new ThemedLabel { Font = UiFonts.Ui(9f), Tone = Tone.Dim, AutoEllipsis = true };
            _usageCell.Controls.AddRange(new Control[] { _usageBar, _usageLabel });
            _usageCell.Resize += (s, e) =>
            {
                int w = _usageCell.ClientSize.Width;
                _usageBar.SetBounds(0, Dpi.S(5), w, Dpi.S(8));
                _usageLabel.SetBounds(0, Dpi.S(17), w, Dpi.S(20));
            };
            _usageRowLabel = AddSettingsRow("Uso este mes", _usageCell, Dpi.S(18));

            // --- Respaldo: Google cuando no funciona ninguno de los de pago ---
            _fallbackCell = NewCell(Dpi.S(50));
            _fallbackSwitch = new ToggleSwitch { AccessibleName = "Usar Google Translate si Microsoft y DeepL no funcionan" };
            _fallbackSwitch.CheckedChanged += FallbackSwitch_CheckedChanged;
            var fallbackCaption = new ThemedLabel
            {
                Text = "Usar Google si Microsoft y DeepL no funcionan",
                Font = UiFonts.Ui(10f),
                Tone = Tone.Text,
                AutoEllipsis = true
            };
            _fallbackOrder = new ThemedLabel { Font = UiFonts.Ui(9f), Tone = Tone.Dim, AutoEllipsis = true };
            _fallbackCell.Controls.AddRange(new Control[] { _fallbackSwitch, fallbackCaption, _fallbackOrder });
            _fallbackCell.Resize += (s, e) =>
            {
                int w = _fallbackCell.ClientSize.Width, x = Dpi.S(56);
                _fallbackSwitch.Location = Point.Empty;
                fallbackCaption.SetBounds(x, 0, Math.Max(10, w - x), Dpi.S(26));
                _fallbackOrder.SetBounds(x, Dpi.S(28), Math.Max(10, w - x), Dpi.S(20));
            };
            _fallbackRowLabel = AddSettingsRow("Respaldo", _fallbackCell, Dpi.S(26));

            // --- Palabras que no se traducen ---
            var glossaryCell = NewCell(Dpi.S(64));
            _glossaryBox = new FieldBox { Radius = 8, InsetX = 12 };
            _glossaryBox.Input.Font = UiFonts.Ui(10.5f);
            _glossaryBox.Input.Hint = "Seraphine, Kael, Aguas Profundas";
            _glossaryBox.Input.AccessibleName = "Palabras que no se traducen, separadas por comas";
            _glossaryBox.Input.TextChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _glossaryTimer.Stop();
                _glossaryTimer.Start();
            };
            var glossaryDesc = new ThemedLabel
            {
                Text = "Nombres, lugares o jerga del servidor, separados por comas. Se quedan tal cual.",
                Font = UiFonts.Ui(9f),
                Tone = Tone.Dim,
                AutoEllipsis = true
            };
            glossaryCell.Controls.AddRange(new Control[] { _glossaryBox, glossaryDesc });
            glossaryCell.Resize += (s, e) =>
            {
                int w = glossaryCell.ClientSize.Width;
                _glossaryBox.SetBounds(0, 0, w, Dpi.S(40));
                glossaryDesc.SetBounds(0, Dpi.S(44), w, Dpi.S(20));
            };
            AddSettingsRow("No traducir", glossaryCell, Dpi.S(40));

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

            // --- Después de enviar / tecla rápida ---
            _returnSwitch = AddSwitchRow("Después de enviar", "Volver al traductor para seguir escribiendo", ReturnSwitch_CheckedChanged);
            _hotkeySwitch = AddSwitchRow("Tecla rápida", "Ctrl+Mayús+Espacio salta entre el juego y el traductor", HotkeySwitch_CheckedChanged);

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

            // --- Mensajes privados ---
            _alertSwitch = AddSwitchRow("Mensajes privados", "Avisar con un sonido y haciendo parpadear la ventana", AlertSwitch_CheckedChanged);

            // --- Colores ---
            _themeSeg = new SegmentedControl { Anchor = AnchorStyles.Left | AnchorStyles.Top };
            foreach (var theme in Themes.All) _themeSeg.AddOption(theme.Name, theme.Accent);
            _themeSeg.SelectedIndexChanged += ThemeSeg_SelectedIndexChanged;
            AddSettingsRow("Colores", _themeSeg, _themeSeg.Height);

            // --- Ventana: siempre encima y compacta ---
            var windowCell = NewCell(Dpi.S(26));
            _topSwitch = new ToggleSwitch { AccessibleName = "Mantener la ventana siempre encima del juego" };
            _topSwitch.CheckedChanged += TopSwitch_CheckedChanged;
            var topLabel = new ThemedLabel { Text = "Siempre encima", Font = UiFonts.Ui(10f), Tone = Tone.Text, AutoEllipsis = true };
            _compactSwitch = new ToggleSwitch { AccessibleName = "Ventana compacta: sin título, pestañas ni ayuda" };
            _compactSwitch.CheckedChanged += CompactSwitch_CheckedChanged;
            var compactLabel = new ThemedLabel { Text = "Compacta", Font = UiFonts.Ui(10f), Tone = Tone.Text, AutoEllipsis = true };
            windowCell.Controls.AddRange(new Control[] { _topSwitch, topLabel, _compactSwitch, compactLabel });
            windowCell.Resize += (s, e) =>
            {
                int w = windowCell.ClientSize.Width, x = Dpi.S(56);
                int half = Math.Max(Dpi.S(200), w / 2);
                _topSwitch.Location = Point.Empty;
                topLabel.SetBounds(x, 0, Math.Max(10, half - x - Dpi.S(8)), Dpi.S(26));
                _compactSwitch.Location = new Point(half, 0);
                compactLabel.SetBounds(half + x, 0, Math.Max(10, w - half - x), Dpi.S(26));
            };
            AddSettingsRow("Ventana", windowCell, Dpi.S(26));

            // --- Transparencia sin foco ---
            var opacityCell = NewCell(Dpi.S(48));
            _opacitySlider = new Slider { Minimum = 4, Maximum = 10, AccessibleName = "Opacidad de la ventana cuando no la estás usando" };
            _opacitySlider.ValueChanged += OpacitySlider_ValueChanged;
            _opacityValue = new ThemedLabel { Font = UiFonts.Ui(9.75f), Tone = Tone.Text, TextAlign = ContentAlignment.MiddleRight };
            var opacityDesc = new ThemedLabel
            {
                Text = "Cuando no estás usando el traductor, para ver el juego a través.",
                Font = UiFonts.Ui(9f),
                Tone = Tone.Dim,
                AutoEllipsis = true
            };
            opacityCell.Controls.AddRange(new Control[] { _opacitySlider, _opacityValue, opacityDesc });
            opacityCell.Resize += (s, e) =>
            {
                int w = opacityCell.ClientSize.Width;
                _opacityValue.SetBounds(w - Dpi.S(52), 0, Dpi.S(52), Dpi.S(28));
                _opacitySlider.SetBounds(0, 0, Math.Max(40, w - Dpi.S(60)), Dpi.S(28));
                opacityDesc.SetBounds(0, Dpi.S(28), w, Dpi.S(20));
            };
            var lastLabel = AddSettingsRow("Transparencia", opacityCell, Dpi.S(28));
            lastLabel.Margin = new Padding(0, 0, Dpi.S(12), 0);
            opacityCell.Margin = Padding.Empty;

            _settingsPanel.Controls.Add(_settingsTable);
        }

        private ToggleSwitch AddSwitchRow(string label, string text, EventHandler onChanged)
        {
            var cell = NewCell(Dpi.S(26));
            var toggle = new ToggleSwitch { AccessibleName = text };
            toggle.CheckedChanged += onChanged;
            var caption = new ThemedLabel { Text = text, Font = UiFonts.Ui(10f), Tone = Tone.Text, AutoEllipsis = true };
            cell.Controls.AddRange(new Control[] { toggle, caption });
            cell.Resize += (s, e) =>
            {
                toggle.Location = Point.Empty;
                caption.SetBounds(Dpi.S(56), 0, Math.Max(10, cell.ClientSize.Width - Dpi.S(56)), Dpi.S(26));
            };
            AddSettingsRow(label, cell, Dpi.S(26));
            return toggle;
        }

        private void LayoutKeyCell()
        {
            int w = _keyCell.ClientSize.Width, gap = Dpi.S(10), h = Dpi.S(40);
            int bw = Gfx.Measure("Comprobando…", UiFonts.UiStrong(9.75f)).Width + Dpi.S(32);
            _checkBtn.SetBounds(w - bw, 0, bw, h);
            int right = _checkBtn.Left - gap;
            if (_regionBox.Visible)
            {
                int rw = Dpi.S(130);
                _regionBox.SetBounds(right - rw, 0, rw, h);
                right = _regionBox.Left - gap;
            }
            _keyBox.SetBounds(0, 0, Math.Max(40, right), h);
            _keyStatus.SetBounds(0, Dpi.S(46), w, Dpi.S(20));
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

        // El panel de ajustes crece con su contenido hasta dejar sitio al chat; si no cabe, se desplaza.
        private void LayoutSettings()
        {
            if (_settingsPanel == null || !_settingsPanel.Visible) return;
            var pad = _settingsPanel.Padding;
            int available = ClientSize.Height - _header.Height - (_tabRow.Visible ? _tabRow.Height : 0) - _footer.Height;
            int maxHeight = Math.Max(Dpi.S(160), available - Dpi.S(150));

            int tableWidth = Math.Max(Dpi.S(240), _settingsPanel.Width - pad.Horizontal);
            int contentHeight = _settingsTable.GetPreferredSize(new Size(tableWidth, 0)).Height;
            int desired = pad.Vertical + contentHeight + 1;
            bool scroll = desired > maxHeight;
            if (scroll)
            {
                tableWidth -= SystemInformation.VerticalScrollBarWidth;
                contentHeight = _settingsTable.GetPreferredSize(new Size(tableWidth, 0)).Height;
                desired = pad.Vertical + contentHeight + 1;
            }

            _settingsTable.Size = new Size(tableWidth, contentHeight);
            _settingsPanel.AutoScrollMinSize = scroll ? new Size(0, desired) : Size.Empty;
            var offset = _settingsPanel.AutoScrollPosition;
            _settingsTable.Location = new Point(pad.Left + offset.X, pad.Top + offset.Y);
            int height = Math.Min(desired, maxHeight);
            if (_settingsPanel.Height != height) _settingsPanel.Height = height;
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
                // Las dos claves se cargan siempre: si Google bloquea, se usa la otra mientras tanto.
                _translator.SetKey(Proveedor.DeepL, _settings.DeepLKey);
                _translator.SetKey(Proveedor.Microsoft, _settings.MicrosoftKey, _settings.MicrosoftRegion);
                _translator.GoogleFallback = _settings.GoogleDeRespaldo;
                _fallbackSwitch.Checked = _settings.GoogleDeRespaldo;
                var preferred = ParseProvider(_settings.Proveedor);
                _translator.Preferred = preferred;
                _providerSeg.SetSelectedSilently(Array.IndexOf(SegProviders, preferred));
                ShowProviderRows(preferred);

                _glossaryBox.Input.Text = string.Join(", ", _settings.NoTraducir);
                _translator.SetProtectedTerms(_settings.NoTraducir);

                _delaySlider.Value = (int)Math.Round(_settings.RetardoEnvioMs / 500.0);
                _delayValue.Text = FormatSeconds(_delaySlider.Value * 500);

                _filtrarSistema = _settings.FiltrarSistema;
                _filterSwitch.Checked = _filtrarSistema;
                _filterLink.Visible = _filtrarSistema;

                _themeSeg.SetSelectedSilently(Math.Max(0, Themes.IndexOf(_theme)));

                _topSwitch.Checked = _settings.SiempreEncima;
                TopMost = _settings.SiempreEncima;
                _returnSwitch.Checked = _settings.VolverAlTraductor;
                _hotkeySwitch.Checked = _settings.TeclaRapida;
                _alertSwitch.Checked = _settings.AvisarPrivados;
                _compactSwitch.Checked = _settings.Compacto;
                _opacitySlider.Value = (int)Math.Round(_settings.OpacidadInactiva / 10.0);
                _opacityValue.Text = $"{_opacitySlider.Value * 10} %";

                _mayusculas = _settings.Mayusculas;
                _capsBtn.Checked = _mayusculas;
            }
            finally
            {
                _suppressEvents = false;
            }
            ApplyCompact(_settings.Compacto);
        }

        private static Proveedor ParseProvider(string? name) =>
            Enum.TryParse<Proveedor>(name, true, out var p) && Enum.IsDefined(p) ? p : Proveedor.Microsoft;

        private Proveedor SelectedProvider => SegProviders[Math.Clamp(_providerSeg.SelectedIndex, 0, SegProviders.Length - 1)];

        // Muestra la fila de la clave y la del consumo según el traductor elegido.
        private void ShowProviderRows(Proveedor p)
        {
            bool keyed = p != Proveedor.Google;
            _keyLabel.Visible = keyed;
            _keyCell.Visible = keyed;
            _usageRowLabel.Visible = keyed;
            _usageCell.Visible = keyed;
            _regionBox.Visible = p == Proveedor.Microsoft;
            _fallbackRowLabel.Visible = keyed;
            _fallbackCell.Visible = keyed;
            UpdateFallbackOrder();

            if (keyed)
            {
                bool deepl = p == Proveedor.DeepL;
                _keyLabel.Text = deepl ? "Clave de DeepL" : "Clave de Microsoft";
                _keyBox.Input.Hint = deepl ? "Pega aquí tu clave de DeepL" : "Pega aquí tu clave de Microsoft Translator";
                _keyBox.Input.AccessibleName = _keyLabel.Text;
                _keyBox.Input.Text = (deepl ? _settings.DeepLKey : _settings.MicrosoftKey) ?? "";
                _regionBox.Input.Text = _settings.MicrosoftRegion ?? "";

                switch (_translator.GetState(p))
                {
                    case ProviderState.NoKey:
                        SetKeyStatus(deepl
                            ? "Pega tu clave y pulsa Comprobar. Mientras tanto se usa Google."
                            : "Pega la clave de tu recurso Translator (plan gratuito F0). Mientras tanto se usa Google.",
                            Tone.Dim);
                        break;
                    case ProviderState.QuotaExhausted:
                        SetKeyStatus("Cuota gratuita agotada: se usa Google y se vuelve a probar cada hora.", Tone.Warn);
                        break;
                    case ProviderState.InvalidKey:
                        SetKeyStatus("La clave guardada ha dejado de funcionar: pulsa Comprobar o pega otra.", Tone.Warn);
                        break;
                    default:
                        SetKeyStatus("Clave guardada. Si se acaba la cuota, se usa Google.", Tone.Dim);
                        break;
                }

                if (deepl)
                {
                    if (_deeplUsage is DeepLUsage usage) ShowUsage(usage);
                    else
                    {
                        _usageBar.Value = 0;
                        _usageLabel.Text = "Pulsa Comprobar para ver el consumo de este mes.";
                        _usageLabel.Tone = Tone.Dim;
                    }
                }
                else
                {
                    ShowMicrosoftUsage();
                }
                LayoutKeyCell();
            }
            LayoutSettings();
        }

        // "Orden: Microsoft → DeepL → Google", con los traductores que tienen clave guardada.
        private void UpdateFallbackOrder()
        {
            var chain = _translator.ConfiguredChain();
            var names = string.Join(" → ", chain.Select(p => p == Proveedor.Google ? "Google" : TranslationService.Name(p)));
            bool google = chain.Contains(Proveedor.Google);
            _fallbackOrder.Text = google
                ? "Orden: " + names
                : "Orden: " + names + ". Si fallan, los mensajes se muestran sin traducir.";
            _fallbackOrder.Tone = google ? Tone.Dim : Tone.Warn;
        }

        // Ventana compacta: sin título, pestañas ni línea de ayuda, para dejarla pequeña sobre el juego.
        private void ApplyCompact(bool compact)
        {
            _header.Height = Dpi.S(compact ? 56 : 68);
            _title.Visible = !compact;
            _subtitle.Visible = !compact;
            _tabRow.Visible = !compact;
            _hint.Visible = !compact;
            _footer.Height = Dpi.S(compact ? 72 : 98);
            MinimumSize = compact ? new Size(Dpi.S(400), Dpi.S(320)) : new Size(Dpi.S(560), Dpi.S(560));
            LayoutHeader();
            LayoutSettings();
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
            if (_settings.TeclaRapida) RegisterHotkey();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotkey();
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && (int)m.WParam == HotkeyId)
            {
                OnHotkey();
                return;
            }
            base.WndProc(ref m);
        }

        private void RegisterHotkey()
        {
            if (_hotkeyRegistered || !IsHandleCreated) return;
            _hotkeyRegistered = NativeMethods.RegisterHotKey(Handle, HotkeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SPACE);
            if (!_hotkeyRegistered)
                Notify("No se pudo activar Ctrl+Mayús+Espacio: otro programa ya usa esa combinación.", Tone.Warn);
        }

        private void UnregisterHotkey()
        {
            if (!_hotkeyRegistered) return;
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        // Ctrl+Mayús+Espacio: desde el juego salta al campo de escribir; desde el traductor vuelve al juego.
        // Un mensaje escrito tras saltar con la tecla se envía y te deja en el juego.
        private void OnHotkey()
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == Handle)
            {
                var game = _hotkeyReturnTo != IntPtr.Zero && NativeMethods.IsWindow(_hotkeyReturnTo)
                    ? _hotkeyReturnTo
                    : GameSender.FindGameWindow();
                _hotkeyReturnTo = IntPtr.Zero;
                if (game != IntPtr.Zero) WindowFocus.BringToFront(game);
                return;
            }

            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            WindowFocus.BringToFront(Handle);
            Activate();
            _hotkeyReturnTo = foreground;
            _input.Input.Focus();
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
                LayoutSettings();
            }
            else
            {
                if (_glossaryTimer.Enabled) ApplyGlossary();
                _input.Input.Focus();
            }
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
            var active = _translator.ActiveProvider;
            string shortName = active is Proveedor a ? TranslationService.Name(a) : "Sin traductor";
            string longName = active == Proveedor.Google ? "Google Translate" : shortName;
            var cooldown = _translator.CooldownRemaining;
            int pending = _incoming.Pending;

            // Si no se está usando el traductor elegido, el motivo (sin clave, sin cuota, en pausa…).
            string note = "";
            var noteTone = Tone.Dim;
            var preferred = _translator.Preferred;
            if (preferred != active && preferred != Proveedor.Google)
            {
                var name = TranslationService.Name(preferred);
                switch (_translator.GetState(preferred))
                {
                    case ProviderState.NoKey:
                        note = "sin clave de " + name;
                        break;
                    case ProviderState.QuotaExhausted:
                        note = name + " sin cuota";
                        noteTone = Tone.Warn;
                        break;
                    case ProviderState.InvalidKey:
                        note = "clave de " + name + " no válida";
                        noteTone = Tone.Warn;
                        break;
                    default:
                        if (_translator.IsPaused(preferred)) note = name + " en pausa";
                        break;
                }
            }

            bool resized;
            if (active == null)
            {
                // Ninguno funciona y el respaldo con Google está desactivado.
                resized = _pill.SetState(Tone.Alert, "Sin traductor", Tone.Warn, note, Tone.Dim, warn: true);
            }
            else if (cooldown.TotalSeconds >= 1)
            {
                resized = _pill.SetState(Tone.Alert, $"Pausa {Math.Ceiling(cooldown.TotalSeconds)} s", Tone.Warn,
                    pending > 0 ? $"{pending} en cola" : "", Tone.Dim, warn: true);
            }
            else if (!_logFound)
            {
                resized = _pill.SetState(Tone.Dim, "Esperando al juego", Tone.Dim, shortName, Tone.Dim, warn: false);
            }
            else if (pending >= 3)
            {
                resized = _pill.SetState(Tone.Accent, shortName, Tone.Text, $"{pending} en cola", Tone.Dim, warn: false);
            }
            else
            {
                resized = _pill.SetState(Tone.Ok, longName, Tone.Text, note, noteTone, warn: false);
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
                    var provider = _translator.ActiveProvider is Proveedor ap ? TranslationService.Name(ap) : "el traductor";
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

            if (_translator.Preferred == Proveedor.DeepL && _translator.GetKey(Proveedor.DeepL) is string key)
                _ = RefreshUsageAsync(key);
            CheckGameChatLogging();
            CheckMicrosoftSetup();
        }

        // Microsoft es el traductor principal: si aún no hay clave, se explica y (la primera vez) se abren los ajustes.
        private void CheckMicrosoftSetup()
        {
            if (_translator.Preferred != Proveedor.Microsoft || _translator.GetKey(Proveedor.Microsoft) != null) return;
            var now = _translator.ActiveProvider is Proveedor ap ? TranslationService.Name(ap) : "ningún traductor";
            Notify($"Ahora se traduce con {now}. Para usar Microsoft Translator (gratis hasta 2 millones de caracteres al mes) " +
                   "pega tu clave en Ajustes → Traductor. Los pasos para conseguirla están en el README del proyecto.", Tone.Accent);
            if (_settings.MigradoAMicrosoft) ToggleSettings(true);
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
            if (_glossaryTimer.Enabled) ApplyGlossary();
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
                _glossaryTimer.Dispose();
                _opacityPreviewTimer.Dispose();
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

                if (_usageDirty)
                {
                    _usageDirty = false;
                    if (!_saveTimer.Enabled) _saveTimer.Start(); // sin retrasarlo: hay tráfico continuo
                    if (_usageCell.Visible && SelectedProvider == Proveedor.Microsoft) ShowMicrosoftUsage();
                }
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
                if (line.Kind == LineKind.Chat)
                {
                    SignalEcho(line.Text);
                    if (_settings.AvisarPrivados && IsPrivate(line.Channel) && !WasRecentlySent(line.Text))
                        AlertPrivate();
                }

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
            public required bool ReturnToApp { get; init; } // volver al traductor después de enviar
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
            // Si se llegó con la tecla rápida desde el juego, después de enviar se queda en el juego.
            var item = new OutgoingItem { Text = text, Raw = raw, ReturnToApp = _settings.VolverAlTraductor && _hotkeyReturnTo == IntPtr.Zero };
            _hotkeyReturnTo = IntPtr.Zero;
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
            try { error = GameSender.Send(textForGame, item.ReturnToApp ? _ownHandle : IntPtr.Zero); }
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

        private bool WasRecentlySent(string text)
        {
            var key = Normalize(text);
            long now = Environment.TickCount64;
            lock (_recentSent)
                return _recentSent.Any(x => now - x.At <= 60_000 && EchoMatches(key, x.Text));
        }

        private static bool IsPrivate(string? channel) =>
            string.Equals(channel, "Tell", StringComparison.OrdinalIgnoreCase);

        // Mensaje privado: sonido y parpadeo en la barra de tareas (como mucho uno cada 2 s).
        private void AlertPrivate()
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastAlertAt) < 2000) return;
            Interlocked.Exchange(ref _lastAlertAt, now);
            try
            {
                BeginInvoke(() =>
                {
                    System.Media.SystemSounds.Asterisk.Play();
                    if (ActiveForm == this) return;
                    var info = new NativeMethods.FLASHWINFO
                    {
                        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
                        hwnd = Handle,
                        dwFlags = NativeMethods.FLASHW_ALL | NativeMethods.FLASHW_TIMERNOFG,
                        uCount = 0,
                        dwTimeout = 0
                    };
                    NativeMethods.FlashWindowEx(ref info);
                });
            }
            catch (InvalidOperationException) { /* ventana aún sin crear o cerrándose */ }
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

        private void FallbackSwitch_CheckedChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            _settings.GoogleDeRespaldo = _fallbackSwitch.Checked;
            _translator.GoogleFallback = _fallbackSwitch.Checked;
            UpdateFallbackOrder();
            UpdateStatus();
            ScheduleSave();
        }

        private void HotkeySwitch_CheckedChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            _settings.TeclaRapida = _hotkeySwitch.Checked;
            if (_settings.TeclaRapida) RegisterHotkey();
            else UnregisterHotkey();
            ScheduleSave();
        }

        private void AlertSwitch_CheckedChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            _settings.AvisarPrivados = _alertSwitch.Checked;
            ScheduleSave();
        }

        private void CompactSwitch_CheckedChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            _settings.Compacto = _compactSwitch.Checked;
            ApplyCompact(_settings.Compacto);
            ScheduleSave();
        }

        private void OpacitySlider_ValueChanged(object? sender, EventArgs e)
        {
            int percent = _opacitySlider.Value * 10;
            _opacityValue.Text = $"{percent} %";
            if (_suppressEvents) return;
            _settings.OpacidadInactiva = percent;
            ScheduleSave();

            // Vista previa un momento, para ver cómo quedará.
            Opacity = percent / 100.0;
            _opacityPreviewTimer.Stop();
            _opacityPreviewTimer.Start();
        }

        private void ApplyGlossary()
        {
            _glossaryTimer.Stop();
            var terms = ProtectedTerms.ParseList(_glossaryBox.Input.Text);
            if (terms.SequenceEqual(_settings.NoTraducir, StringComparer.OrdinalIgnoreCase)) return;
            _settings.NoTraducir = terms;
            _translator.SetProtectedTerms(terms);
            ScheduleSave();
        }

        private void ProviderSeg_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var p = SelectedProvider;
            _settings.Proveedor = p.ToString();
            _translator.Preferred = p;
            ShowProviderRows(p);
            if (p != Proveedor.Google && _translator.GetKey(p) == null) _keyBox.Input.Focus();
            ScheduleSave();
            UpdateStatus();
        }

        private void KeyField_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (_checkBtn.Enabled) CheckKey_Click(_checkBtn, EventArgs.Empty);
        }

        private async void CheckKey_Click(object? sender, EventArgs e)
        {
            var p = SelectedProvider;
            if (p == Proveedor.Google) return;
            var key = _keyBox.Input.Text.Trim();
            var region = _regionBox.Input.Text.Trim();
            if (key.Length == 0)
            {
                SetKeyStatus("Pega la clave y pulsa Comprobar.", Tone.Warn);
                return;
            }

            _checkBtn.Enabled = false;
            _checkBtn.Text = "Comprobando…";
            SetKeyStatus("Comprobando la clave…", Tone.Dim);
            try
            {
                if (p == Proveedor.DeepL)
                {
                    var usage = await _translator.CheckDeepLKeyAsync(key, _cts.Token);
                    ApplyKey(p, key, null);
                    SetKeyStatus(key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
                        ? "✓ Clave válida, plan gratuito"
                        : "✓ Clave válida", Tone.Ok);
                    ShowUsage(usage);
                }
                else
                {
                    await _translator.CheckMicrosoftKeyAsync(key, region, _cts.Token);
                    ApplyKey(p, key, region);
                    SetKeyStatus("✓ Clave válida. Microsoft Translator listo.", Tone.Ok);
                }
            }
            catch (ProviderUnavailableException ex)
            {
                var hint = p == Proveedor.Microsoft && region.Length == 0
                    ? " Si tu recurso no es global, escribe su región (por ejemplo westeurope)."
                    : "";
                SetKeyStatus($"{TranslationService.Name(p)} rechazó la clave: {ex.Message}.{hint}", Tone.Warn);
            }
            catch (OperationCanceledException)
            {
                return; // cerrando la ventana
            }
            catch (Exception ex)
            {
                // Sin conexión para comprobarla: se guarda igualmente.
                ApplyKey(p, key, region);
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
            catch (ProviderUnavailableException ex)
            {
                SetKeyStatus("DeepL no acepta la clave guardada: " + ex.Message + ".", Tone.Warn);
            }
            catch
            {
                // sin conexión al arrancar: se verá al pulsar Comprobar
            }
        }

        private void ApplyKey(Proveedor p, string key, string? region)
        {
            if (p == Proveedor.DeepL)
            {
                _settings.DeepLKey = key;
            }
            else
            {
                _settings.MicrosoftKey = key;
                _settings.MicrosoftRegion = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
            }
            _settings.Proveedor = p.ToString();
            _translator.SetKey(p, key, region);
            _translator.Preferred = p;
            _settings.Save();
            UpdateFallbackOrder();
        }

        private void SetKeyStatus(string text, Tone tone)
        {
            _keyStatus.Text = text;
            _keyStatus.Tone = tone;
        }

        // Microsoft no informa del consumo con la clave: se cuenta lo que envía esta app este mes.
        private void ShowMicrosoftUsage()
        {
            long used = _settings.MicrosoftUsageThisMonth();
            _usageBar.AccessibleName = "Caracteres enviados a Microsoft este mes";
            _usageBar.Value = (double)used / MicrosoftFreeChars;
            _usageLabel.Text = $"≈ {FormatCount(used)} de {FormatCount(MicrosoftFreeChars)} gratis (contados por esta app)";
            _usageLabel.Tone = used >= MicrosoftFreeChars * 0.9 ? Tone.Warn : Tone.Text;
        }

        private void ShowUsage(DeepLUsage usage)
        {
            if (IsDisposed) return;
            _deeplUsage = usage;
            _usageBar.AccessibleName = "Caracteres de DeepL usados este mes";
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
