using System.Diagnostics;
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

        // Devuelve null si se envió bien, o el motivo del fallo.
        public static string? Send(string text)
        {
            var hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero)
                return "No se encontró la ventana del juego. Asegúrate de que Neverwinter Nights esté abierto.";

            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            if (!BringToFront(hwnd))
                return "Windows no dejó poner el juego en primer plano. Haz clic en el juego y vuelve a enviar el mensaje.";

            Thread.Sleep(120);
            SendKeys.SendWait("{ENTER}");
            Thread.Sleep(60);

            // Comprobación de seguridad: si el usuario cambió de ventana justo ahora,
            // no escribimos el texto en otra aplicación.
            if (NativeMethods.GetForegroundWindow() != hwnd)
                return "El juego perdió el foco antes de escribir; mensaje no enviado.";

            SendKeys.SendWait(EscapeForSendKeys(text));
            Thread.Sleep(60);
            SendKeys.SendWait("{ENTER}");
            return null;
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

        // SendKeys interpreta + ^ % ~ ( ) { } [ ] como especiales: van entre llaves.
        // Los saltos de línea se convierten en espacios (un Enter enviaría el mensaje a medias).
        private static string EscapeForSendKeys(string text)
        {
            var sb = new StringBuilder(text.Length + 8);
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
                    case '[':
                    case ']':
                        sb.Append('{').Append(c).Append('}');
                        break;
                    case '\r':
                    case '\n':
                    case '\t':
                        sb.Append(' ');
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
