using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XiaoJian
{
    internal static class Program
    {
        private static readonly object ErrorLogLock = new object();
        private static bool _shuttingDown;

        // Set as soon as the application starts tearing its windows down. A failure during that
        // window is written to the log but never reported with a dialog, because a modal box
        // raised while the message loop is being dismantled would block the exit it is reporting.
        internal static void BeginShutdown()
        {
            _shuttingDown = true;
        }

        // Posts work to the UI thread's message queue. The WinForms synchronisation context owns a
        // hidden marshalling window that outlives every card, so this still works once the last
        // window has closed — which is exactly when the application has to be told to quit.
        internal static void PostToUi(MethodInvoker action)
        {
            if (action == null) return;
            SynchronizationContext context = SynchronizationContext.Current;
            if (context == null)
            {
                action();
                return;
            }
            context.Post(delegate(object state) { action(); }, null);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArgument(args, "--self-test"))
            {
                return SelfTests.Run();
            }

            // Portable by default: everything lives in a "data" folder next to the executable, and
            // only a program sitting somewhere it may not write to falls back to the per-user
            // folder. Content written by an older version is carried over the first time, and so is
            // its autostart launcher - that one is named after the old product, and left alone it
            // would sit in the Startup folder starting nothing.
            string dataDirectory = DataLocation.Resolve(args);
            DataLocation.AdoptLegacyData(dataDirectory);
            StartupManager.CarryOverLegacyLauncher();
            AppState state = AppState.Load(Path.Combine(dataDirectory, "state.ini"));

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\XiaoJian.SingleInstance.1A7C4B92", out createdNew))
            {
                if (!createdNew)
                {
                    if (TryLocateExistingCard()) return 0;
                    MessageBox.Show(UiText.Get(state,
                        "小笺已经在运行了。\n\n请双击右下角通知区域中的小图标，或按 Ctrl+Alt+Q 寻找卡片。",
                        "XiaoJian is already running.\n\nDouble-click its tray icon or press Ctrl+Alt+Q to find the card."),
                        UiText.Get(state, "小笺", "XiaoJian"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                InstallExceptionHandling();

                bool suppressStartupRegistration = HasArgument(args, "--preview");
                // Every card keeps its own content library. The one shared library older versions
                // wrote is handed to the first card before any card reads its own file.
                QuoteRepository.MigrateSharedLibrary(dataDirectory, state);

                if (!state.StartupInitialized && !suppressStartupRegistration)
                {
                    state.StartupInitialized = true;
                    try
                    {
                        DialogResult answer = MessageBox.Show(
                            UiText.Get(state,
                                "要让小笺随 Windows 自动启动吗？\n\n以后可以随时在卡片的右键菜单中修改。",
                                "Start XiaoJian automatically with Windows?\n\nYou can change this later from the card's right-click menu."),
                            UiText.Get(state, "欢迎使用小笺", "Welcome to XiaoJian"),
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button1);
                        StartupManager.SetEnabled(answer == DialogResult.Yes);
                    }
                    catch
                    {
                        state.StartupEnableFailed = true;
                    }
                    state.Save();
                }

                using (CardManager manager = new CardManager(state, dataDirectory))
                {
                    manager.Start();
                    // No main form: the application lives in the tray and in however many cards the
                    // user has, and leaves through Application.Exit from the menu.
                    Application.Run();
                }
                return 0;
            }
        }

        // Quitting disposes a context menu, one or more card windows, a tray icon and a hot key,
        // all of it native state. A failure that still slips through is reported and written to a
        // log rather than being allowed to fall out of the message loop, where Windows answers
        // with the bare "JIT debugging" prompt and no clue about what actually went wrong.
        private static void InstallExceptionHandling()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                ReportUnhandledException(e == null ? null : e.Exception, false);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                bool terminating = e != null && e.IsTerminating;
                ReportUnhandledException(e == null ? null : e.ExceptionObject as Exception, terminating);
            };
        }

        private static void ReportUnhandledException(Exception error, bool fatal)
        {
            string logPath = WriteErrorLog(error);
            if (_shuttingDown && !fatal) return;

            string detail = error == null ? "" : error.Message;
            string message =
                "小笺遇到了一个意外错误。" + (fatal ? "程序即将退出。" : "程序会继续运行。") + "\n\n" +
                "错误信息：" + detail + "\n\n" +
                "详细信息已记录到：\n" + logPath + "\n\n" +
                "XiaoJian hit an unexpected error." +
                (fatal ? " The application will close." : " The application will keep running.") + "\n\n" +
                "Details: " + detail + "\n\n" +
                "Full details were written to:\n" + logPath;
            try
            {
                MessageBox.Show(message, "小笺 · XiaoJian", MessageBoxButtons.OK,
                    fatal ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
            }
            catch (Exception)
            {
                // A report that cannot be shown must not replace the failure it describes.
            }
        }

        // Appends to a log next to the executable, falling back to the temporary directory when
        // the program sits somewhere it may not write to.
        private static string WriteErrorLog(Exception error)
        {
            string[] candidates = new string[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XiaoJian_error.log"),
                Path.Combine(Path.GetTempPath(), "XiaoJian_error.log")
            };
            lock (ErrorLogLock)
            {
                foreach (string path in candidates)
                {
                    try
                    {
                        bool fresh = !File.Exists(path);
                        using (StreamWriter writer = new StreamWriter(path, true, new UTF8Encoding(false)))
                        {
                            if (fresh) writer.WriteLine("XiaoJian error log");
                            writer.WriteLine("[" +
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "]");
                            writer.WriteLine(error == null ? "(no exception object)" : error.ToString());
                            writer.WriteLine();
                        }
                        return path;
                    }
                    catch (Exception)
                    {
                        // Try the next location; logging must never throw on top of the failure.
                    }
                }
            }
            return "(不可写 / not writable)";
        }

        private static bool HasArgument(string[] args, string expected)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool TryLocateExistingCard()
        {
            string[] titles = new string[] { "小笺", "XiaoJian" };
            foreach (string title in titles)
            {
                IntPtr window = NativeMethods.FindWindow(null, title);
                if (window == IntPtr.Zero) continue;
                IntPtr response;
                IntPtr sent = NativeMethods.SendMessageTimeout(window, (uint)QuoteCardForm.LocateMessage,
                    IntPtr.Zero, IntPtr.Zero, 0x0002, 1000, out response);
                if (sent != IntPtr.Zero && response.ToInt32() == QuoteCardForm.LocateResponse) return true;
            }
            return false;
        }
    }
}
