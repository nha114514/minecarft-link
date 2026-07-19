using System.Windows;
using System.Windows.Threading;

namespace MCLink.P2p.Tester;

public partial class App : Application
{
    private int _fatalErrorShown;

    public App()
    {
        DispatcherUnhandledException += HandleDispatcherUnhandledException;
    }

    private void HandleDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (Interlocked.Exchange(ref _fatalErrorShown, 1) == 0)
        {
            MessageBox.Show(
                "程序遇到意外错误，请重新打开。",
                "MCLink P2P 测试器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        Shutdown(-1);
    }
}
