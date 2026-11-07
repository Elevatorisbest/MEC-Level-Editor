using Frosty.Core;
using LevelEditorPlugin.BF1Core.Native;
using LevelEditorPlugin.Diagnostics;
using System.Windows;

namespace LevelEditorPlugin.BF1Core.Extensions;

public class BF1CoreMenuExtension : MenuExtension
{
    public override string TopLevelMenuName => "Map";
    public override string MenuItemName => "Battlefield 1 Map Editor";

    private MainWindow mainWindow = null;

    public BF1CoreMenuExtension()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (mainWindow == null)
            {
                mainWindow = new MainWindow();
                mainWindow.Hide();
            }

            UiHangWatchdog.EnsureStarted();

            HotKey.AddKey(Keys.F1);
            // F5 ("copy mesh to camera") is not registered: Frosty uses F5 for Launch, so one
            // key press both added a copy and launched the game. Use the window's button instead.
            HotKey.Run();
        });
    }

    private static int showCount;

    /// <summary>
    /// Logs how long it took from the menu click until the window was drawn and the UI thread was
    /// idle again (first opening of a session was reported to be slow).
    /// </summary>
    private static void MeasureShow(string what)
    {
        int n = ++showCount;
        PerfProbe probe = PerfProbe.Start("BF1 window", $"{what} (opening #{n} this session)");
        Application.Current.Dispatcher.BeginInvoke(new System.Action(probe.Dispose), System.Windows.Threading.DispatcherPriority.Background);
    }

    public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
    {
        // First creation
        if (mainWindow == null)
            goto RE_CREATE;

        // Window hidden
        if (mainWindow.Visibility == Visibility.Hidden)
        {
            MeasureShow("show window");
            mainWindow.Show();
            return;
        }

        // Window minimized
        if (mainWindow.WindowState == WindowState.Minimized)
        {
            MeasureShow("restore minimized window");
            mainWindow.WindowState = WindowState.Normal;
            return;
        }

        // Window not topmost (false means window has been closed)
        if (mainWindow.IsVisible)
        {
            mainWindow.Topmost = true;
            mainWindow.Topmost = false;
            return;
        }

        // Window was closed, recreate
        mainWindow.Close();
        mainWindow = null;

    RE_CREATE:
        MeasureShow("create + show window");
        mainWindow = new MainWindow();
        mainWindow.Show();
    });
}
