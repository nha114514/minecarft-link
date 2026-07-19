# MCLink P2P Tester

MCLink P2P Tester 是一个 Windows 图形工具，用 WebRTC 数据通道把房主本机的 Minecraft Java 局域网世界直接转给同学。它不需要配置光猫端口映射，也不依赖公网 IPv4、付费服务器或商业中继。

## 使用方法

房主和加入方都需要安装 MCLink P2P Tester，并使用相同的 Minecraft 版本和模组。

### 房主

1. 在 Minecraft 单人世界中点击“对局域网开放”，记下聊天中显示的端口。
2. 打开 MCLink P2P Tester，选择“创建联机”。
3. 输入 Minecraft 局域网端口并生成邀请信息。
4. 把邀请信息私下发给同学。
5. 收到同学的回应信息后粘贴回来，等待连接完成。

### 加入方

1. 打开 MCLink P2P Tester，选择“加入联机”。
2. 粘贴房主发来的邀请信息并生成回应信息。
3. 把回应信息发回房主。
4. 工具显示本地地址后，在 Minecraft“多人游戏”中连接该地址。

邀请和回应信息不会自动复制，也不会写入日志。只把它们发给参与本次联机的同学。

## 限制

- 只支持 Minecraft Java 的 TCP 游戏流量；Bedrock 和 UDP 语音模组不在当前范围内。
- 这是不使用中继服务器的直接连接。某些运营商 CGNAT、对称 NAT、校园网或严格防火墙组合仍可能阻止连接。
- 如果直连失败，工具不会偷偷改用付费服务或上传游戏流量。

## 安装

最终安装包：

```text
artifacts\installer\MCLink-P2P-Tester-Setup.exe
```

安装器包含 `.NET 8` 运行环境。接收电脑不需要预装 SDK。安装时可以使用默认位置，也可以选择父目录；例如选择 `D:\Apps` 后会安装到 `D:\Apps\MCLink P2P Tester`。

安装器只请求一次管理员权限，用于写入所选安装目录、创建快捷方式、登记卸载信息，以及添加仅限程序本身的 UDP 入站防火墙规则。

## 构建和测试

开发电脑需要 `.NET 8 SDK`：

```powershell
dotnet test MCLink.sln -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer\p2p-tester\Build-Installer.ps1 -RepositoryRoot D:\Dev\Projects\lianjiproject
powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer\p2p-tester\Verify-Package.ps1 -RepositoryRoot D:\Dev\Projects\lianjiproject
```

解决方案只保留 P2P 核心、P2P 测试和 WPF 测试器三个项目。
