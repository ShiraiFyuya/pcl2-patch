# 实现说明

这份文档面向想了解原理或自己动手适配新版本的人。普通使用只需要看 [README](../README.md)。

## 仓库结构

```
pcl2-patch/
├── src/
│   ├── Pcl2Patch.Core/       补丁引擎（不依赖 UI）
│   │   ├── Ctx.cs            程序集加载、宽松解析器、IL 辅助
│   │   ├── Patches.cs        五个补丁
│   │   └── Patcher.cs        流程编排（探测 / 应用 / 回读 / 备份）
│   └── Pcl2Patch.Gui/        WPF 界面
├── docs/                     截图与本文件
├── .github/workflows/        Release 自动构建
├── LICENSE                   GPL-3.0
└── pcl2-patch.sln
```

引擎基于 [Mono.Cecil](https://github.com/jbevain/cecil)：读入目标程序集，在内存里改 IL / 元数据，再写回新文件；**仓库里不含 PCL 的任何原始字节**。

## 五项改动

| # | 内容 | 位置 |
| --- | --- | --- |
| 1 | 恢复版本列表加载器 | `PageDownloadInstall.LoaderInit` |
| 2 | 恢复整合包安装分派 | `ModModpack.ModpackInstall(File, InstanceName, Logo)` |
| 3 | 资源页版本行改为「安装」 | `PageDownloadResourceDetail.Save_Click` |
| 4 | 屏蔽静默自动更新 | `ModSecret.UpdateStart(bool Silent, …)` |
| 5 | 更新方式默认值改为「不更新」 | `Settings` 静态构造 |
| 6 | 恢复「原版游戏」入口（仅 2.99.0.1+） | `PageDownloadLeft..ctor` |
| 7 | 恢复启动页/空状态的下载入口（仅 2.99.0.1+） | `PageLaunchLeft.RefreshButtonsUI` · `PageSelectRight` |
| 8 | PCL-CE：解除地区限制 | `PCL.Core.dll` · `RegionAssessment.IsRestricted` |

## 定位方式

不写死文件偏移、不写死原始字节，全部按「类型名 + 方法名 + 签名 + IL 结构 + 数据流」定位：

- 版本列表加载器：找 `PageLoaderInit` 调用前最后一个 `newobj LoaderTask<,>`，把整段表达式替换成 `ldsfld ModDownload::DlClientListLoader`；
- 安装分派：读 `ModpackInstall` 结尾那个 `throw`，把它换成对新增 `DispatchModpackInstall` 的调用；
- 局部变量（归档对象 / 计数器 / 返回值）：按「`OpenZip` 的返回值落到哪个 `stloc`」「`ldc.i4.m1` 初始化的是哪个局部」推断，不依赖序号；
- `ResourceTypes.Modpack` 的数值：从「创建版本列表那个方法里已有的比较」现读，不硬编码。

## 安全性设计

- **每个补丁独立**：一项定位失败不影响其它项，界面会标出原因。
- **双段自检**：写入前探测一次；写入后重新加载文件再探测一次，全部通过才落盘。
- **不碰原文件**：先复制到临时文件操作，成功后备份再替换。
- **拒绝重复打补丁**：已处理过的文件会直接判定为「无需处理」。

## 适配新版本

PCL 每发一个新版本，都要做一次**适配**：确认五个补丁还能定位到对应代码，必要时调整锚点。界面上失配的那一项会直接说明原因，例如：

```
[!] Minecraft 版本下载   PageLoaderInit 前找不到 LoaderTask<,> 构造
```

适配时只需要改 `src/Pcl2Patch.Core/Patches.cs` 里对应的那个 `static PatchOutcome Xxx(...)`：

1. 用 dnSpy / ILSpy 打开新版 PCL，看同类方法的结构；
2. 调整定位条件（通常只是换一个锚点指令或调用）；
3. 应用逻辑（`Apply`）一般不用动。

补丁按版本挂载：`2.99.0.1` 起新增了「隐藏下载入口」的改动，因此该版本会多打一项「下载入口」；旧版本则显示为跳过。每个补丁彼此独立，约 80 行。

## 构建与发布

```bat
dotnet build pcl2-patch.sln -c Release

dotnet publish src\Pcl2Patch.Gui\Pcl2Patch.Gui.csproj -c Release -o dist
```

推 `v*` tag 会触发 `.github/workflows/release.yml`，自动编译单文件并挂到 Release。
## PCL-CE 支持

PCL-CE（社区版）是 C# 重写的分支，限制逻辑和原版完全不同：它不靠编译期常量，而是**运行期判定地区**。

```
PCL.Core.App.Region.RegionPolicy.Evaluate(ReadWindowsSignals())
    -> RegionAssessment(Classification, Reason)
        -> IsRestricted => Classification is Mainland or Unknown
```

下载、整合包安装、版本修复等入口都会先过 `RestrictedFeaturePolicy.Check(...)`，最终都落到 `IsRestricted`。所以只要让 `IsRestricted` 恒为 `false`，整条限制链就失效了。

### 难点：发布物是 .NET 单文件包

CE 的 exe 是 apphost + bundle（Bundle v6），内嵌 93 个托管程序集，不能直接当程序集读写。处理方式：

1. 按 bundle 清单（位于文件末尾）找到内嵌的 `PCL.Core.dll` 的偏移与长度；
2. 取出这段字节，用 Cecil 读成模块，定位 `RegionAssessment::get_IsRestricted`；
3. 把方法体改成 tiny header + `ldc.i4.0` + `ret` —— **只占 3 字节，比原方法短**，所以不需要重写 bundle；
4. 把改过的 DLL 字节按原偏移写回 exe（等长覆盖），再回读自检。

整个 exe 大小、其它程序集、清单偏移都不变，因此不会破坏单文件包结构。

### 版本判定

`PCL.Core.dll` 里没有 `RegionAssessment` 时（早期 CE 没有地区限制），该项显示为「跳过」，不会报错。
