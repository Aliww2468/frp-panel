# 桌面程序构建、验证与发布说明

本文面向 FRP Panel 的维护者和源码使用者，适用于 1.3.0。普通用户请阅读 [安装指南](../docs/installation.md) 和 [用户操作手册](../docs/user-guide.md)。除另有说明外，命令均在项目根目录的 PowerShell 中执行。

## 1. 目录和职责

| 文件或目录 | 职责 |
| --- | --- |
| `FrpPanel.csproj` | .NET 8 Windows 桌面项目、程序版本及 WebView2 包引用 |
| `Program.cs` | 工作目录解析、后台启动、WebView2 窗口、托盘、自启动及更新交接 |
| `SingleInstance.cs` | 当前 Windows 会话的单实例互斥与旧版进程兼容检测 |
| `UpdateService.cs` | GitHub 正式发行查询、下载地址限制及摘要校验 |
| `UpdateDialog.cs` | 手动检查、下载进度、更新说明和安装确认窗口 |
| `build.ps1` | 构建本机目录版本，准备 Python，创建项目快捷方式 |
| `build-installer.ps1` | 在独立白名单目录中准备发行文件，编译安装程序及摘要文件 |
| `installer.iss` | Inno Setup 安装、运行环境检测、更新交接及卸载处理 |
| `legacy-runtime-files.iss` | 明确列出的旧版内置 .NET 文件清理清单 |
| `remove-legacy-runtime.ps1` | 源码目录构建时应用上述清理清单 |
| `PACKAGE-README.txt` | 随后续构建安装到根目录 `README.txt` 的完整纯文本说明书 |
| `tests/` | 单实例、更新服务与安装交接检查项目 |
| `app/` | 本机运行版本的输出目录，不提交到 Git |
| `downloads/`、`tools/`、`package/` | 构建缓存、工具及隔离发行目录，不提交到 Git |

桌面程序通过回环 HTTP 访问 Python 后台。启动时先取得单实例锁，再确定工作目录和端口；后台身份检查会核对应用名称和工作目录，避免直接接管其他目录的服务。

## 2. 构建环境

准备 Windows x64、PowerShell、.NET 8 SDK，并允许 NuGet 还原。实际运行桌面窗口还需要 .NET 8 Desktop Runtime x64 和 Microsoft Edge WebView2 Runtime。

当前项目使用 `net8.0-windows`，引用 `Microsoft.Web.WebView2 1.0.4191.47`。运行目录采用依赖系统共享运行时的发布方式，即 `--self-contained false`，不再内嵌完整 .NET。

按 [客户端目录说明](../client/README.md) 准备 `client/frpc.exe`，并保留根目录 `frp_0.71.0_windows_amd64/LICENSE`，用于发行包中的第三方许可证。

构建前先从托盘退出目标目录的桌面软件。构建会写入该目录的程序文件，不应对正在使用的可执行文件进行覆盖。

## 3. 构建本机目录版本

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./desktop/build.ps1
```

脚本生成应用图标，调用 `dotnet publish` 输出至 `desktop/app`，按明确清单清理旧版 .NET 文件，并准备 Python 3.12.10 Windows 嵌入式运行文件。Python 压缩包不存在时从 Python 官方地址下载，存在时复用缓存；脚本还会检查所需标准库是否可导入。

当前 Python 准备流程不包含独立的官方摘要比对步骤。不能将 Python 的成功下载或导入检查描述成已完成与 Microsoft Bootstrapper 相同的签名验证。

构建成功后可使用项目根目录生成的“FRP 面板.lnk”或“启动软件.cmd”。这两个入口运行 `desktop/app/FrpPanel.exe`，沿用当前项目的 `client` 和 `panel` 数据。

### 3.1 桌面启动参数

| 参数 | 作用 |
| --- | --- |
| `--workspace <目录>` | 指定包含 `panel/server.py` 的工作根目录；正常运行还需要 `client` 中的程序和配置 |
| `--port <端口>` | 指定面板回环端口，桌面入口接受 1024–65535，默认 17600 |
| `--background` | 初始化后隐藏至托盘，不自动启动 FRPC |

在项目根目录使用自定义端口的示例：

```powershell
./desktop/app/FrpPanel.exe --workspace . --port 17601
```

未指定工作目录时，程序从可执行文件所在目录向上查找 `panel/server.py` 和 `client`。单实例范围不随工作目录或端口变化，因此不能通过换端口启动第二个桌面实例。

托盘自启动项保存可执行文件路径、工作目录和 `--background`，目前不保存自定义端口。可执行文件与工作目录分离时，不支持通过标准安装器在线更新，应手工更新正确的文件位置。

## 4. 准备安装包依赖

安装包构建需要 Inno Setup 6。编译器默认路径为 `desktop/tools/inno/ISCC.exe`，可通过 `-Compiler` 指定实际位置。

`desktop/downloads` 中需要预先准备：

| 文件 | 来源与用途 |
| --- | --- |
| `python-3.12.10-embed-amd64.zip` | [Python 官方嵌入式运行文件](https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip)，运行 `build.ps1` 会准备 |
| `MicrosoftEdgeWebview2Setup.exe` | [Microsoft 官方 Bootstrapper](https://go.microsoft.com/fwlink/p/?LinkId=2124703)，安装目标机器缺少 WebView2 时使用 |
| `ChineseSimplified.isl` | [Inno Setup 官方简体中文语言文件](https://github.com/jrsoftware/issrc/blob/main/Files/Languages/ChineseSimplified.isl) |

Inno Setup 可从 [官方网站](https://jrsoftware.org/isinfo.php) 获取。构建脚本检查 WebView2 Bootstrapper 的 Authenticode 状态及 Microsoft 签发主体，未通过时终止。

.NET 安装程序无需手工预置。脚本读取 Microsoft .NET 8 官方发布元数据，选择稳定的 Windows Desktop x64 安装程序，下载或复用缓存，并校验官方 SHA-512 和 Microsoft 数字签名。随后将固定 HTTPS 下载地址及 SHA-256 写入本次安装器；运行时安装程序不会嵌入最终发行包。

## 5. 构建正式安装包

默认版本构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./desktop/build-installer.ps1
```

