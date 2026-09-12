using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Guna.UI2.WinForms;
using FontAwesome.Sharp;

namespace TraductorNWN
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private readonly RichTextBox _chatBoxAmbos;
        private readonly RichTextBox _chatBoxIngles;
        private readonly RichTextBox _chatBoxEspanol;
        private readonly Guna2TextBox _inputBox;
        private readonly Label _titleLabel;
        private readonly Guna2TextBox _deeplKeyBox;
        private readonly Guna2Button _deeplBtn;
        private readonly IconButton _mayusBtn;
        private Guna2Panel _mayusBtnFrame = null!;

        private readonly HttpClient _http = new HttpClient();
        private volatile bool _running = true;
        private string _mode = "Google";
        private string? _deeplKey;
        private bool _mayusculasActivas = false;

        // Retraso extra (ms) añadido antes de enviar el texto traducido al juego,
        // para dar margen a quien escribe a corregirse o simplemente ir más despacio.
        private const int RetardoEnvioExtraMs = 2000;

        private static readonly string ConfigFile = Path.Combine(AppContext.BaseDirectory, "config_traductor.txt");
        // Formato típico de línea de NWN: [código] Nombre del hablante: [Talk] mensaje...
        // Nos interesa solo el nombre; descartamos el código entre corchetes y la etiqueta [Talk]/[Shout]/etc.
        private static readonly Regex SpeakerRegex = new(@"^\[.*?\]\s*(.*?):\s*\[.*?\](.*)$", RegexOptions.Compiled);

        // Etiquetas de color del cliente de NWN, tipo <cÌ®þ>texto</c> — no son legibles, hay que quitarlas.
        private static readonly Regex ColorTagRegex = new(@"</?c[^>]*>", RegexOptions.Compiled);

        // Texto de acción/emote escrito entre asteriscos, ej: *kneels down to pet the dog*
        private static readonly Regex EmoteRegex = new(@"\*([^*]+)\*", RegexOptions.Compiled);

        // Imports para localizar la ventana del juego (por coincidencia parcial de título,
        // ya que el título exacto puede variar según versión/idioma) y darle el foco.
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // Busca cualquier ventana visible cuyo título contenga alguna de las palabras clave
        // (insensible a mayúsculas), en vez de exigir una coincidencia exacta.
        private static IntPtr FindGameWindow(params string[] keywords)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var sb = new System.Text.StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                foreach (var kw in keywords)
                {
                    if (title.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false; // dejar de enumerar, ya la encontramos
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Paleta: negro + naranja. Pocos tonos, cada uno con un único rol claro —
        // antes había varios negros casi idénticos (#141414, #1a1a1a, #0f0f0f) usados
        // sin criterio, lo que hacía que la app se sintiera "parcheada". Ahora solo hay
        // cuatro fondos, cada uno con un propósito fijo:
        private static readonly Color BgApp = ColorTranslator.FromHtml("#0a0a0a");       // fondo de la ventana
        private static readonly Color BgSurface = ColorTranslator.FromHtml("#151515");   // tarjetas/paneles
        private static readonly Color BgSurfaceHover = ColorTranslator.FromHtml("#1f1f1f");
        private static readonly Color BgField = ColorTranslator.FromHtml("#1c1c1c");     // campos editables
        private static readonly Color BgFieldHover = ColorTranslator.FromHtml("#242424");
        private static readonly Color BgChat = ColorTranslator.FromHtml("#000000");      // lectura del chat
        private static readonly Color Coral = ColorTranslator.FromHtml("#ff8a3d");       // acento principal
        private static readonly Color CoralHover = ColorTranslator.FromHtml("#ffa15c");
        private static readonly Color CoralDark = ColorTranslator.FromHtml("#1a0e05");   // texto sobre fondo naranja
        private static readonly Color Teal = ColorTranslator.FromHtml("#d97a2e");        // acento del estado DeepL
        private static readonly Color TextMain = ColorTranslator.FromHtml("#f5f0ea");
        private static readonly Color TextDim = ColorTranslator.FromHtml("#8a8580");
        private static readonly Color BorderSoft = ColorTranslator.FromHtml("#262626");

        // Escala única de radios y espaciados: todas las tarjetas comparten el mismo
        // redondeo, todos los controles interactivos comparten otro. Así el aspecto es
        // consistente en vez de tener esquinas distintas en cada sitio.
        private const int RadiusCard = 10;
        private const int RadiusControl = 8;
        private const int Gap = 14;

        private Label _statusDot = null!;
        private Panel _statusBadge = null!;

        // Centra el punto y el texto verticalmente dentro del badge de estado (cada fuente tiene
        // una altura distinta, así que un Location fijo a mano nunca queda alineado con precisión)
        // y coloca el texto justo a la derecha del punto, con un pequeño espacio entre ambos.
        // Se llama tanto al construir la ventana como cada vez que cambia el texto del badge
        // (el ancho de "Google Translate" y "DeepL" difiere, así que el punto también se recoloca).
        private void LayoutStatusBadge()
        {
            const int leftPad = 16;
            const int gap = 8;
            _statusDot.Location = new Point(leftPad, (_statusBadge.Height - _statusDot.Height) / 2);
            _titleLabel.Location = new Point(_statusDot.Right + gap, (_statusBadge.Height - _titleLabel.Height) / 2);
        }

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

            // El icono del .exe (ApplicationIcon en el .csproj, app.ico) solo controla
            // cómo se ve el archivo en el Explorador. Para que la ventana en ejecución
            // muestre ese mismo icono en su barra de título y en la barra de tareas hay
            // que asignarlo también aquí, explícitamente. Se extrae directamente del
            // propio .exe en marcha, así siempre coincide con el que se compiló.
            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { /* si falla, la ventana usa el icono genérico de WinForms; no es crítico */ }

            // ---------- Cabecera ----------
            var header = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = BgApp };

            // Pequeño acento cuadrado a la izquierda del título, a modo de "marca" minimalista.
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

            // Badge de estado ("pill") en la esquina, tipo indicador moderno.
            var statusBadge = new Guna2Panel
            {
                FillColor = BgSurface,
                BorderColor = BorderSoft,
                BorderThickness = 1,
                BorderRadius = RadiusControl,
                Size = new Size(168, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            // BackColor explícito en los dos: un Label sin BackColor propio hereda por
            // "ambient properties" el BackColor real (no el FillColor) del Guna2Panel que
            // lo contiene, que por defecto es negro — de ahí el recuadro negro que se veía
            // detrás del punto y del texto en vez del gris de la insignia.
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
            statusBadge.Controls.Add(_statusDot);
            statusBadge.Controls.Add(_titleLabel);

            _statusBadge = statusBadge;
            LayoutStatusBadge();
            statusBadge.Resize += (s, e) => LayoutStatusBadge();

            header.Controls.Add(brandMark);
            header.Controls.Add(appName);
            header.Controls.Add(appSubtitle);
            header.Controls.Add(statusBadge);

            void PositionStatusBadge() => statusBadge.Location = new Point(header.Width - statusBadge.Width - 4, 13);
            header.Resize += (s, e) => PositionStatusBadge();
            PositionStatusBadge();

            var headerDivider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = BorderSoft, Margin = new Padding(0) };

            // ---------- Chat (con pestañas: Ambos / Inglés / Español) ----------
            var chatContainer = new Panel { Dock = DockStyle.Fill, BackColor = BgApp, Padding = new Padding(0, 4, 0, Gap) };

            // Guna2TabControl: control de terceros con temario oscuro nativo por estado
            // (idle/hover/selected), así que no hace falta pintar nada a mano ni tocar
            // SetWindowTheme (eso fue justo lo que rompió las pestañas en una versión anterior).
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

            var tabAmbos = new TabPage("Ambos") { BackColor = BgChat, Padding = new Padding(0) };
            var tabIngles = new TabPage("Inglés") { BackColor = BgChat, Padding = new Padding(0) };
            var tabEspanol = new TabPage("Español") { BackColor = BgChat, Padding = new Padding(0) };

            _chatBoxAmbos = CreateChatBox();
            _chatBoxIngles = CreateChatBox();
            _chatBoxEspanol = CreateChatBox();

            // Un pequeño margen interior entre el borde de la pestaña y el texto del chat.
            var padAmbos = new Panel { Dock = DockStyle.Fill, BackColor = BgChat, Padding = new Padding(1, 8, 1, 1) };
            var padIngles = new Panel { Dock = DockStyle.Fill, BackColor = BgChat, Padding = new Padding(1, 8, 1, 1) };
            var padEspanol = new Panel { Dock = DockStyle.Fill, BackColor = BgChat, Padding = new Padding(1, 8, 1, 1) };
            padAmbos.Controls.Add(_chatBoxAmbos);
            padIngles.Controls.Add(_chatBoxIngles);
            padEspanol.Controls.Add(_chatBoxEspanol);

            tabAmbos.Controls.Add(padAmbos);
            tabIngles.Controls.Add(padIngles);
            tabEspanol.Controls.Add(padEspanol);

            tabs.TabPages.Add(tabAmbos);
            tabs.TabPages.Add(tabIngles);
            tabs.TabPages.Add(tabEspanol);

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

            // ---------- Entrada de texto ----------
            var inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 78, BackColor = BgApp, Padding = new Padding(0, 22, 0, Gap) };

            var inputRow = new Panel { Dock = DockStyle.Fill };

            // Botón toggle de MAYÚSCULAS, a la derecha del campo de texto. Opcional: apagado
            // por defecto. Va dentro de un marco redondeado (mismo radio que el campo de
            // texto de al lado) para que los dos elementos de esta fila compartan el mismo
            // lenguaje visual en vez de mezclar un cuadrado con un campo redondeado.
            // Icono de Font Awesome (TextHeight = una "A" grande junto a una "a" pequeña,
            // el icono estándar para "cambiar caso de texto") en vez de solo las letras "AA".
            var mayusHost = new Panel { Dock = DockStyle.Right, Width = 50, Padding = new Padding(Gap, 0, 0, 0) };
            _mayusBtnFrame = new Guna2Panel
            {
                Dock = DockStyle.Fill,
                FillColor = BgField,
                BorderColor = BorderSoft,
                BorderThickness = 1,
                BorderRadius = RadiusControl,
                Padding = new Padding(4)
            };
            _mayusBtn = new IconButton
            {
                IconChar = IconChar.TextHeight,
                IconFont = IconFont.Solid,
                IconColor = TextDim,
                IconSize = 20,
                Dock = DockStyle.Fill,
                BackColor = BgField,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Text = ""
            };
            _mayusBtn.FlatAppearance.BorderSize = 0;
            _mayusBtn.FlatAppearance.MouseOverBackColor = BgFieldHover;
            _mayusBtn.Click += MayusBtn_Click;
            _mayusBtnFrame.Controls.Add(_mayusBtn);
            mayusHost.Controls.Add(_mayusBtnFrame);

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

            inputRow.Controls.Add(inputBoxHost);
            inputRow.Controls.Add(mayusHost);
            inputPanel.Controls.Add(inputRow);

            // ---------- Panel DeepL ----------
            // Una sola tarjeta, una sola fila: etiqueta + campo + botón. Nada de icono
            // decorativo ni segunda línea de texto — cuanto menos elementos, más fácil
            // es que quede ordenado. El mismo radio (RadiusCard/RadiusControl) que el
            // resto de la app, para que se sienta parte del mismo conjunto.
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

            // El propio placeholder explica para qué sirve el campo — así no hace falta
            // una segunda línea de texto explicativo debajo, que era lo que sobrecargaba
            // el panel antes.
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
            keyFieldHost.Controls.Add(_deeplKeyBox);

            deeplCard.Controls.Add(keyFieldHost);
            deeplCard.Controls.Add(deeplLabel);
            deeplCard.Controls.Add(_deeplBtn);
            configPanel.Controls.Add(deeplCard);

            Controls.Add(chatContainer);
            Controls.Add(inputPanel);
            Controls.Add(configPanel);
            Controls.Add(headerDivider);
            Controls.Add(header);

            Load += MainForm_Load;
            FormClosing += (s, e) => _running = false;
        }

        // Dibuja un rectángulo con esquinas redondeadas y borde sutil (look "card" moderno).
        // Usa Cascadia Code si está instalada (viene con Windows Terminal / VS moderno);
        // si no, cae en Consolas, que está en todos los Windows.
        private RichTextBox CreateChatBox()
        {
            return new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = BgChat,
                ForeColor = TextMain,
                Font = GetPreferredMonoFont(),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                DetectUrls = false
            };
        }

        private static Font GetPreferredMonoFont()
        {
            using var installed = new System.Drawing.Text.InstalledFontCollection();
            bool hasCascadia = installed.Families
                .Any(f => f.Name.Equals("Cascadia Code", StringComparison.OrdinalIgnoreCase));
            return hasCascadia ? new Font("Cascadia Code", 9.5f) : new Font("Consolas", 9.5f);
        }


        private void MainForm_Load(object? sender, EventArgs e)
        {
            LoadConfig();
            var t = new Thread(WatchLog) { IsBackground = true };
            t.Start();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var key = File.ReadAllText(ConfigFile).Trim();
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        _deeplKeyBox.Text = key;
                        _deeplKey = key;
                        _mode = "DeepL";
                        _titleLabel.Text = "DeepL";
                        _statusDot.ForeColor = Teal;
                        LayoutStatusBadge();
                    }
                }
            }
            catch { /* ignorar config corrupta */ }
        }

        private void DeeplBtn_Click(object? sender, EventArgs e)
        {
            var key = _deeplKeyBox.Text.Trim();
            if (string.IsNullOrEmpty(key))
            {
                _mode = "Google";
                _deeplKey = null;
                _titleLabel.Text = "Google Translate";
                _statusDot.ForeColor = Coral;
                LayoutStatusBadge();
                if (File.Exists(ConfigFile)) File.Delete(ConfigFile);
                AppendLine("[Sistema]: Volviendo a Google Translate.", TextDim);
                return;
            }

            _deeplKey = key;
            _mode = "DeepL";
            _titleLabel.Text = "DeepL";
            _statusDot.ForeColor = Teal;
            LayoutStatusBadge();
            try { File.WriteAllText(ConfigFile, key); } catch { /* no crítico */ }
            MessageBox.Show(this, "DeepL configurado. Se usará esa clave para traducir.", "Listo",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private string GetLogPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var oneDrivePath = Path.Combine(userProfile, "OneDrive", "Documents", "Neverwinter Nights", "logs", "nwclientlog1.txt");
            if (File.Exists(oneDrivePath)) return oneDrivePath;
            return Path.Combine(userProfile, "Documents", "Neverwinter Nights", "logs", "nwclientlog1.txt");
        }

        private void WatchLog()
        {
            AppendLine("[Sistema]: Buscando el log de Neverwinter Nights...", TextDim);
            string logPath = GetLogPath();

            while (_running && !File.Exists(logPath))
            {
                Thread.Sleep(1000);
                logPath = GetLogPath();
            }
            if (!_running) return;

            AppendLine("[Sistema]: Log detectado. Traduciendo en tiempo real...", Teal);

            try
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(0, SeekOrigin.End);
                long lastLength = stream.Length;

                while (_running)
                {
                    // El log puede haber crecido desde la última lectura; releemos la longitud
                    // real del archivo en disco en vez de fiarnos del buffer del stream.
                    long currentLength;
                    try { currentLength = new FileInfo(logPath).Length; }
                    catch { currentLength = stream.Length; }

                    if (currentLength < lastLength)
                    {
                        // El log se truncó o rotó (nueva sesión de juego): volvemos al principio.
                        stream.Seek(0, SeekOrigin.Begin);
                        lastLength = 0;
                    }

                    if (currentLength == lastLength)
                    {
                        Thread.Sleep(200);
                        continue;
                    }

                    stream.Seek(lastLength, SeekOrigin.Begin);
                    using var partialReader = new StreamReader(stream, System.Text.Encoding.Latin1, false, 4096, leaveOpen: true);
                    string? line;
                    while ((line = partialReader.ReadLine()) != null)
                    {
                        var clean = line.Trim();
                        if (!string.IsNullOrEmpty(clean) &&
                            !clean.Contains("Messages for:") &&
                            !clean.Contains("----"))
                        {
                            ProcessIncomingLine(clean);
                        }
                    }
                    lastLength = stream.Position;
                }
            }
            catch (Exception ex)
            {
                AppendLine($"[Error de lectura de log]: {ex.Message}", TextDim);
            }
        }

        // Quita las etiquetas de color <c...>...</c> que usa el cliente de NWN
        // y que no aportan nada legible en la traducción.
        private static string StripColorTags(string text) => ColorTagRegex.Replace(text, "").Trim();

        private void ProcessIncomingLine(string clean)
        {
            try
            {
                var match = SpeakerRegex.Match(clean);
                string translated;

                if (match.Success)
                {
                    var speaker = match.Groups[1].Value.Trim();
                    var englishMsg = StripColorTags(match.Groups[2].Value);
                    if (string.IsNullOrWhiteSpace(englishMsg)) return;

                    translated = Translate(englishMsg, "en", "es");

                    // Panel "Ambos": nombre del hablante + mensaje en ambos idiomas.
                    AppendSpeakerMessage(speaker, translated, ChatTarget.Ambos, extraLine: "  EN  " + englishMsg, extraColor: TextDim);
                    NotificarMensajeCompleto(ChatTarget.Ambos);

                    // Paneles dedicados: solo el idioma correspondiente.
                    AppendSpeakerMessage(speaker, englishMsg, ChatTarget.Ingles);
                    NotificarMensajeCompleto(ChatTarget.Ingles);

                    AppendSpeakerMessage(speaker, translated, ChatTarget.Espanol);
                    NotificarMensajeCompleto(ChatTarget.Espanol);
                }
                else
                {
                    var cleanMsg = StripColorTags(clean);
                    if (string.IsNullOrWhiteSpace(cleanMsg)) return;

                    translated = Translate(cleanMsg, "en", "es");
                    AppendFormattedMessage(translated, ChatTarget.Ambos);
                    AppendLine("(" + cleanMsg + ")", TextDim, ChatTarget.Ambos);
                    NotificarMensajeCompleto(ChatTarget.Ambos);

                    AppendFormattedMessage(cleanMsg, ChatTarget.Ingles);
                    NotificarMensajeCompleto(ChatTarget.Ingles);

                    AppendFormattedMessage(translated, ChatTarget.Espanol);
                    NotificarMensajeCompleto(ChatTarget.Espanol);
                }
            }
            catch (Exception ex)
            {
                // Mostramos el error (p.ej. fallo de red al traducir) en vez de ocultarlo,
                // y al menos dejamos ver la línea original sin traducir.
                AppendLine(clean, TextDim, ChatTarget.Ambos);
                AppendLine("[Error de traducción]: " + ex.Message, TextDim, ChatTarget.Ambos);
            }
        }

        // Escribe "Nombre: mensaje" (con el mensaje resaltado si es una acción/emote entre asteriscos),
        // y opcionalmente una segunda línea extra debajo (usada en el panel "Ambos" para el otro idioma).
        private void AppendSpeakerMessage(string speaker, string message, ChatTarget target,
            string? extraLine = null, Color? extraColor = null)
        {
            AppendSpeakerAndMessage(speaker, message, target);
            if (extraLine != null)
                AppendLine(extraLine, extraColor ?? TextDim, target);
        }

        // Igual que arriba pero sin nombre de hablante (líneas de sistema/narración del juego).
        private void AppendFormattedMessage(string message, ChatTarget target)
        {
            AppendMessageParts(message, target);
        }

        private void AppendSpeakerAndMessage(string speaker, string message, ChatTarget target)
        {
            AppendRun(speaker + ":  ", Teal, target, bold: true);
            AppendMessageParts(message, target, endWithNewLine: true);
        }

        // Divide el mensaje en fragmentos normales y fragmentos *entre asteriscos* (acciones/emotes),
        // pintando cada tipo con un estilo distinto dentro de la misma línea.
        private void AppendMessageParts(string message, ChatTarget target, bool endWithNewLine = true)
        {
            int lastIndex = 0;
            foreach (Match m in EmoteRegex.Matches(message))
            {
                if (m.Index > lastIndex)
                    AppendRun(message.Substring(lastIndex, m.Index - lastIndex), TextMain, target, bold: false);

                AppendRun("✦ " + m.Groups[1].Value.Trim() + " ✦", Coral, target, bold: true, italic: true);
                lastIndex = m.Index + m.Length;
            }

            if (lastIndex < message.Length)
                AppendRun(message.Substring(lastIndex), TextMain, target, bold: false);

            if (endWithNewLine)
                AppendLine("", TextMain, target); // salto de línea final
        }

        private void InputBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;

            var textEs = _inputBox.Text.Trim();
            if (string.IsNullOrEmpty(textEs)) return;
            _inputBox.Clear();

            try
            {
                var textEn = Translate(textEs, "es", "en");
                if (_mayusculasActivas) textEn = textEn.ToUpperInvariant();

                AppendLine("→ Enviando: " + textEn, Coral);

                // Enviar al juego bloquearía la UI si se hace en el hilo principal;
                // lo hacemos en segundo plano para no congelar la ventana durante la espera.
                var textoAEnviar = textEn;
                Task.Run(() => SendToGame(textoAEnviar));
            }
            catch (Exception ex)
            {
                AppendLine("[Error de envío]: " + ex.Message, TextDim);
            }
        }

        private void MayusBtn_Click(object? sender, EventArgs e)
        {
            _mayusculasActivas = !_mayusculasActivas;
            var bg = _mayusculasActivas ? Coral : BgField;
            _mayusBtn.BackColor = bg;
            _mayusBtn.IconColor = _mayusculasActivas ? CoralDark : TextDim;
            _mayusBtn.FlatAppearance.MouseOverBackColor = _mayusculasActivas ? CoralHover : BgFieldHover;
            _mayusBtnFrame.FillColor = bg;
            _mayusBtnFrame.BorderColor = _mayusculasActivas ? Coral : BorderSoft;
        }

        private void SendToGame(string text)
        {
            // Retardo extra antes de escribir en el juego, para dar margen a corregirse
            // o simplemente ir más despacio antes de que el mensaje salga.
            Thread.Sleep(RetardoEnvioExtraMs);

            var hwnd = FindGameWindow("Neverwinter", "NWN");

            if (hwnd == IntPtr.Zero)
            {
                AppendLine("[Aviso]: No se encontró la ventana del juego. Asegúrate de que Neverwinter Nights esté abierto.", TextDim);
                return;
            }

            SetForegroundWindow(hwnd);
            Thread.Sleep(150);
            SendKeys.SendWait("{ENTER}");
            Thread.Sleep(50);
            SendKeys.SendWait(EscapeForSendKeys(text));
            Thread.Sleep(50);
            SendKeys.SendWait("{ENTER}");
        }

        // SendKeys interpreta + ^ % ~ ( ) { } como caracteres especiales;
        // hay que escaparlos entre llaves para enviarlos como texto literal.
        private static string EscapeForSendKeys(string text)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in text)
            {
                switch (c)
                {
                    case '+':
                    case '^':
                    case '%':
                    case '~':
                    case '(':
                    case ')':
                    case '{':
                    case '}':
                        sb.Append('{').Append(c).Append('}');
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private string Translate(string text, string source, string target)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            if (_mode == "DeepL" && !string.IsNullOrEmpty(_deeplKey))
            {
                return TranslateDeepL(text, target);
            }
            return TranslateGoogle(text, source, target);
        }

        // Usa el endpoint público no oficial de Google Translate (translate.googleapis.com),
        // el mismo que emplean extensiones de traducción gratuitas.
        // Límite mínimo de tiempo entre peticiones al endpoint público de Google,
        // para evitar que nos bloquee con 429 (Too Many Requests) cuando llegan
        // varias líneas de chat seguidas.
        private static readonly object ThrottleLock = new();
        private static DateTime _lastGoogleCall = DateTime.MinValue;
        private const int MinMillisecondsBetweenCalls = 350;

        private string TranslateGoogle(string text, string source, string target)
        {
            const int maxRetries = 4;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                ThrottleGoogle();
                try
                {
                    var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={source}&tl={target}&dt=t&q={Uri.EscapeDataString(text)}";
                    var response = _http.GetStringAsync(url).GetAwaiter().GetResult();
                    using var doc = JsonDocument.Parse(response);
                    var root = doc.RootElement[0];
                    var result = "";
                    foreach (var segment in root.EnumerateArray())
                        result += segment[0].GetString();
                    return result;
                }
                catch (HttpRequestException ex) when (IsRateLimit(ex) && attempt < maxRetries)
                {
                    // Backoff exponencial: 1s, 2s, 4s, 8s antes de reintentar.
                    Thread.Sleep(1000 * (int)Math.Pow(2, attempt));
                }
            }
            throw new Exception("Google Translate sigue devolviendo 429 tras varios reintentos (límite de peticiones alcanzado).");
        }

        private static bool IsRateLimit(HttpRequestException ex)
        {
            return ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                   || (ex.Message?.Contains("429") ?? false);
        }

        private static void ThrottleGoogle()
        {
            lock (ThrottleLock)
            {
                var elapsed = (DateTime.UtcNow - _lastGoogleCall).TotalMilliseconds;
                if (elapsed < MinMillisecondsBetweenCalls)
                    Thread.Sleep(MinMillisecondsBetweenCalls - (int)elapsed);
                _lastGoogleCall = DateTime.UtcNow;
            }
        }

        private string TranslateDeepL(string text, string target)
        {
            var targetLang = target.Equals("es", StringComparison.OrdinalIgnoreCase) ? "ES" : "EN-US";
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api-free.deepl.com/v2/translate");
            request.Headers.Add("Authorization", $"DeepL-Auth-Key {_deeplKey}");
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("text", text),
                new KeyValuePair<string,string>("target_lang", targetLang)
            });
            request.Content = content;

            var response = _http.SendAsync(request).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new Exception("DeepL respondió con error: " + body);

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString() ?? text;
        }

        private enum ChatTarget { Ambos, Ingles, Espanol }

        // El chat se ve "de a 15 mensajes": se puede hacer scroll libremente hacia arriba
        // para ver bloques anteriores, pero para que la ventana no crezca sin límite en memoria
        // recortamos el historial más antiguo una vez se acumulan muchos bloques.
        private const int MensajesPorBloque = 15;
        private const int BloquesEnBuffer = 12; // ~180 mensajes conservados como máximo por panel

        private int _mensajesAmbos = 0;
        private int _mensajesIngles = 0;
        private int _mensajesEspanol = 0;

        // Sobrecarga de conveniencia: los mensajes de sistema/estado van solo al panel "Ambos".
        private void AppendLine(string text, Color color) => AppendLine(text, color, ChatTarget.Ambos);

        private void AppendLine(string text, Color color, ChatTarget target)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => AppendLine(text, color, target)));
                return;
            }

            var box = GetBox(target);
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = color;
            box.AppendText(text + Environment.NewLine);
            box.ScrollToCaret();
        }

        // Escribe un fragmento de texto SIN salto de línea, con color y estilo (negrita/cursiva)
        // propios — para poder mezclar varios estilos dentro del mismo renglón (ej. nombre en negrita
        // seguido del mensaje normal, con partes en *emote* resaltadas en medio).
        private void AppendRun(string text, Color color, ChatTarget target, bool bold = false, bool italic = false)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => AppendRun(text, color, target, bold, italic)));
                return;
            }

            var box = GetBox(target);
            var style = FontStyle.Regular;
            if (bold) style |= FontStyle.Bold;
            if (italic) style |= FontStyle.Italic;

            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = color;
            box.SelectionFont = new Font(box.Font, style);
            box.AppendText(text);
            box.ScrollToCaret();
        }

        private RichTextBox GetBox(ChatTarget target) => target switch
        {
            ChatTarget.Ingles => _chatBoxIngles,
            ChatTarget.Espanol => _chatBoxEspanol,
            _ => _chatBoxAmbos
        };

        // Se llama una vez por cada "mensaje" completo (no por cada línea suelta) para llevar
        // la cuenta y recortar el historial antiguo cuando se supera el máximo del buffer.
        private void NotificarMensajeCompleto(ChatTarget target)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => NotificarMensajeCompleto(target)));
                return;
            }

            int maxMensajes = MensajesPorBloque * BloquesEnBuffer;

            switch (target)
            {
                case ChatTarget.Ingles:
                    _mensajesIngles++;
                    if (_mensajesIngles > maxMensajes) RecortarBuffer(_chatBoxIngles, ref _mensajesIngles, maxMensajes);
                    break;
                case ChatTarget.Espanol:
                    _mensajesEspanol++;
                    if (_mensajesEspanol > maxMensajes) RecortarBuffer(_chatBoxEspanol, ref _mensajesEspanol, maxMensajes);
                    break;
                default:
                    _mensajesAmbos++;
                    if (_mensajesAmbos > maxMensajes) RecortarBuffer(_chatBoxAmbos, ref _mensajesAmbos, maxMensajes);
                    break;
            }
        }

        // Elimina el primer bloque de mensajes (el más antiguo) del texto del RichTextBox,
        // manteniendo el scroll en la posición actual del usuario tras el recorte.
        private static void RecortarBuffer(RichTextBox box, ref int contador, int maxMensajes)
        {
            var lines = box.Lines;
            // Buscamos dónde termina aproximadamente el primer bloque a eliminar
            // (una fracción de las líneas totales, proporcional a los mensajes de más).
            int exceso = contador - maxMensajes + MensajesPorBloque;
            int lineasPorMensajeAprox = Math.Max(1, lines.Length / Math.Max(1, contador));
            int lineasARecortar = Math.Min(lines.Length, exceso * lineasPorMensajeAprox);

            if (lineasARecortar <= 0) return;

            int charIndex = 0;
            for (int i = 0; i < lineasARecortar; i++)
                charIndex += lines[i].Length + Environment.NewLine.Length;

            bool wasAtEnd = box.SelectionStart >= box.TextLength - 1;
            box.Select(0, Math.Min(charIndex, box.TextLength));
            box.SelectedText = "";
            contador = Math.Max(0, contador - MensajesPorBloque);

            if (wasAtEnd)
            {
                box.SelectionStart = box.TextLength;
                box.ScrollToCaret();
            }
        }

    }
}
