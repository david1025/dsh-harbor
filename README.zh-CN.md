<p align="center">
  <img src="DSHHarbor/Assets/dsh-harbor-logo.png" alt="DSH Harbor Logo" width="180">
</p>

# DSH Harbor

[English](README.md)

> [!IMPORTANT]
> 本项目是由社区贡献者维护、基于 DeepSeek Harness 构建的开源项目。**本项目并非 DeepSeek 官方产品，未获得 DeepSeek 官方背书，也不代表 DeepSeek 官方立场。**

DSH Harbor 是一个非官方的 Windows 桌面启动器，用于运行 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)。它会自动安装和管理本地 Harness 运行环境，在回环地址上启动 Web 服务，并通过内置 WebView2 窗口显示界面。

项目的目标很直接：让 DeepSeek Harness 像普通 Windows 应用一样易于安装和使用，无需用户手动安装 Node.js、配置 pnpm 或管理终端进程。

## 主要特性

- **一键安装**——首次启动时自动下载并准备 Node.js 和 DeepSeek Harness。
- **内置 Web 界面**——在本机启动 Harness Web 服务，并显示在原生 WPF 窗口中。
- **Node.js 自动选择**——优先使用兼容的系统 Node.js；未检测到时自动下载并校验内置运行时。
- **国内镜像支持**——通过阿里云 npmmirror 安装 npm 软件包。
- **Windows 模块兼容布局**——将 `DSH_HOME` 放在 hoisted Harness 安装目录下，即使 Windows 阻止 profile fallback junction，Node 仍可解析真实软件包。
- **系统托盘运行**——关闭窗口时隐藏到托盘，可从托盘菜单重新显示或彻底退出。
- **可视化启动诊断**——显示安装和启动进度；失败时提供重试和打开日志目录操作。
- **可靠清理子进程**——使用 Windows Job Object，在退出 DSH Harbor 时一并终止 Harness 服务及其子进程。
- **安全的版本检查**——每天最多检查一次 Harness 新版本，同时继续使用经过测试的固定版本。

## 系统要求

- Windows 10 或 Windows 11，兼容 x64
- 首次启动时能够访问互联网
- 安装程序需要管理员权限
- Microsoft Edge WebView2 Runtime
- .NET 10 Desktop Runtime

安装程序会检测 WebView2 和 .NET Desktop Runtime，并在缺失时自动下载。

## 安装方法

1. 从仓库的 **Releases** 页面下载最新的 `DSHHarborSetup-*.exe`。
2. 运行安装程序。
3. 从开始菜单或桌面快捷方式启动 **DSH Harbor**。
4. 等待首次初始化完成。安装 Harness 后，应用可能会自动重启一次。

当前版本首次初始化时会安装经过测试的 `@deepseek-ai/dsh@0.1.1-rc.2`，后续启动会复用本地安装。

> 安装程序目前没有 Authenticode 数字签名。在加入代码签名证书之前，Windows SmartScreen 可能显示“未知发布者”警告。

## 工作原理

```text
DSH Harbor
  ├─ 选择兼容的系统 Node.js 或内置 Node.js
  ├─ 使用 pnpm 安装固定版本的 DeepSeek Harness
  ├─ 准备兼容 Windows 的 DSH_HOME 模块布局
  ├─ 在可用的 127.0.0.1 端口启动 `dsh web`
  └─ 通过 WebView2 显示本地服务
```

本地 Web 服务使用动态选择的回环端口。DSH Harbor 会等待服务就绪，然后才显示浏览器界面。

### 为什么 `DSH_HOME` 位于 Harness 目录内部？

DeepSeek Harness 使用目录 junction 维护扁平的 `profiles/node_modules` fallback。某些受限或非交互式 Windows 启动环境会将这些 junction 判定为不受信任的装入点，进而导致所有 profile 插件出现 `ERR_MODULE_NOT_FOUND`。

DSH Harbor 将有效 Home 放在 `harness\home`。这样，真实的 hoisted `harness\node_modules` 会进入 Node.js 的父目录查找路径，不再完全依赖 junction，同时仍然只加载一份 Harness 软件包。旧版本的 `dsh-home` 数据会自动迁移。

## 本地数据和日志

DSH Harbor 将运行数据保存在：

```text
%LOCALAPPDATA%\DSHHarbor
```

从原 **DSH Desktop** 名称升级的安装会继续使用旧的 `%LOCALAPPDATA%\DSHDesktop` 目录。这是有意保留的兼容行为：它可以保存现有 profiles，并避免移动正在使用的 Harness 安装。全新安装使用上面的 DSH Harbor 路径。

