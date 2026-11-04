using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BiliLiveManager;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Set up logging before constructing MainWindow. WPF StartupUri can otherwise
        // terminate the process silently when InitializeComponent or tray init fails.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Write("进程级未处理异常", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("未观察到的后台任务异常", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        base.OnStartup(e);
        try
        {
            AppLog.Write("启动阶段：开始构造主窗口，参数=" + string.Join(" ", e.Args));
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            // Do not allow a stale --minimized flag or tray setting to hide a manual launch.
            if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            {
                window.WindowState = WindowState.Normal;
                window.Activate();
            }
            AppLog.Write("启动阶段：主窗口已显示");
        }
        catch (Exception ex)
        {
            AppLog.Write("严重：启动时构造或显示主窗口失败", ex);
            try
            {
                System.Windows.MessageBox.Show("程序启动失败：" + ex.Message + "\n\n详细日志：\n" + AppLog.LogFile +
                   "\n\n可尝试运行“安全模式启动.cmd”。", "BiliSnagTide 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            Shutdown(1);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        AppLog.Write("UI 线程未处理异常", args.Exception);
        try
        {
            System.Windows.MessageBox.Show("发生界面错误：" + args.Exception.Message + "\n\n日志：" + AppLog.LogFile,
                "BiliSnagTide 运行异常", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
        // For runtime failures after a window exists, allow the user to view and save logs.
        // Fatal startup failures are handled by the explicit constructor try/catch above.
        args.Handled = MainWindow is { IsLoaded: true };
    }
}
