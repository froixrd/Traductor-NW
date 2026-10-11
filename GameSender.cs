using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TraductorNWN
{
    // Localiza la ventana del juego y le escribe el mensaje (Enter + texto + Enter).
    internal static class GameSender
    {
        private static readonly string[] GameProcessNames = { "nwmain" };

        // Procesos que NUNCA deben recibir pulsaciones aunque su título contenga "Neverwinter"
        // (una pestaña del navegador con la wiki, Discord, Steam…).
        private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore",
            "explorer", "discord", "steam", "steamwebhelper", "notepad", "notepad++", "code", "devenv"
        };

        private static IntPtr _cachedWindow;

        // Código físico (scancode) de la tecla Enter principal.
        private const ushort ScanEnter = 0x1C;

        // Tiempo que se mantiene pulsado Enter: el juego mira el teclado una vez por fotograma,
        // y una pulsación de 0 ms puede caer entre dos fotogramas y perderse.
        private const int EnterHoldMs = 50;

        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        // Devuelve null si se envió bien, o el motivo del fallo.
        //
        // Por qué no SendKeys: SendKeys simula las teclas sin su código físico (scancode). Las letras
        // llegan igualmente al chat como caracteres, pero el juego (NWN:EE usa SDL) identifica Enter por
        // ese código, así que el Enter final no se reconocía y el mensaje se quedaba escrito sin enviar.
        // Ahora:
        //  - Enter se envía con su scancode real y se mantiene pulsado unos milisegundos;
        //  - el texto se envía como caracteres Unicode: llega igual con cualquier distribución de
        //    teclado (ñ, tildes, ¿ ¡) y nunca puede disparar atajos del juego;
        //  - se deja margen para que el juego procese el texto antes del Enter final.
        //
        // returnTo: ventana a la que devolver el foco al terminar (el traductor), solo si era la ventana
        // activa justo antes de enviar; así se puede seguir escribiendo sin hacer clic.
        public static string? Send(string text, IntPtr returnTo = default)
        {
            var clean = OutgoingText.ForGame(text);
            if (clean.Length == 0) return null;

            var previous = NativeMethods.GetForegroundWindow();
            var hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero)
                return "No se encontró la ventana del juego. Asegúrate de que Neverwinter Nights esté abierto.";

            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            if (!BringToFront(hwnd))
                return "Windows no dejó poner el juego en primer plano. Haz clic en el juego y vuelve a enviar el mensaje.";

            // Con Alt, Ctrl o Mayús pulsadas, Enter podría hacer otra cosa (Alt+Enter = pantalla completa).
            WaitForModifiersReleased(1500);
            Thread.Sleep(150);

            // 1. Abrir la caja de chat.
            if (!PressEnter())
                return BlockedMessage;
            Thread.Sleep(250);

            // Comprobación de seguridad: si el usuario cambió de ventana justo ahora,
            // no escribimos el texto en otra aplicación.
            if (NativeMethods.GetForegroundWindow() != hwnd)
                return "El juego perdió el foco antes de escribir; mensaje no enviado.";

            // 2. Escribir el mensaje.
            if (!TypeText(clean))
                return BlockedMessage;

            // 3. Margen para que el juego termine de meter el texto en la caja antes del Enter final.
            Thread.Sleep(Math.Clamp(120 + clean.Length * 2, 150, 600));

            if (NativeMethods.GetForegroundWindow() != hwnd)
                return "El juego perdió el foco antes de enviar. Si el texto se quedó en el chat del juego, pulsa Enter allí.";

            // 4. Enviar.
            if (!PressEnter())
                return BlockedMessage;

            // 5. Volver al traductor, si desde ahí se escribió el mensaje.
            if (returnTo != IntPtr.Zero && previous == returnTo && NativeMethods.IsWindow(returnTo))
            {
                Thread.Sleep(150); // que el juego reciba el Enter antes de perder el foco
                BringToFront(returnTo);
            }
            return null;
        }

        private const string BlockedMessage =
            "Windows no dejó enviar las teclas al juego. Si el juego se ejecuta como administrador, abre también el traductor como administrador.";

        private static bool PressEnter()
        {
            bool down = SendKeyInputs(new[] { ScanKey(ScanEnter, keyUp: false) });
            Thread.Sleep(EnterHoldMs);
            bool up = SendKeyInputs(new[] { ScanKey(ScanEnter, keyUp: true) }); // soltar siempre, aunque fallara la pulsación
            return down && up;
        }

        private static bool TypeText(string text)
        {
            const int charsPerBatch = 16;
            var batch = new List<NativeMethods.INPUT>(charsPerBatch * 2);
            for (int i = 0; i < text.Length; i++)
            {
                batch.Add(UnicodeKey(text[i], keyUp: false));
                batch.Add(UnicodeKey(text[i], keyUp: true));
                bool last = i == text.Length - 1;
                if (batch.Count >= charsPerBatch * 2 || last)
                {
                    if (!SendKeyInputs(batch.ToArray())) return false;
                    batch.Clear();
                    if (!last) Thread.Sleep(8);
                }
            }
            return true;
        }

        private static bool SendKeyInputs(NativeMethods.INPUT[] inputs)
        {
            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            return sent == inputs.Length;
        }

        private static NativeMethods.INPUT ScanKey(ushort scanCode, bool keyUp) => new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wScan = scanCode,
                    dwFlags = NativeMethods.KEYEVENTF_SCANCODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0)
                }
            }
        };

        private static NativeMethods.INPUT UnicodeKey(char c, bool keyUp) => new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wScan = c,
                    dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0)
                }
            }
        };

        private static void WaitForModifiersReleased(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && AnyModifierDown())
                Thread.Sleep(20);
        }

        private static bool AnyModifierDown()
        {
            foreach (var vk in new[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN })
                if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0) return true;
            return false;
        }

        private static IntPtr FindGameWindow()
        {
            if (_cachedWindow != IntPtr.Zero && NativeMethods.IsWindow(_cachedWindow) && NativeMethods.IsWindowVisible(_cachedWindow))
                return _cachedWindow;

            _cachedWindow = FindByProcess();
            if (_cachedWindow == IntPtr.Zero)
                _cachedWindow = FindByTitle();
            return _cachedWindow;
        }

        // Lo más fiable: el ejecutable del juego (nwmain.exe).
        private static IntPtr FindByProcess()
        {
            foreach (var name in GameProcessNames)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }

                try
                {
                    foreach (var p in procs)
                    {
                        try
                        {
                            var h = p.MainWindowHandle;
                            if (h != IntPtr.Zero) return h;
                        }
                        catch { /* proceso cerrándose */ }
                    }
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }
            }
            return IntPtr.Zero;
        }

        // Plan B: por título, excluyendo nuestra propia ventana (su título contiene
        // "Neverwinter") y aplicaciones que no son el juego.
        private static IntPtr FindByTitle()
        {
            uint ownPid = (uint)Environment.ProcessId;
            IntPtr found = IntPtr.Zero;
            var sb = new StringBuilder(256);

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                sb.Clear();
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.Length == 0) return true;

                bool looksLikeGame = title.Contains("Neverwinter", StringComparison.OrdinalIgnoreCase)
                                     || title.StartsWith("NWN", StringComparison.OrdinalIgnoreCase);
                if (!looksLikeGame) return true;

                NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == ownPid || IsExcludedProcess(pid)) return true;

                found = hWnd;
                return false; // encontrada, dejamos de enumerar
            }, IntPtr.Zero);

            return found;
        }

        private static bool IsExcludedProcess(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return ExcludedProcesses.Contains(p.ProcessName);
            }
            catch
            {
                return false;
            }
        }

        private static bool BringToFront(IntPtr hwnd)
        {
            if (NativeMethods.GetForegroundWindow() == hwnd) return true;

            NativeMethods.SetForegroundWindow(hwnd);
            if (WaitForeground(hwnd, 150)) return true;

            // Windows a veces bloquea SetForegroundWindow; el truco habitual es enlazar
            // temporalmente la entrada con el hilo de la ventana que tiene el foco.
            var fg = NativeMethods.GetForegroundWindow();
            uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
            uint thisThread = NativeMethods.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != thisThread &&
                            NativeMethods.AttachThreadInput(thisThread, fgThread, true);
            try
            {
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(thisThread, fgThread, false);
            }
            return WaitForeground(hwnd, 300);
        }

        private static bool WaitForeground(IntPtr hwnd, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (NativeMethods.GetForegroundWindow() == hwnd) return true;
                Thread.Sleep(15);
            }
            return NativeMethods.GetForegroundWindow() == hwnd;
        }
    }
}
