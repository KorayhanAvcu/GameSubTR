using System.IO;
using System.Windows;
using System.Windows.Threading;
using GameSubTR.Core.Logging;

namespace GameSubTR.Patcher;

public partial class App : Application
{
    /// <summary>GameSubTR ile ortak veri klasörü (ayarlar, loglar).</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSubTR");

    public static string LogDir => Path.Combine(DataDir, "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Init(LogDir);
        Log.Info("GameSubTR.Patcher başladı");
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Beklenmeyen hata", e.Exception);
        MessageBox.Show("Beklenmeyen bir hata oluştu:\n\n" + e.Exception.Message + "\n\nAyrıntı log klasöründe.",
            "GameSubTR Patcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
