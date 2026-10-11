using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FontAwesome.Sharp;
using Guna.UI2.WinForms;

namespace TraductorNWN
{
    internal enum ChatTarget { Ambos, Ingles, Espanol }

    public sealed class MainForm : Form
    {
        // ---------- Paleta: negro + naranja (cada tono con un único rol) ----------
        private static readonly Color BgApp = ColorTranslator.FromHtml("#0a0a0a");       // fondo de la ventana
        private static readonly Color BgSurface = ColorTranslator.FromHtml("#151515");   // tarjetas/paneles
        private static readonly Color BgSurfaceHover = ColorTranslator.FromHtml("#1f1f1f");
        private static readonly Color BgField = ColorTranslator.FromHtml("#1c1c1c");     // campos editables
        private static readonly Color BgFieldHover = ColorTranslator.FromHtml("#242424");
        private static readonly Color BgChat = ColorTranslator.FromHtml("#000000");      // lectura del chat
        private static readonly Color Coral = ColorTranslator.FromHtml("#ff8a3d");       // acento principal
        private static readonly Color CoralHover = ColorTranslator.FromHtml("#ffa15c");
        private static readonly Color CoralDark = ColorTranslator.FromHtml("#1a0e05");   // texto sobre fondo naranja
        private static readonly Color Teal = ColorTranslator.FromHtml("#d97a2e");        // acento del estado DeepL / nombres
        private static readonly Color Warn = ColorTranslator.FromHtml("#e5534b");        // API limitando / errores
        private static readonly Color TextMain = ColorTranslator.FromHtml("#f5f0ea");
        private static readonly Color TextDim = ColorTranslator.FromHtml("#8a8580");
        private static readonly Color BorderSoft = ColorTranslator.FromHtml("#262626");

        private const int RadiusCard = 10;
        private const int RadiusControl = 8;
        private const int Gap = 14;

        // ---------- Comportamiento ----------
        private const int MaxMensajesPorPanel = 200;

        private static readonly Regex EmoteRegex = new(@"\*([^*]+)\*", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> NombresCanal = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Shout"] = "Grito",
            ["Whisper"] = "Susurro",
            ["Tell"] = "Privado",
            ["Party"] = "Grupo",
            ["DM"] = "DM",
            ["Server"] = "Servidor"
        };

        private static readonly Lazy<Font> MonoFont = new(CreateMonoFont);

        // ---------- Controles ----------
        private readonly Panel _header;
        private readonly Guna2Panel _statusBadge;
        private readonly Label _statusDot;
        private readonly Label _titleLabel;
        private readonly RichTextBox _chatBoxAmbos;
        private readonly RichTextBox _chatBoxIngles;
        private readonly RichTextBox _chatBoxEspanol;
        private readonly ChatView _viewAmbos;
        private readonly ChatView _viewIngles;
        private readonly ChatView _viewEspanol;
        private readonly Guna2TextBox _inputBox;
        private readonly Guna2TextBox _deeplKeyBox;
        private readonly Guna2Button _deeplBtn;
        private readonly IconButton _mayusBtn;
        private readonly Guna2Panel _mayusBtnFrame;
        private readonly IconButton _filtroBtn;
        private readonly Guna2Panel _filtroBtnFrame;
        private readonly ToolTip _toolTip = new();
        private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 60 };

        // ---------- Servicios ----------
        private readonly AppSettings _settings = AppSettings.Load();
        private readonly TranslationService _translator = new();
        private readonly ChatParser _parser = new();
        private readonly LogWatcher _watcher;
        private readonly IncomingPipeline _incoming;
        private readonly CancellationTokenSource _cts = new();

        // Mensajes que escribe el usuario (se traducen y envían de uno en uno, en orden).
        private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // Lo que hay que pintar: los hilos de fondo lo dejan aquí y un timer de la UI
        // lo vuelca por lotes (nada de Invoke síncronos por cada fragmento de texto).
        private readonly ConcurrentQueue<DisplayItem> _display = new();

        private int _badgeTicks;
        private volatile bool _mayusculasActivas;
        private volatile bool _filtrarSistema;

        private readonly record struct DisplayItem(ChatTarget Target, List<Run> Runs);

