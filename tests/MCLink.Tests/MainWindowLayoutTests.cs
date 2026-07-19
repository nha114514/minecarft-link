namespace MCLink.Tests;

public sealed class MainWindowLayoutTests
{
    [Fact]
    public void RoleSelectionDoesNotShowTheSubtitleOrIdleStatus()
    {
        var xamlPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("不用配置路由器，交换两次连接码", xaml);
        Assert.DoesNotContain("Text=\"MCLink P2P 测试器\"", xaml);
        Assert.DoesNotContain("准备就绪", xaml);
        Assert.DoesNotContain("选择创建或加入", xaml);
    }
}
