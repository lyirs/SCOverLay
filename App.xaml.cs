using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace StarCitizenOverLay
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private readonly ServiceProvider _serviceProvider;

        public ServiceProvider Services => _serviceProvider;

        public App()
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            var services = new ServiceCollection();
            services.AddWpfBlazorWebView();
            services.AddSingleton<OverlayShellState>();
            services.AddSingleton<OverlaySearchApiService>();

            _serviceProvider = services.BuildServiceProvider();
            Resources.Add("services", _serviceProvider);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider.Dispose();
            base.OnExit(e);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                var logPath = WriteCrashLog("OnStartup", ex);
                System.Windows.MessageBox.Show(
                    $"启动失败。{Environment.NewLine}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}日志：{logPath}",
                    "StarCitizenOverLay",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                Shutdown(-1);
                return;
            }

            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashLog("DispatcherUnhandledException", e.Exception);
        }

        private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            WriteCrashLog("AppDomainUnhandledException", e.ExceptionObject as Exception);
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
        }

        private static string WriteCrashLog(string source, Exception? exception)
        {
            try
            {
                var crashDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StarCitizenOverLay");

                Directory.CreateDirectory(crashDir);

                var logPath = Path.Combine(crashDir, "crash.log");
                var content =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                    $"{exception}{Environment.NewLine}" +
                    $"{new string('-', 80)}{Environment.NewLine}";

                File.AppendAllText(logPath, content);
                return logPath;
            }
            catch
            {
                return "(写日志失败)";
            }
        }
    }
}
