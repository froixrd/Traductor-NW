namespace TraductorNWN
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();

            // Una sola instancia: dos traductores leyendo el mismo log duplicarían
            // las peticiones a la API (y los mensajes enviados al juego).
            using var mutex = new Mutex(true, @"Local\TraductorNWN_SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show("El traductor ya está abierto.", "Traductor NWN",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Un error inesperado no debe cerrar la aplicación en mitad de la partida.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                ErrorLog.Write(e.Exception);
                MessageBox.Show("Error inesperado: " + e.Exception.Message + "\n\nDetalles en:\n" + ErrorLog.FilePath,
                    "Traductor NWN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ErrorLog.Write(e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                ErrorLog.Write(e.Exception);
                e.SetObserved();
            };

            Application.Run(new MainForm());
        }
    }

    // Registro de errores en %AppData%\TraductorNWN\errores.log (para poder diagnosticar fallos).
    internal static class ErrorLog
    {
        private static readonly object Lock = new();

        public static string FilePath => Path.Combine(AppSettings.Folder, "errores.log");

        public static void Write(Exception? ex)
        {
            if (ex == null) return;
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(AppSettings.Folder);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 512 * 1024) info.Delete();
                    File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
                }
            }
            catch { /* nunca fallar por registrar un fallo */ }
        }
    }
}