编译器位于其他目录时：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./desktop/build-installer.ps1 -Compiler 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe' -Version 1.3.0
```

以上编译器路径是常见安装位置示例，应以本机实际路径为准。版本参数只接受 `主版本.次版本.修订版本` 三段数字。

成功后生成：

- `release/FRP-Panel-Setup-1.3.0-x64.exe`：安装程序。
- `release/FRP-Panel-Setup-1.3.0-x64.exe.sha256`：安装程序摘要及文件名。
- `desktop/package/<随机标识>/`：本次白名单发行目录，具体路径由脚本输出。

脚本只复制桌面发布文件、Python、面板后台与静态资源、FRPC、许可文件及 `PACKAGE-README.txt`。不会复制开发目录中的 `frpc.toml`、令牌、日志或备份。不要改为直接压缩整个项目目录发布。

`build-installer.ps1` 会独立发布桌面程序到本次发行目录，不会自动更新 `desktop/app` 中供开发者直接运行的版本。需要刷新本机目录版本时，另行运行 `build.ps1`。

## 6. 安装器行为

正式安装器使用固定 AppId，默认安装到 `%LOCALAPPDATA%/Programs/FrpPanel`，应用本身按当前用户安装。缺少 .NET 8 Desktop Runtime x64 时，下载并核对构建时固定的 SHA-256，再请求管理员权限执行 Microsoft 安装程序；已有合格运行环境时跳过。

检测同时关注 .NET 桌面与核心运行时，普通 .NET Runtime、x86 或其他主版本不能替代。缺少 WebView2 时调用已通过构建阶段签名检查的官方 Bootstrapper。下载、安装或检测失败时返回错误，不继续部署缺少必要环境的应用。

旧版内置 .NET 的清理依照 `legacy-runtime-files.iss` 中的精确文件列表，构建时检查清理名单是否会误删本次新发布文件。不得扩展为递归删除用户目录。

卸载保留运行后生成的配置、日志与备份，清理属于该可执行路径的用户登录自启动项，不卸载共享 .NET 和 WebView2。普通覆盖安装前需要正常退出应用，安装器不会主动强制结束相关进程。

## 7. 在线更新协议与发布约束

更新服务固定查询本仓库 `/releases/latest`。可接受版本为 `v主版本.次版本.修订版本`，且必须高于当前版本；草稿和预发布版不进入更新流程。

发行附件必须同时提供以下精确名称：

```text
FRP-Panel-Setup-X.Y.Z-x64.exe
FRP-Panel-Setup-X.Y.Z-x64.exe.sha256
```

摘要文件内容为 SHA-256 十六进制摘要、空白分隔和对应安装程序文件名。不得发布摘要对应另一安装程序的混合附件。

更新只接受本仓库的固定格式附件地址及允许的 GitHub HTTPS 下载节点，安装程序限制为 128 MiB。下载会核对期望长度、摘要文件及 GitHub 资产摘要（存在时），失败或取消时清理未完成的文件。软件安装前再次核对摘要与程序版本信息。

桌面与安装器使用随机命名事件完成准备、提交和取消交接。安装器持有旧桌面与后台的进程句柄；收到提交并确认旧进程退出后，取得单实例锁，再修改安装文件。安装成功后释放互斥锁并重新启动软件，转发需要用户手动恢复。

该流程不提供整目录事务回滚。安装准备阶段的取消和超时会阻止继续安装；实际文件安装阶段发生磁盘或系统故障时，应按安装日志排查并手工修复，不能承诺任何阶段失败都自动恢复旧版。

## 8. 验证方法与证据边界

### 8.1 后端测试

```powershell
./desktop/app/python/python.exe -m unittest discover -s panel/tests -v
```

也可使用已安装的 Python 3.11+。测试覆盖初次启动不覆盖已有配置、配置导入及备份、认证处理、配置编辑、桌面后台身份与退出接口等。完整 FRP 生命周期和转发测试依赖本地测试 FRPS；缺少该文件时会跳过，应在报告中保留跳过原因。

### 8.2 桌面逻辑检查

```powershell
dotnet run --project ./desktop/tests/SingleInstanceChecks/SingleInstanceChecks.csproj -c Release
dotnet run --project ./desktop/tests/UpdateChecks/UpdateChecks.csproj -c Release
```

单实例检查覆盖并发启动、不同工作目录、重复实例退出、异常退出后的互斥恢复和旧版检测。更新检查覆盖版本比较、附件与 URL 限制、摘要解析、损坏下载、重定向及取消等路径。

真实读取 GitHub 正式发行并下载安装包进行校验：

```powershell
dotnet run --project ./desktop/tests/UpdateChecks/UpdateChecks.csproj -c Release -- --live
```

`--live` 只检查和下载校验，不执行安装，也不证明更新窗口及安装后自动重新打开已通过真实桌面验证。

### 8.3 安装交接检查

先使用 `installer.iss` 编译独立测试安装器。需传入与正式构建相同的 `PayloadDir`、`ReleaseDir`、`AppVersion`、`DotNetUrl` 和 `DotNetHash` 定义，并额外传入 `/DInstallerTest=1`。发行目录与测试输出目录应分开。

`InstallerTest` 使用不同 AppId，并禁止安装完成后启动正式桌面窗口。测试包不得上传为正式安装包。随后向测试程序传入测试安装器的完整路径和全新的隔离目录：

```powershell
dotnet run --project ./desktop/tests/InstallerHandoffChecks/InstallerHandoffChecks.csproj -c Release -- 'C:/FRP-Test/FRP-Panel-Setup-1.3.0-x64.exe' 'C:/FRP-Test/fresh-fixture'
```

示例路径须替换为本机准备好的测试路径，不能指向日常使用的安装目录。测试会创建配置及占位进程，验证取消时不安装、等待旧进程退出、成功部署及配置保留。该测试不执行正式安装器的安装后自动启动步骤。

### 8.4 需要实际环境验证的行为

正式发布前应分别记录实际桌面打开、主题、关闭到托盘、恢复窗口、重复启动提示、后台启停及正常退出的验证结果。运行环境自动补装应在缺少指定运行时的干净 Windows 环境测试，不能以已安装 .NET 的机器替代。

自启动需要用户登录验证；更新窗口及自动重新打开需要真实桌面流程验证；外网转发需要真实业务访问验证。测试通过、跳过和未测试应分开记录，不使用固定历史测试数量代替本次结果。

## 9. GitHub 发布流程

1. 检查工作区差异，确认代码、文档和版本号相互一致。新软件版本需同步项目版本、安装器默认值和构建脚本默认值；`-Version` 覆盖只作用于本次安装包发布。
2. 更新使用文档和纯文本说明书，再执行与修改相关的验证。
3. 构建安装包，检查独立发行目录无个人配置、凭据、日志和备份，核对 EXE 与摘要。
4. 使用明确的文件清单暂存修改，检查暂存差异，不强制添加被忽略的运行数据。
5. 提交并推送源码，使用该提交创建版本标签及正式 GitHub Release。
6. 上传 EXE 和对应 `.sha256`，填写功能变化、操作影响、升级方法及实际验证范围。
7. 回读正式发行，核对标签、附件名称、大小、上传状态和摘要。需要作为在线更新目标的版本应设为最新正式发行。

可使用已登录 GitHub CLI 的 `gh release create` 或 GitHub 网页发布。通过 CLI 填写多段发行说明时，将正文保存到 UTF-8 文件并使用 `--notes-file`，避免命令行换行和引号错误。不要将访问令牌写进命令、说明书或仓库。

仅修订文档时，可以提交文档并在对应 Release 附加单独的说明书，不需要修改应用版本或重新打包同版本 EXE。已经发布的安装包内部说明不会因仓库文档更新而变化；如需把新版说明内嵌到安装程序，应随下一次正式软件构建发布。

## 10. 文档维护约定

根目录 README 负责项目介绍和阅读导航，`docs` 负责用户指南，桌面及面板 README 负责维护细节。纯文本说明书由以下用户文档按顺序合并：安装指南、用户操作手册、故障排查指南。

更新文档后同步 `PACKAGE-README.txt`；本机 `release/使用说明.txt` 和发布的 `FRP-Panel-Manual-1.3.0.txt` 使用同一份合订本。文档中的程序行为应与代码核对，路径示例不能包含个人凭据，组件许可证保持原文。

相关文档：[项目首页](../README.md) · [网页后台说明](../panel/README.md) · [安装指南](../docs/installation.md)