        public MainForm()
        {
            Text = "Traductor · Neverwinter Nights";
            Width = 720;
            Height = 760;
            MinimumSize = new Size(560, 560);
            BackColor = BgApp;
            TopMost = true;
            Font = new Font("Segoe UI", 9.5f);
            Padding = new Padding(16);

            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { /* si falla, la ventana usa el icono genérico de WinForms */ }

            // ---------- Cabecera ----------
            _header = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = BgApp };

            var brandMark = new Panel
            {
                BackColor = Coral,
                Size = new Size(4, 34),
                Location = new Point(0, 6)
            };

            var appName = new Label
            {
                Text = "Traductor de Chat",
                ForeColor = TextMain,
                Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 3)
            };
            var appSubtitle = new Label
            {
                Text = "NEVERWINTER NIGHTS  ·  EN ⇄ ES",
                ForeColor = TextDim,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(17, 32)
            };

            // Badge de estado: proveedor activo, pausas por límite de la API y mensajes en cola.
            _statusBadge = new Guna2Panel
            {
                FillColor = BgSurface,
                BorderColor = BorderSoft,
                BorderThickness = 1,
                BorderRadius = RadiusControl,
                Size = new Size(168, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            // BackColor explícito: si no, heredan el BackColor (negro) del Guna2Panel y no su FillColor.
            _statusDot = new Label
            {
                Text = "●",
                ForeColor = Coral,
                BackColor = BgSurface,
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Margin = Padding.Empty
            };
            _titleLabel = new Label
            {
                Text = "Google Translate",
                ForeColor = TextMain,
                BackColor = BgSurface,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                AutoSize = true,
                Margin = Padding.Empty
            };
            _statusBadge.Controls.Add(_statusDot);
            _statusBadge.Controls.Add(_titleLabel);

            _header.Controls.Add(brandMark);
            _header.Controls.Add(appName);
            _header.Controls.Add(appSubtitle);
            _header.Controls.Add(_statusBadge);
            _header.Resize += (s, e) => PositionStatusBadge();
            LayoutStatusBadge();

            var headerDivider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = BorderSoft, Margin = new Padding(0) };

            // ---------- Chat (pestañas: Ambos / Inglés / Español) ----------
            var chatContainer = new Panel { Dock = DockStyle.Fill, BackColor = BgApp, Padding = new Padding(0, 4, 0, Gap) };

            var tabs = new Guna2TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                TabButtonSize = new Size(140, 38),
                TabMenuOrientation = TabMenuOrientation.HorizontalTop,
                TabMenuBackColor = BgSurface
            };
            tabs.TabButtonIdleState.FillColor = BgSurface;
            tabs.TabButtonIdleState.InnerColor = BgSurface;
            tabs.TabButtonIdleState.ForeColor = TextDim;
            tabs.TabButtonIdleState.BorderColor = BorderSoft;
            tabs.TabButtonHoverState.FillColor = BgSurfaceHover;
            tabs.TabButtonHoverState.InnerColor = BgSurfaceHover;
            tabs.TabButtonHoverState.ForeColor = TextMain;
            tabs.TabButtonHoverState.BorderColor = BorderSoft;
            tabs.TabButtonSelectedState.FillColor = BgChat;
            tabs.TabButtonSelectedState.InnerColor = BgChat;
            tabs.TabButtonSelectedState.ForeColor = Coral;
            tabs.TabButtonSelectedState.BorderColor = Coral;

            _chatBoxAmbos = CreateChatBox();
            _chatBoxIngles = CreateChatBox();
            _chatBoxEspanol = CreateChatBox();
            _viewAmbos = new ChatView(_chatBoxAmbos, MaxMensajesPorPanel);
            _viewIngles = new ChatView(_chatBoxIngles, MaxMensajesPorPanel);
            _viewEspanol = new ChatView(_chatBoxEspanol, MaxMensajesPorPanel);

            tabs.TabPages.Add(CreateChatTab("Ambos", _chatBoxAmbos));
            tabs.TabPages.Add(CreateChatTab("Inglés", _chatBoxIngles));
            tabs.TabPages.Add(CreateChatTab("Español", _chatBoxEspanol));

            var tabsBorder = new Guna2Panel
            {
                Dock = DockStyle.Fill,
                FillColor = BgChat,
                BorderColor = BorderSoft,
                BorderThickness = 1,
                BorderRadius = 0,
                Padding = new Padding(2)
            };
            tabsBorder.Controls.Add(tabs);
            chatContainer.Controls.Add(tabsBorder);

            // ---------- Entrada de texto + interruptores ----------
            var inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 78, BackColor = BgApp, Padding = new Padding(0, 22, 0, Gap) };
            var inputRow = new Panel { Dock = DockStyle.Fill };

            var mayus = CreateToggle(IconChar.TextHeight, "Enviar los mensajes en MAYÚSCULAS");
            _mayusBtn = mayus.Button;
            _mayusBtnFrame = mayus.Frame;
            _mayusBtn.Click += MayusBtn_Click;

            var filtro = CreateToggle(IconChar.Filter,
                "Solo traducir el chat de jugadores.\n" +
                "El combate y los mensajes del sistema se muestran sin traducir en la pestaña Inglés,\n" +
                "así no se gastan peticiones de la API ni te bloquean en peleas largas.");
            _filtroBtn = filtro.Button;
            _filtroBtnFrame = filtro.Frame;
            _filtroBtn.Click += FiltroBtn_Click;

            _inputBox = new Guna2TextBox
            {
                Dock = DockStyle.Fill,
                FillColor = BgField,
                BorderColor = BorderSoft,
                BorderThickness = 1,
                BorderRadius = RadiusControl,
                ForeColor = TextMain,
                Font = new Font("Segoe UI", 10.5f),
                PlaceholderText = "Escribe en español y pulsa Enter para enviarlo al juego…"
            };
            _inputBox.FocusedState.BorderColor = Coral;
            _inputBox.HoverState.BorderColor = Coral;
            _inputBox.KeyDown += InputBox_KeyDown;

            var inputBoxHost = new Panel { Dock = DockStyle.Fill };
            inputBoxHost.Controls.Add(_inputBox);

            // Orden de alta = orden de docking: el Fill primero, y de los Right el último queda más a la derecha.
            inputRow.Controls.Add(inputBoxHost);
            inputRow.Controls.Add(mayus.Host);
            inputRow.Controls.Add(filtro.Host);
            inputPanel.Controls.Add(inputRow);

            // ---------- Panel DeepL ----------
            var configPanel = new Panel { Dock = DockStyle.Bottom, Height = 74, BackColor = BgApp, Padding = new Padding(0, 0, 0, Gap) };
            var deeplCard = new Guna2Panel
            {
                Dock = DockStyle.Fill,
                FillColor = BgSurface,
                BorderColor = BorderSoft,
                BorderThickness = 1,
                BorderRadius = RadiusCard,
                Padding = new Padding(14, 10, 14, 10)
            };

            var deeplLabel = new Label
            {
                Text = "DeepL",
                Dock = DockStyle.Left,
                Width = 58,
                ForeColor = TextMain,
                BackColor = BgSurface,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _deeplBtn = new Guna2Button
            {
                Text = "CONECTAR",
                Dock = DockStyle.Right,
                Width = 108,
                FillColor = Coral,
                ForeColor = CoralDark,
                BorderRadius = RadiusControl,
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Animated = true
            };
            _deeplBtn.HoverState.FillColor = CoralHover;
            _deeplBtn.Click += DeeplBtn_Click;
            _toolTip.SetToolTip(_deeplBtn, "Comprueba y guarda la clave. Con el campo vacío vuelve a Google Translate.");

            var keyFieldHost = new Panel { Dock = DockStyle.Fill, BackColor = BgSurface, Padding = new Padding(10, 0, 10, 0) };
            _deeplKeyBox = new Guna2TextBox
            {
                Dock = DockStyle.Fill,
                FillColor = BgField,
                BorderColor = BorderSoft,
                BorderThickness = 1,
                BorderRadius = RadiusControl,
                ForeColor = TextMain,
                PasswordChar = '•',
                Font = new Font("Segoe UI", 9.5f),
                PlaceholderText = "Clave de API de DeepL (opcional)"
            };
            _deeplKeyBox.FocusedState.BorderColor = Coral;
            _deeplKeyBox.HoverState.BorderColor = Coral;
            _deeplKeyBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                if (_deeplBtn.Enabled) DeeplBtn_Click(_deeplBtn, EventArgs.Empty);
            };
            keyFieldHost.Controls.Add(_deeplKeyBox);

            deeplCard.Controls.Add(keyFieldHost);
            deeplCard.Controls.Add(deeplLabel);
            deeplCard.Controls.Add(_deeplBtn);
            configPanel.Controls.Add(deeplCard);

            Controls.Add(chatContainer);
            Controls.Add(inputPanel);
            Controls.Add(configPanel);
            Controls.Add(headerDivider);
            Controls.Add(_header);

            // ---------- Servicios y estado guardado ----------
            _watcher = new LogWatcher(OnLogLines, (msg, important) => Notify(msg, important ? Teal : TextDim));
            _incoming = new IncomingPipeline(_translator, ShowTranslated, ShowUntranslated,
                (msg, warn) => Notify(msg, warn ? Warn : Teal));
            _translator.Notice += msg => Notify(msg, Warn);

            _mayusculasActivas = _settings.Mayusculas;
            _filtrarSistema = _settings.FiltrarSistema;
            PaintToggle(_mayusBtnFrame, _mayusBtn, _mayusculasActivas);
            PaintToggle(_filtroBtnFrame, _filtroBtn, _filtrarSistema);

            if (!string.IsNullOrEmpty(_settings.DeepLKey))
            {
                _deeplKeyBox.Text = _settings.DeepLKey;
                _translator.DeepLKey = _settings.DeepLKey;
            }

            RestoreWindowBounds();
            UpdateBadge();

            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
        }

        // ======================================================================
        //  Construcción de la UI
        // ======================================================================

        private static Font CreateMonoFont()
        {
            // Cascadia Code si está instalada (Windows Terminal / VS); si no, Consolas.
            try
            {
                using var installed = new System.Drawing.Text.InstalledFontCollection();
                bool hasCascadia = installed.Families.Any(f => f.Name.Equals("Cascadia Code", StringComparison.OrdinalIgnoreCase));
                return hasCascadia ? new Font("Cascadia Code", 9.5f) : new Font("Consolas", 9.5f);
            }
            catch
            {
                return new Font("Consolas", 9.5f);
            }
        }

        private static RichTextBox CreateChatBox() => new()
        {
            Dock = DockStyle.Fill,
            BackColor = BgChat,
            ForeColor = TextMain,
            Font = MonoFont.Value,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            HideSelection = false
        };

        private static TabPage CreateChatTab(string title, RichTextBox box)
        {
            var page = new TabPage(title) { BackColor = BgChat, Padding = new Padding(0) };
            var pad = new Panel { Dock = DockStyle.Fill, BackColor = BgChat, Padding = new Padding(1, 8, 1, 1) };
            pad.Controls.Add(box);
            page.Controls.Add(pad);
            return page;
        }

        private (Panel Host, Guna2Panel Frame, IconButton Button) CreateToggle(IconChar icon, string tooltip)
        {
            var host = new Panel { Dock = DockStyle.Right, Width = 50, Padding = new Padding(Gap, 0, 0, 0) };
            var frame = new Guna2Panel
            {
                Dock = DockStyle.Fill,
                FillColor = BgField,
                BorderColor = BorderSoft,
                BorderThickness = 1,
                BorderRadius = RadiusControl,
                Padding = new Padding(4)
            };
            var button = new IconButton
            {
                IconChar = icon,
                IconFont = IconFont.Solid,
                IconColor = TextDim,
                IconSize = 20,
                Dock = DockStyle.Fill,
                BackColor = BgField,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Text = ""
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = BgFieldHover;
            _toolTip.SetToolTip(button, tooltip);

            frame.Controls.Add(button);
            host.Controls.Add(frame);
            return (host, frame, button);
        }

        private static void PaintToggle(Guna2Panel frame, IconButton button, bool active)
        {
            var bg = active ? Coral : BgField;
            button.BackColor = bg;
            button.IconColor = active ? CoralDark : TextDim;
            button.FlatAppearance.MouseOverBackColor = active ? CoralHover : BgFieldHover;
            frame.FillColor = bg;
            frame.BorderColor = active ? Coral : BorderSoft;
        }

        // Centra punto y texto dentro del badge y ajusta su ancho al texto actual.
        private void LayoutStatusBadge()
        {
            const int leftPad = 16;
            const int gap = 8;
            const int rightPad = 16;
            _statusDot.Location = new Point(leftPad, (_statusBadge.Height - _statusDot.Height) / 2);
            _titleLabel.Location = new Point(_statusDot.Right + gap, (_statusBadge.Height - _titleLabel.Height) / 2);

            int width = Math.Max(120, _titleLabel.Left + _titleLabel.PreferredWidth + rightPad);
            if (_statusBadge.Width != width) _statusBadge.Width = width;
            PositionStatusBadge();
        }

        private void PositionStatusBadge() =>
            _statusBadge.Location = new Point(_header.Width - _statusBadge.Width - 4, 13);

        private void UpdateBadge()
        {
            bool deepl = _translator.ActiveProvider == Proveedor.DeepL;
            string name = deepl ? "DeepL" : "Google";
            var cooldown = _translator.CooldownRemaining;
            int pending = _incoming.Pending;

            string text;
            Color dot;
            if (cooldown.TotalSeconds >= 1)
            {
                text = $"{name} · pausa {Math.Ceiling(cooldown.TotalSeconds)} s";
                dot = Warn;
            }
            else if (pending >= 5)
            {
                text = $"{name} · cola {pending}";
                dot = deepl ? Teal : Coral;
            }
            else
            {
                text = deepl ? "DeepL" : "Google Translate";
                dot = deepl ? Teal : Coral;
            }

            if (_titleLabel.Text != text)
            {
                _titleLabel.Text = text;
                LayoutStatusBadge();
            }
            if (_statusDot.ForeColor != dot) _statusDot.ForeColor = dot;
        }

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

        // ======================================================================
        //  Ciclo de vida
        // ======================================================================

        private void MainForm_Load(object? sender, EventArgs e)
        {
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            var ct = _cts.Token;
            RunBackground(() => _watcher.RunAsync(ct), "lector del log");
            RunBackground(() => _incoming.RunAsync(ct), "traductor");
            RunBackground(() => OutgoingWorkerAsync(ct), "envío al juego");
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cts.Cancel();
            _uiTimer.Stop();
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
                _toolTip.Dispose();
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
                    Notify($"Error interno en el {nombre}: {ex.Message}", Warn);
                }
            });
        }

        // Vuelca a los paneles todo lo pendiente de pintar, agrupado por panel.
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            if (!_display.IsEmpty)
            {
                List<List<Run>>? ambos = null, ingles = null, espanol = null;
                int count = 0;
                while (count < 500 && _display.TryDequeue(out var item))
                {
                    count++;
                    switch (item.Target)
                    {
                        case ChatTarget.Ingles:
                            (ingles ??= new List<List<Run>>()).Add(item.Runs);
                            break;
                        case ChatTarget.Espanol:
                            (espanol ??= new List<List<Run>>()).Add(item.Runs);
                            break;
                        default:
                            (ambos ??= new List<List<Run>>()).Add(item.Runs);
                            break;
                    }
                }
                if (ambos != null) _viewAmbos.AppendMessages(ambos);
                if (ingles != null) _viewIngles.AppendMessages(ingles);
                if (espanol != null) _viewEspanol.AppendMessages(espanol);
            }

            if (++_badgeTicks >= 8)
            {
                _badgeTicks = 0;
                UpdateBadge();
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
                ParsedLine? line;
                try { line = _parser.Parse(raw); }
                catch { continue; }
                if (line == null) continue;

                bool translate = line.Kind == LineKind.Chat || !_filtrarSistema;

                // El panel Inglés no espera a nadie: se pinta al instante.
                Enqueue(ChatTarget.Ingles, BuildOriginal(line, dim: !translate));

                if (translate) _incoming.Enqueue(line);
            }
        }

        // ======================================================================
        //  Salida: lo que escribe el usuario → inglés → juego
        // ======================================================================

        private void InputBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;

            var textEs = _inputBox.Text.Trim();
            if (textEs.Length == 0) return;
            _inputBox.Clear();

            // No se traduce aquí: la UI nunca se congela esperando a la red.
            _outgoing.Writer.TryWrite(textEs);
        }

        private async Task OutgoingWorkerAsync(CancellationToken ct)
        {
            await foreach (var textEs in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                string textEn;
                try
                {
                    textEn = await _translator.TranslateOneWithRetryAsync(textEs, "es", "en", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Notify($"No se pudo traducir tu mensaje «{textEs}»: {ex.Message}", Warn);
                    continue;
                }

                if (_mayusculasActivas) textEn = textEn.ToUpperInvariant();

                // Cuando el juego escriba nuestro propio mensaje en el log, se mostrará el
                // texto original en español sin gastar otra petición.
                _translator.Remember(textEn, textEs, "en", "es");

                var runs = new List<Run> { new("→ Enviando: " + textEn, Coral) };
                if (!string.Equals(textEn.Trim(), textEs, StringComparison.OrdinalIgnoreCase))
                    runs.Add(new Run("\n  ES  " + textEs, TextDim));
                Enqueue(ChatTarget.Ambos, runs);

                // Margen antes de escribir en el juego (configurable en config.json).
                await Task.Delay(_settings.RetardoEnvioMs, ct).ConfigureAwait(false);

                string? error;
                try { error = GameSender.Send(textEn); }
                catch (Exception ex) { error = "Error al enviar al juego: " + ex.Message; }
                if (error != null) Notify(error, Warn);
            }
        }

        // ======================================================================
        //  Botones
        // ======================================================================

        private void MayusBtn_Click(object? sender, EventArgs e)
        {
            _mayusculasActivas = !_mayusculasActivas;
            PaintToggle(_mayusBtnFrame, _mayusBtn, _mayusculasActivas);
            _settings.Mayusculas = _mayusculasActivas;
            _settings.Save();
        }

        private void FiltroBtn_Click(object? sender, EventArgs e)
        {
            _filtrarSistema = !_filtrarSistema;
            PaintToggle(_filtroBtnFrame, _filtroBtn, _filtrarSistema);
            _settings.FiltrarSistema = _filtrarSistema;
            _settings.Save();
            Notify(_filtrarSistema
                ? "Solo se traduce el chat de jugadores (combate y sistema, sin traducir, en la pestaña Inglés)."
                : "Se traducen también los mensajes de combate y del sistema.", TextDim);
        }

        private async void DeeplBtn_Click(object? sender, EventArgs e)
        {
            var key = _deeplKeyBox.Text.Trim();
            if (key.Length == 0)
            {
                _translator.DeepLKey = null;
                _settings.DeepLKey = null;
                _settings.Save();
                Notify("Volviendo a Google Translate.", TextDim);
                UpdateBadge();
                return;
            }

            var oldText = _deeplBtn.Text;
            _deeplBtn.Enabled = false;
            _deeplBtn.Text = "PROBANDO…";
            try
            {
                var usage = await _translator.CheckDeepLKeyAsync(key, _cts.Token);
                ApplyDeepLKey(key);
                Notify(usage.Limit > 0
                    ? $"DeepL conectado · {usage.Count:N0} de {usage.Limit:N0} caracteres usados este mes."
                    : "DeepL conectado.", Teal);
            }
            catch (DeepLUnavailableException ex)
            {
                Notify("DeepL rechazó la clave: " + ex.Message + ". Se sigue usando el proveedor anterior.", Warn);
            }
            catch (OperationCanceledException)
            {
                return; // cerrando la ventana
            }
            catch (Exception ex)
            {
                // Sin conexión para comprobarla: se guarda igualmente.
                ApplyDeepLKey(key);
                Notify("No se pudo comprobar la clave (" + ex.Message + "). Se usará igualmente.", TextDim);
            }
            finally
            {
                if (!IsDisposed)
                {
                    _deeplBtn.Text = oldText;
                    _deeplBtn.Enabled = true;
                    UpdateBadge();
                }
            }
        }

        private void ApplyDeepLKey(string key)
        {
            _translator.DeepLKey = key;
            _settings.DeepLKey = key;
            _settings.Save();
        }

        // ======================================================================
        //  Construcción de mensajes para los paneles
        // ======================================================================

        private void Enqueue(ChatTarget target, List<Run> runs) => _display.Enqueue(new DisplayItem(target, runs));

        private void Notify(string text, Color color) =>
            Enqueue(ChatTarget.Ambos, new List<Run> { new("[Sistema]: " + text, color) });

        private static List<Run> BuildOriginal(ParsedLine line, bool dim)
        {
            var runs = new List<Run>(6);
            if (line.Kind == LineKind.Chat)
            {
                AddSpeaker(runs, line);
                AddMessageParts(runs, line.Text, TextMain);
            }
            else
            {
                AddMessageParts(runs, line.Text, dim ? TextDim : TextMain);
            }
            return runs;
        }

        private void ShowTranslated(ParsedLine line, string translated)
        {
            bool same = string.Equals(translated.Trim(), line.Text.Trim(), StringComparison.OrdinalIgnoreCase);

            if (line.Kind == LineKind.Chat)
            {
                // Ambos: nombre + traducción, y debajo el original (si cambia algo).
                var ambos = new List<Run>(8);
                AddSpeaker(ambos, line);
                AddMessageParts(ambos, translated, TextMain);
                if (!same) ambos.Add(new Run("\n  EN  " + line.Text, TextDim));
                Enqueue(ChatTarget.Ambos, ambos);

                var es = new List<Run>(6);
                AddSpeaker(es, line);
                AddMessageParts(es, translated, TextMain);
                Enqueue(ChatTarget.Espanol, es);
            }
            else
            {
                var ambos = new List<Run>(6);
                AddMessageParts(ambos, translated, TextMain);
                if (!same) ambos.Add(new Run("\n(" + line.Text + ")", TextDim));
                Enqueue(ChatTarget.Ambos, ambos);

                var es = new List<Run>(4);
                AddMessageParts(es, translated, TextMain);
                Enqueue(ChatTarget.Espanol, es);
            }
        }

        private void ShowUntranslated(ParsedLine line, string motivo)
        {
            bool chat = line.Kind == LineKind.Chat;

            var ambos = new List<Run>(8);
            if (chat) AddSpeaker(ambos, line);
            AddMessageParts(ambos, line.Text, chat ? TextMain : TextDim);
            ambos.Add(new Run("\n  ⚠ sin traducir · " + motivo, TextDim));
            Enqueue(ChatTarget.Ambos, ambos);

            var es = new List<Run>(6);
            if (chat) AddSpeaker(es, line);
            AddMessageParts(es, line.Text, TextDim);
            Enqueue(ChatTarget.Espanol, es);
        }

        // "14:35  [Privado] Nombre:  "
        private static void AddSpeaker(List<Run> runs, ParsedLine line)
        {
            if (!string.IsNullOrEmpty(line.Time))
                runs.Add(new Run(line.Time + "  ", TextDim));

            if (!string.IsNullOrEmpty(line.Channel) && !line.Channel.Equals("Talk", StringComparison.OrdinalIgnoreCase))
            {
                var canal = NombresCanal.TryGetValue(line.Channel, out var es) ? es : line.Channel;
                runs.Add(new Run("[" + canal + "] ", Coral, FontStyle.Bold));
            }

            runs.Add(new Run((line.Speaker ?? "?") + ":  ", Teal, FontStyle.Bold));
        }

        // Separa el mensaje en texto normal y *acciones/emotes*, cada uno con su estilo.
        private static void AddMessageParts(List<Run> runs, string message, Color baseColor)
        {
            int last = 0;
            foreach (Match m in EmoteRegex.Matches(message))
            {
                if (m.Index > last)
                    runs.Add(new Run(message.Substring(last, m.Index - last), baseColor));
                runs.Add(new Run("✦ " + m.Groups[1].Value.Trim() + " ✦", Coral, FontStyle.Bold | FontStyle.Italic));
                last = m.Index + m.Length;
            }
            if (last < message.Length)
                runs.Add(new Run(message.Substring(last), baseColor));
        }
    }
}
