<div align="center">

<img width="112" alt="蓝色星原：旅谣图标" src="./Hi3Helper.Plugin.Azurpromilia/icon.ico" />

# Hi3Helper.Plugin.Azurpromilia

[English](./README.md) · **简体中文**

为 Windows x64 上的 [Collapse Launcher](https://collapselauncher.com/) 开发的第三方插件，支持蛮啾网络旗下 **《蓝色星原：旅谣》** 的下载、更新与启动。

[GitHub Releases](https://github.com/misaka10843/Hi3Helper.Plugin.Azurpromilia/releases) · [反馈问题](https://github.com/misaka10843/Hi3Helper.Plugin.Azurpromilia/issues)

</div>

---

> [!IMPORTANT]
> 遇到插件问题，请在本仓库提交 Issue，方便维护者跟进。

## ✨ 功能特性

- **版本检测**：检查已安装客户端是否为当前发布的版本。
- **游戏管理**：支持识别已有安装、下载、安装、启动和运行检测。获取公开游戏包时无需在插件内登录。
- **预下载**：提前暂存更新文件，对应版本正式发布后复用。
- **文件级更新**：复用已有文件，并在更新时校验和修复缺失或损坏的文件。独立的手动修复入口和游戏设置页待接入。
- **官方资讯与背景**：展示官方静态背景、动态背景、轮播、新闻、公告和活动，具体内容随官方发布更新。
- **社媒面板**：展示官方二维码和链接。官方提供网页商城和客服入口时，也会一并显示。

## 🧩 如何安装插件

请使用支持插件的 Windows x64 版 Collapse Launcher。建议使用较新的版本，以便切换静态和动态背景。

1. **下载插件**。前往 [GitHub Releases](https://github.com/misaka10843/Hi3Helper.Plugin.Azurpromilia/releases) 下载插件 ZIP，也可以从源码构建。
2. **进入插件管理**。在 Collapse Launcher 的 **设置** 页面打开插件管理菜单。
3. **添加并应用**。点击 **添加 .zip 或 manifest.json**，选择插件 ZIP，然后重启启动器。
4. **选择《蓝色星原：旅谣》**，并设置游戏安装目录。

## 🛠️ 从源码构建

安装 .NET 10 SDK。Native AOT 发布还需要 Visual Studio 的 **使用 C++ 的桌面开发**工作负载。

```powershell
git submodule update --init --recursive
dotnet build Hi3Helper.Plugin.Azurpromilia.sln -c Release -p:Platform=x64
dotnet publish Hi3Helper.Plugin.Azurpromilia -c ReleaseNoReflection -r win-x64 -p:PublishProfile=ReleaseNoReflectionPublish-O1 -o publish_output
Get-ChildItem publish_output -Filter *.pdb -Recurse -File | Remove-Item
./Indexer.exe ./publish_output
```

使用以下命令运行回归检查。在线检查会读取官方包索引，校验一个 CDN 小文件，并检查启动器内容。

```powershell
dotnet run --project tests/PublicDownloadTests -c Release
dotnet run --project tests/PublicDownloadTests -c Release -- --live
```

## ⚠️ 免责声明

本项目是独立的第三方开源插件，与蛮啾网络及 Collapse Launcher 官方无隶属关系。