| 路径 | 用途 |
| --- | --- |
| `harness\` | 由 pnpm 管理的 DeepSeek Harness 安装目录 |
| `harness\home\` | 当前 `DSH_HOME`、profiles 和 Harness 用户数据 |
| `runtime\` | 未找到兼容系统 Node.js 时使用的内置 Node.js |
| `cache\` | 下载缓存 |
| `logs\launcher.log` | 安装、初始化和桌面启动器诊断日志 |
| `logs\dsh.log` | Harness 进程输出和启动错误 |
| `update-state.json` | 最近一次 Harness 版本检查时间 |

启动失败时，可以直接通过错误页面打开日志目录。

## 故障排查

### 关闭窗口后应用不见了

关闭主窗口只会将 DSH Harbor 隐藏到系统托盘。双击托盘图标可以重新打开；托盘菜单还可从 GitHub Releases 检查版本更新、查看包含 Harness 与 Node.js 实际版本的“关于”信息，或选择“退出”来彻底停止 Harness。

### 首次启动失败

1. 点击错误页面上的“打开日志目录”。
2. 首先检查 `launcher.log`，然后检查 `dsh.log`。
3. 确认 npm 镜像和 Node.js 下载地址可以访问。
4. 解决日志中报告的问题后点击“重试”。

提交问题时，请同时附上两份日志，并在公开前删除密钥、凭据和个人信息。

### Harness 插件报告 `ERR_MODULE_NOT_FOUND`

0.1.8 及更高版本使用前述祖先 `node_modules` 布局。若从旧版本升级，请安装最新版本，并让 DSH Harbor 自动迁移旧 Home，无需手动删除用户数据。

## 从源码构建

### 构建要求

- Visual Studio 2022，或带 Windows 桌面开发工具的 .NET 10 SDK
- Microsoft Edge WebView2 Runtime
- 使用当前安装器配置时需要 Windows x64

### 构建和运行

```powershell
dotnet build .\DSHHarbor\DSHHarbor.csproj
dotnet run --project .\DSHHarbor\DSHHarbor.csproj
```

### 创建 Release 发布目录

```powershell
dotnet publish .\DSHHarbor\DSHHarbor.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o .\artifacts\win-x64
```

### 构建安装程序

仓库中包含当前打包流程使用的 Inno Setup 命令行编译器：

```powershell
.\tools\InnoSetup\ISCC.exe .\installer\DSHHarbor.iss
```

生成的安装程序位于 `artifacts\installer\`。

## 项目结构

| 目录 | 说明 |
| --- | --- |
| `DSHHarbor/` | WPF 应用和运行环境初始化逻辑 |
| `installer/` | Inno Setup 安装器配置 |
| `tools/InnoSetup/` | 打包流程使用的安装器编译工具 |
| `artifacts/` | 本地发布和安装器输出，不属于源代码 |

## 更新内置 Harness 版本

Harness 版本会有意固定在 `DSHHarbor/DshRuntime.cs` 中。修改版本前请完成：

1. 更新固定的软件包版本。
2. 测试完全干净的首次启动。
3. 同时测试全新的 `%LOCALAPPDATA%\DSHHarbor` 目录，以及使用旧 `%LOCALAPPDATA%\DSHDesktop` 目录的升级场景。
4. 验证 Web 界面启动、托盘退出、重试逻辑以及两份日志。
5. 发布新的安装器版本。

当前版本的每日 registry 检查只提供版本信息，不会静默替换经过测试的软件包。

## 参与贡献

欢迎提交问题和 Pull Request。报告启动问题时，请提供：

- Windows 版本
- DSH Harbor 版本
- Node.js 是检测到的系统版本还是自动下载的版本
- 已删除凭据和个人信息的 `launcher.log` 与 `dsh.log`
- 从全新安装或升级安装开始的复现步骤

请尽量保持改动聚焦，并同时验证首次安装和后续启动。

## 开源许可证

DSH Harbor 使用 [MIT License](LICENSE) 开源。

## 致谢

- [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)
- [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
- [pnpm](https://pnpm.io/)
- [Inno Setup](https://jrsoftware.org/isinfo.php)

## 免责声明

DSH Harbor 是由社区贡献者维护、基于 DeepSeek Harness 构建的开源项目。本项目并非 DeepSeek 官方产品，未获得 DeepSeek 官方背书，也不代表 DeepSeek 官方立场。DeepSeek 和 DeepSeek Harness 的相关权利归各自所有者所有。
