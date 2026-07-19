using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MCLink.Core;

namespace MCLink.P2p.Tester;

public partial class MainWindow : Window
{
    private static readonly Brush NeutralStatusBrush = new SolidColorBrush(Color.FromRgb(147, 160, 153));
    private static readonly Brush WorkingStatusBrush = new SolidColorBrush(Color.FromRgb(210, 153, 58));
    private static readonly Brush SuccessStatusBrush = new SolidColorBrush(Color.FromRgb(47, 118, 84));
    private static readonly Brush ErrorStatusBrush = new SolidColorBrush(Color.FromRgb(178, 69, 62));

    private P2pHostCoordinator? _host;
    private P2pGuestCoordinator? _guest;
    private Task? _guestWaitTask;
    private int? _hostPort;
    private bool _stopping;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        ShowRoleSelection();
    }

    private void CreateModeButton_Click(object sender, RoutedEventArgs e)
    {
        RolePanel.Visibility = Visibility.Collapsed;
        GuestPanel.Visibility = Visibility.Collapsed;
        HostPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        SetStatus("创建联机", "先在 Minecraft 中对局域网开放", NeutralStatusBrush);
        HostPortTextBox.Focus();
    }

    private void JoinModeButton_Click(object sender, RoutedEventArgs e)
    {
        RolePanel.Visibility = Visibility.Collapsed;
        HostPanel.Visibility = Visibility.Collapsed;
        GuestPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        SetStatus("加入联机", "粘贴房主发来的邀请码", NeutralStatusBrush);
        GuestInviteTextBox.Focus();
    }

    private void TutorialButton_Click(object sender, RoutedEventArgs e)
    {
        var tutorial = new TutorialWindow { Owner = this };
        tutorial.ShowDialog();
    }

    private async void CreateInviteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HostPortTextBox.Text.Trim(), out var port)
            || port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            SetStatus("端口不正确", "请输入 Minecraft 显示的 1–65535 端口", ErrorStatusBrush);
            return;
        }

        SetHostBusy(true);
        SetStatus("正在确认世界", "检查这个端口是不是 Minecraft Java 世界…", WorkingStatusBrush);
        try
        {
            var endpoint = await FindMinecraftEndpointAsync(port);
            if (endpoint is null)
            {
                SetStatus("没有找到世界", "请先对局域网开放，并核对游戏显示的端口", ErrorStatusBrush);
                return;
            }

            if (_host is null || _hostPort != port)
            {
                if (_host is not null)
                {
                    await _host.StopAsync();
                }

                _host = new P2pHostCoordinator(endpoint);
                _hostPort = port;
            }

            InviteCodeTextBox.Text = await _host.CreateInviteAsync();
            HostResponseTextBox.Clear();
            HostInviteSection.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Visible;
            SetStatus("邀请码已准备好", "复制并私发给同学，再粘贴回应码", SuccessStatusBrush);
        }
        catch (Exception exception)
        {
            ShowConnectionError(exception);
        }
        finally
        {
            SetHostBusy(false);
        }
    }

    private async void AcceptResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null || string.IsNullOrWhiteSpace(HostResponseTextBox.Text))
        {
            SetStatus("还缺少回应码", "粘贴同学发回的完整内容", ErrorStatusBrush);
            return;
        }

        SetHostBusy(true);
        SetStatus("正在连接", "通常会在几秒内完成…", WorkingStatusBrush);
        try
        {
            await _host.AcceptResponseAsync(HostResponseTextBox.Text.Trim());
            SetStatus(
                "已连接",
                $"当前已连接 {_host.ConnectedPeerCount} 人，可以进入游戏",
                SuccessStatusBrush);
        }
        catch (Exception exception)
        {
            ShowConnectionError(exception);
        }
        finally
        {
            SetHostBusy(false);
        }
    }

    private async void CreateResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GuestInviteTextBox.Text))
        {
            SetStatus("还缺少邀请码", "粘贴房主发来的完整内容", ErrorStatusBrush);
            return;
        }

        CreateResponseButton.IsEnabled = false;
        SetStatus("正在准备回应码", "不会保存你粘贴的连接码…", WorkingStatusBrush);
        P2pGuestCoordinator? coordinator = null;
        try
        {
            if (_guest is not null)
            {
                await _guest.StopAsync();
            }

            coordinator = new P2pGuestCoordinator();
            var response = await coordinator.CreateResponseAsync(GuestInviteTextBox.Text.Trim());
            _guest = coordinator;
            ResponseCodeTextBox.Text = response;
            GuestResponseSection.Visibility = Visibility.Visible;
            LocalAddressSection.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            SetStatus("回应码已准备好", "复制并发回给房主，程序正在等待连接", SuccessStatusBrush);
            _guestWaitTask = WaitForGuestConnectionAsync(coordinator);
        }
        catch (Exception exception)
        {
            if (coordinator is not null)
            {
                await coordinator.StopAsync();
            }

            ShowConnectionError(exception);
            CreateResponseButton.IsEnabled = true;
        }
    }

    private async Task WaitForGuestConnectionAsync(P2pGuestCoordinator coordinator)
    {
        try
        {
            var port = await coordinator.WaitForLocalPortAsync();
            if (!ReferenceEquals(_guest, coordinator))
            {
                return;
            }

            LocalAddressTextBox.Text = $"127.0.0.1:{port}";
            LocalAddressSection.Visibility = Visibility.Visible;
            SetStatus("连接成功", "复制地址，在 Minecraft 多人游戏中直接连接", SuccessStatusBrush);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_guest, coordinator))
            {
                ShowConnectionError(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_guest, coordinator))
            {
                CreateResponseButton.IsEnabled = true;
            }
        }
    }

    private void CopyInviteButton_Click(object sender, RoutedEventArgs e) =>
        CopyText(InviteCodeTextBox.Text, "邀请码已复制");

    private void CopyResponseButton_Click(object sender, RoutedEventArgs e) =>
        CopyText(ResponseCodeTextBox.Text, "回应码已复制");

    private void CopyAddressButton_Click(object sender, RoutedEventArgs e) =>
        CopyText(LocalAddressTextBox.Text, "地址已复制");

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopActiveSessionAsync(resetInterface: true);
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        await StopActiveSessionAsync(resetInterface: true);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs eventArgs)
    {
        if (_closing)
        {
            return;
        }

        eventArgs.Cancel = true;
        _closing = true;
        await StopActiveSessionAsync(resetInterface: false);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Closing -= MainWindow_Closing;
            Close();
        });
    }

    private async Task StopActiveSessionAsync(bool resetInterface)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        StopButton.IsEnabled = false;
        SetStatus("正在停止", "关闭监听和直连…", WorkingStatusBrush);
        var guest = _guest;
        var host = _host;
        _guest = null;
        _host = null;
        _hostPort = null;
        try
        {
            var stops = new List<Task>(2);
            if (guest is not null)
            {
                stops.Add(guest.StopAsync());
            }

            if (host is not null)
            {
                stops.Add(host.StopAsync());
            }

            await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(10));
            if (_guestWaitTask is not null)
            {
                await _guestWaitTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                          or IOException
                                          or SocketException
                                          or ObjectDisposedException
                                          or TimeoutException)
        {
        }
        finally
        {
            _guestWaitTask = null;
            _stopping = false;
            StopButton.IsEnabled = true;
            if (resetInterface)
            {
                ClearSessionFields();
                ShowRoleSelection();
            }
        }
    }

    private static async Task<IPEndPoint?> FindMinecraftEndpointAsync(int port)
    {
        var probe = new MinecraftStatusProbe();
        if (await probe.ProbeAsync(
                IPAddress.Loopback,
                port,
                TimeSpan.FromSeconds(2)) is not null)
        {
            return new IPEndPoint(IPAddress.Loopback, port);
        }

        if (await probe.ProbeAsync(
                IPAddress.IPv6Loopback,
                port,
                TimeSpan.FromSeconds(2)) is not null)
        {
            return new IPEndPoint(IPAddress.IPv6Loopback, port);
        }

        return null;
    }

    private void SetHostBusy(bool busy)
    {
        CreateInviteButton.IsEnabled = !busy;
        AcceptResponseButton.IsEnabled = !busy;
        HostPortTextBox.IsEnabled = !busy;
    }

    private void CopyText(string text, string successStatus)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            SetStatus(successStatus, "现在可以发给对方", SuccessStatusBrush);
        }
        catch (COMException)
        {
            SetStatus("复制失败", "请选中文本后按 Ctrl+C", ErrorStatusBrush);
        }
    }

    private void ShowConnectionError(Exception exception)
    {
        if (exception is InvalidDataException or FormatException)
        {
            SetStatus("连接码不可用", "连接码无效、过期或不属于当前邀请", ErrorStatusBrush);
        }
        else if (exception is TimeoutException)
        {
            SetStatus("90 秒内没有连上", "当前网络组合可能不支持无中继直连", ErrorStatusBrush);
        }
        else if (exception is SocketException or IOException)
        {
            SetStatus("直连已断开", "检查网络后重新交换连接码", ErrorStatusBrush);
        }
        else if (exception is not OperationCanceledException)
        {
            SetStatus("没有完成连接", "请停止后重新交换连接码", ErrorStatusBrush);
        }
    }

    private void ShowRoleSelection()
    {
        RolePanel.Visibility = Visibility.Visible;
        HostPanel.Visibility = Visibility.Collapsed;
        GuestPanel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
        DetailText.Text = string.Empty;
    }

    private void ClearSessionFields()
    {
        InviteCodeTextBox.Clear();
        HostResponseTextBox.Clear();
        GuestInviteTextBox.Clear();
        ResponseCodeTextBox.Clear();
        LocalAddressTextBox.Clear();
        HostInviteSection.Visibility = Visibility.Collapsed;
        GuestResponseSection.Visibility = Visibility.Collapsed;
        LocalAddressSection.Visibility = Visibility.Collapsed;
        CreateResponseButton.IsEnabled = true;
        SetHostBusy(false);
    }

    private void SetStatus(string status, string detail, Brush brush)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = status;
        DetailText.Text = detail;
        StatusDot.Fill = brush;
    }
}
