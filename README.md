# pcl2-patch

![license](https://img.shields.io/badge/license-GPLv3-blue)
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)
[![PCL2 最新](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.github.com%2Frepos%2FMeloong-Git%2FPCL%2Fcommits%3Fpath%3DPlain%2520Craft%2520Launcher%25202%2FMy%2520Project%2FAssemblyInfo.vb%26per_page%3D1&query=%24%5B0%5D.commit.message&label=PCL2%20%E6%9C%80%E6%96%B0&color=2B6CF6)](https://github.com/Meloong-Git/PCL)
[![适配](https://img.shields.io/badge/%E9%80%82%E9%85%8D-2.99.0.0%20%7C%202.99.0.1-1E9E5A)](https://github.com/ShiraiFyuya/pcl2-patch/releases/latest)

为 [Plain Craft Launcher 2](https://github.com/Meloong-Git/PCL)（PCL2）提供一份下载页与整合包安装的修复补丁。

[**⬇ 下载最新版**](https://github.com/ShiraiFyuya/pcl2-patch/releases/latest) · [实现说明](docs/TECHNICAL.md)

![pcl2-patch](docs/gui.png)

## 效果

<p align="center">
  <img src="docs/before-after.png" alt="before / after" width="100%">
</p>

## 能做什么

- **下载 Minecraft** —— 「下载 → 原版游戏」可以正常列出版本，Forge / NeoForge / Fabric / OptiFine / LiteLoader 全流程可用
- **安装整合包** —— 资源页点版本直接开始安装，不用「另存为后自己解压」
- **更新提示** —— 默认只在你手动检查时提示，不会在后台自行替换启动器文件

## 支持版本

| PCL 版本 | 状态 | 补丁项 | 说明 |
| --- | --- | --- | --- |
| `2.99.0.1` | ✅ 已适配 | 7 项 | 含「原版游戏」入口与启动页下载按钮的还原 |
| `2.99.0.0` | ✅ 已适配 | 5 项 | 另 2 项该版本没有对应改动，自动跳过 |
| 其它版本 | ⚠️ 未适配 | — | 拖进工具会逐项显示能否定位；未适配的项需要等工具更新 |

版本号取自 exe 的「文件版本」（右键 → 属性 → 详细信息），工具也会在界面顶部显示读到的是什么版本。

遇到没适配的版本，界面会把不可用的项标红并写明原因，不会写坏文件。
## 使用

先挑一个下载，两个功能、效果、产物**完全一样**，只是运行环境不同：

| 文件 | 大小 | 什么时候用 |
| --- | --- | --- |
| **`pcl2-patch-gui.exe`** | 约 300 KB | 默认选这个。用系统自带的 .NET Framework 4.8（Win10 1809+ / Win11 预装），启动快、体积小 |
| `pcl2-patch-gui-net9.exe` | 约 58 MB | 系统被精简、4.8 缺失或损坏时用。自带 .NET 9 运行时，不依赖系统组件 |

然后：

1. 打开工具，把 `Plain Craft Launcher 2.exe` 拖进窗口（或点「选择…」）；
2. 检查项全绿（2.99.0.0 为 5 项、2.99.0.1 为 7 项）后，点「打补丁」；
3. 确认即可，过程全自动：**备份 → 写入 → 自检**。

界面上的两个开关：

| 开关 | 作用 |
| --- | --- |
| 关闭静默自动更新 | 不在后台自动替换启动器，改为手动检查时提示 |
| 整合包版本行点击 → 安装 | 点版本直接安装，而不是先另存为 |

## 常见问题

**打完补丁，PCL 又更新了怎么办？**
再打一次就行。启动器更新会覆盖文件，重新打开本工具点一下即可。

**怎么还原？**
同一目录下会自动生成 `Plain Craft Launcher 2.exe.bak`，用它覆盖回去即可。

**支持哪些版本？**
见上面的[支持版本](#支持版本)表。工具会**按版本自动选择补丁**：`2.99.0.1` 起作者隐藏了若干下载入口，会额外打两项。

**要不要装 .NET？**
默认的 `pcl2-patch-gui.exe` 不用，它跑在 Windows 自带的 .NET Framework 4.8 上（Win10 1809+ / Win11 预装）。万一你的系统缺 4.8，换 `pcl2-patch-gui-net9.exe`，那个自带运行时。
**会不会改坏文件？**
不会。工具先复制副本再操作，写入前后各检查一次，中途失败不会动原文件。

## 从源码构建

```bat
git clone https://github.com/ShiraiFyuya/pcl2-patch.git
cd pcl2-patch
dotnet build pcl2-patch.sln -c Release
```

推 `v*` tag 会触发自动构建，产物见 Releases。

## 更多

实现细节、补丁清单，以及「PCL 出新版本后怎么做适配」见 [docs/TECHNICAL.md](docs/TECHNICAL.md)。

## 许可

[GPL-3.0](LICENSE) · Copyright (C) 2026 ShiraiFyuya

- 可以自由使用、修改、分发，也可以收费分发；
- 但**分发修改版时必须一并提供源码**，并沿用 GPL-3.0、保留原作者的版权声明；
- 也就是说：**改名打包、闭源拿去卖，是违反本协议的**。

发现有人违反协议转卖，欢迎 [开 issue](../../issues) 告诉我。
