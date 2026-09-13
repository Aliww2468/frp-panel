# FRP Panel

简洁的 Windows FRP 桌面管理面板，支持后台托盘、配置导入和四种外观风格。

## 下载与使用

在 [Releases](https://github.com/Aliww2468/frp-panel/releases/latest) 下载 `FRP-Panel-Setup-1.3.0-x64.exe`，安装后从桌面或开始菜单打开。

- 支持 Windows 10/11 x64，内置 Python 和 FRPC 0.71.0；安装时自动检测 .NET 8 Desktop Runtime x64，缺少才联网安装。
- 在「连接配置」中填写连接信息，或直接导入服务商提供的 UTF-8 `.toml` 配置。
- 查看真实连接状态，管理 TCP / UDP 代理，搜索规则、复制地址、查看和导出日志。
- 关闭或最小化窗口后驻留托盘；双击托盘图标恢复，右键可启停客户端、设置开机自启或退出。
- 同一 Windows 会话只允许运行一个面板，跨安装目录也会检测。重复启动会提示“软件已在运行”，确认提示后退出本次启动，不影响原实例。
- 右上角或托盘菜单「检查更新」从本仓库 Releases 检查正式版，下载并校验后可确认安装。升级保留配置，完成后重新打开软件；转发需手动重新启动。
- 四种风格：简约浅色、午夜深色、暖砂米色、雾蓝灰。Windows 11 标题栏同步风格并隐藏外框描边。

首次打开和开机自启均不会自动建立 FRP 转发；配置完成后手动启动客户端。多数服务商不支持从客户端直接新增隧道，请先在服务商后台创建。

安装包不包含个人配置。更新前先从托盘退出旧版本，覆盖安装保留已生成的配置并清理旧版内置 .NET 文件。缺少 .NET 8 Desktop Runtime x64 或 WebView2 时，安装程序联网安装 Microsoft 官方运行环境；安装 .NET 会请求 Windows 管理员权限。已有环境则直接继续。

在线更新从 1.3.0 起提供，旧版首次需要手动安装此版本。检查和下载不会停止转发；点击「安装并重启」并确认后才停止后台。需要能连接 GitHub，不会自动下载或静默升级。单独使用网页入口时不提供桌面更新按钮。

## 从源码运行

需要 .NET 8 SDK；单独运行网页面板需要 Python 3.11+。先下载 [FRP 0.71.0 Windows AMD64](https://github.com/fatedier/frp/releases/tag/v0.71.0) 的官方压缩包，校验官方 SHA-256 后解压到项目根目录，将其中的 `frpc.exe` 复制到 `client/`。首次启动会生成空白配置。

```powershell
Copy-Item ./frp_0.71.0_windows_amd64/frpc.exe ./client/frpc.exe
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/build.ps1
```

构建完成后使用根目录生成的「FRP 面板.lnk」或「启动软件.cmd」。桌面运行需要 WebView2 Runtime。

也可单独运行网页面板：

```powershell
python panel/server.py
```

浏览器入口为 `http://127.0.0.1:17600`。配置文件在 `client/frpc.toml`，日志在 `client/logs/`，配置备份在 `panel/backups/`。不要将这些文件提交到仓库。

## 构建安装包与验证

参见 [桌面构建说明](desktop/README.md) 和 [面板说明](panel/README.md)。

```powershell
desktop/app/python/python.exe -m unittest discover -s panel/tests -v
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/build-installer.ps1
```

测试使用独立临时配置；完整转发测试需要可执行的 FRPS。状态与单元测试不能代替真实外网连通性验证。

## 技术与第三方组件

- 桌面：C# / .NET 8 WinForms + Microsoft WebView2。
- 面板：Python 标准库 HTTP 服务，原生 HTML / CSS / JavaScript。
- 转发：[fatedier/frp](https://github.com/fatedier/frp)，本项目是独立管理界面。

第三方组件遵循各自许可证，发行安装目录的 `licenses/` 及运行环境目录保留相应许可文件。
