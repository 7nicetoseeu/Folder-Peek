# Folder Peek 小型发版清单

这份清单只做一件事：把每次发版都要重复确认的动作固定下来，避免版本号、README、发布产物三边走散。

## 1. 发版前先确认代码状态

- 确认当前分支就是准备发版的内容
- 确认没有误提交的运行时文件、缓存文件或临时截图
- 确认 `git status --short` 里没有不该进入发版的脏改动
- 如本次发版包含用户可见变更，先整理一份简短更新说明

## 2. 更新版本号

当前版本号以 [FolderPeek.App.csproj](../FolderPeek.App/FolderPeek.App.csproj) 里的 `<Version>` 为准。

发版前：

1. 打开 `FolderPeek.App.csproj`
2. 更新 `<Version>`，例如从 `0.6.14.1` 改到 `0.6.15`
3. 保存后再次确认没有漏改成别的版本格式

建议规则：

- 常规迭代：`0.6.15`
- 小修正补发：`0.6.15.1`

## 3. 核对 README

至少核对这三项：

1. [README.md](../README.md) 里的下载文件名是否和当前版本一致
2. README 里的功能描述是否和实际实现一致
3. 如果这次新增/移除了明显功能，README 是否需要同步

特别留意：

- “直接使用发布包”里的 zip 文件名
- “整理发布包”命令说明
- 是否有已经过期的截图、版本号或交互描述

## 4. 跑最基本验证

发版前至少执行一次：

```powershell
$env:DOTNET_CLI_HOME='F:\Folder Peek\.dotnet-home'
$env:HOME='F:\Folder Peek\.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
dotnet test .\FolderPeek.sln
```

通过标准：

- 测试全部通过
- 如果出现 `NU1900` 这类 NuGet 漏洞源访问警告，可记录但不阻断当前离线发版

## 5. 生成发布产物

framework-dependent 版本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

self-contained 版本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -SelfContained
```

默认产物位置：

- staging 目录：`output/staging/<package-name>`
- zip 目录：`output/release/<package-name>.zip`

## 6. 检查 staging 是否干净

打开 `output/staging/<package-name>`，确认里面：

- 应该有：
  - `FolderPeek.App.exe`
  - `FolderPeek.App.dll`
  - `FolderPeek.App.runtimeconfig.json`
  - `Assets/`
- 不应该有：
  - `FolderPeek.settings.json`
  - `FolderPeek.theme.json`
  - `cache/`
  - 其他明显属于运行后才会生成的文件

如果出现这些文件，说明运行时目录或打包流程又混进发布目录了，先不要发。

## 7. 检查 zip 名称和模式

确认 zip 名称和目标模式一致：

- framework-dependent：`FolderPeek-v<version>-click-folder-expand-win-x64.zip`
- self-contained：`FolderPeek-v<version>-click-folder-expand-win-x64-self-contained.zip`

避免：

- 版本号和 `csproj` 不一致
- self-contained / framework-dependent 名称混淆
- 直接拿旧目录重新压包

## 8. 发版后留一条复核

发版完成后建议再做一次快速复核：

1. release 文件名是否正确
2. README 中展示的下载名是否正确
3. 本次版本对应的更新说明是否已经准备好

## 9. 最短执行版

如果只想按最短路径走，一次发版至少做这 6 步：

1. 更新 `FolderPeek.App.csproj` 里的 `<Version>`
2. 核对 README 里的下载文件名
3. 跑 `dotnet test`
4. 执行 `.\scripts\publish.ps1`
5. 检查 `output/staging` 里没有设置文件和缓存
6. 确认 `output/release` 里的 zip 文件名正确
