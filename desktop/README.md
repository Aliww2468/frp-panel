# FRP Panel 桌面版

## EXE 安装包

成品位于 `../release/FRP-Panel-Setup-1.1.1-x64.exe`，也可从仓库 Releases 下载，支持 Windows 10/11 x64。
这是包含软件和运行环境的安装包；安装后通过桌面或开始菜单快捷方式启动。
默认安装到 `%LOCALAPPDATA%/Programs/FrpPanel`，不要求管理员权限。
缺少 WebView2 时安装程序会联网安装 Microsoft 官方运行环境。

安装包只包含程序和空白初始状态，不包含开发目录里的服务器地址、Token、代理规则、日志或备份。
首次启动自动生成本机管理密码；覆盖安装和卸载均保留已生成的配置、日志及备份。
安装更新或卸载前先从托盘退出软件。旧目录版本和安装版默认使用相同的 17600 端口，应先退出旧版本再启动安装版。

运行 `desktop/build-installer.ps1` 可重新打包。需要 .NET 8 SDK、Inno Setup 6 编译器（默认 `desktop/tools/inno/ISCC.exe`，可用 `-Compiler` 指定），以及 `desktop/downloads/` 中的 Python 嵌入包、简体中文安装语言文件和 Microsoft WebView2 Bootstrapper。构建脚本使用独立白名单目录并校验 Bootstrapper 的 Microsoft 数字签名。
安装器定义位于 `desktop/installer.iss`。产物的 SHA-256 摘要与 EXE 保存在同一目录。

已验证：独立目录静默安装、从安装目录启动桌面及后台、重复启动唤回窗口、托盘退出、覆盖安装保留配置哈希、卸载保留配置。8 项后端测试通过；完整 FRP 转发测试因本机没有可用的测试 FRPS 而跳过。

## 外观风格

右上角“外观”提供简约浅色、午夜深色、暖砂米色和雾蓝灰。点击立即应用，选择保存在本机浏览器配置中，刷新和重开后自动恢复。

## 项目目录版本

在项目根目录双击 **FRP 面板.lnk** 或 **启动软件.cmd**。
实际程序位于 `desktop/app/FrpPanel.exe`，沿用当前项目的 `client/frpc.toml` 和 `panel/`，不会复制或重置你的配置。

## 使用

- 独立 Windows 窗口，界面内嵌，不需要打开浏览器。
- 关闭或最小化窗口会隐藏到系统托盘，后台面板和已启动的 FRPC 继续运行。
- 双击托盘图标，或再次启动软件，可恢复原窗口。重复打开不会启动第二份软件。
- 右键托盘可启动 / 停止客户端、打开配置目录、设置开机自启。
- “退出软件并停止转发”会先停止客户端，再关闭后台面板和桌面软件；配置保留。本地游戏 / 应用服务不受影响。
- 开机自启默认关闭。勾选后当前 Windows 用户登录时驻留托盘；**不会自动启动 FRPC 转发**，需手动启动客户端。
- 这是登录用户会话内的后台软件，不是 Windows 系统服务。睡眠、关机或退出 Windows 登录后不能继续转发。

构建后的目录版本附带 .NET 8 和 Python 运行文件。界面需要机器上安装 Microsoft Edge WebView2 Runtime。移动软件时请保留整个项目目录，不能只复制 exe；移动后需重新设置开机自启路径。

网页浏览器入口 `http://127.0.0.1:17600` 仍可使用。桌面软件退出后该本地入口也会停止。

桌面浏览器缓存和错误日志位于 `%LOCALAPPDATA%/FrpPanel/<目录标识>/`。客户端配置和日志仍在项目 `client/` 内，备份在 `panel/backups/`。

## 构建

需要 .NET 8 SDK，运行：

先按根目录 README 下载并校验 FRP 0.71.0，将 `frpc.exe` 放入 `client/`，保留根目录 `frp_0.71.0_windows_amd64/LICENSE` 用于打包第三方许可。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/build.ps1
```

构建使用 NuGet 官方的 Microsoft.Web.WebView2 包，以及 [Python 官方 Windows 嵌入式运行文件](https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip)。WebView2 使用 [Microsoft 官方桌面嵌入 API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createasync)。

打包前还需要在 `desktop/downloads/` 准备以下文件：

- `python-3.12.10-embed-amd64.zip`：运行 `build.ps1` 会自动下载。
- `MicrosoftEdgeWebview2Setup.exe`：[Microsoft 官方 Bootstrapper](https://go.microsoft.com/fwlink/p/?LinkId=2124703)。构建时校验 Microsoft 数字签名。
- `ChineseSimplified.isl`：[Inno Setup 官方语言文件](https://github.com/jrsoftware/issrc/blob/main/Files/Languages/ChineseSimplified.isl)。

安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)，使用 `desktop/build-installer.ps1 -Compiler '安装路径/ISCC.exe'` 指定编译器。生成的 EXE 和 SHA-256 文件在 `release/`，不要提交运行目录与个人配置。

## 验证

后端测试：`desktop/app/python/python.exe -m unittest discover -s panel/tests -v`。
桌面验证包括实际打开、关闭到托盘、后台接口持续响应、重复启动唤回原实例，以及退出时停止后台。
开机登录行为需要在用户开启自启并重新登录后验证。
