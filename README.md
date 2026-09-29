# SVN ChangeList View

按 SVN changelist 分组显示工作副本待提交文件的 Visual Studio 工具窗口，UI 仿 Visual Studio 自带的 Pending Changes。

## 功能

- **三组折叠视图**：`Changes` / `Changelist "ignore-on-commit"` / `Unversioned`，组内文件按目录树状浏览（面包屑式单行，不可折叠）。
- **ignore-on-commit 完整显示**：使用 `svn status --xml --cl ignore-on-commit` 单独查询，包含未改动成员，可正常移回。
- **状态栏徽标**：VS 窗口右下角显示待提交文件数量，点击直接打开本工具窗口。
- **解决方案资源管理器右键「SVN Show Log」**：在 项目 / 文件 / 文件夹 / 解决方案 节点右键菜单直接打开 TortoiseSVN 日志窗口（支持多选，多路径用 `*` 合并）。
- **聚焦解决方案目录**：只显示解决方案所在工作副本的内容，不层层展开磁盘目录。

## 环境要求

- Visual Studio 2022 / 2026（Community / Professional / Enterprise）
- .NET Framework 4.7.2+
- TortoiseSVN（需要其中的 `svn.exe` 命令行工具）

## 使用

1. 打开包含 SVN 工作副本的解决方案。
2. 菜单「视图 → 其他窗口 → SVN ChangeList View」打开工具窗口。
3. 点击状态栏右下角的数字徽标可快速打开。

## 数据来源

- `svn status --xml`：本地改动与未版本控制文件。
- `svn status --xml --cl ignore-on-commit`：ignore-on-commit changelist 成员（含未改动成员）。

## 版本历史与回滚

本项目用 git 管理，每个可安装版本都打了标签：

| 标签 | 说明 |
| --- | --- |
| v1.4.1 | 首个稳定版本（工具窗口 + 状态栏徽标） |
| v1.4.2 | 新增右键「SVN Show Log」；InstallationTarget 扩展到 VS2026 |
| v1.4.3 | Show Log 菜单可见性修复 |

回滚到某个历史版本（例如 v1.4.2）：

```bash
git tag                      # 查看可用版本
git checkout v1.4.2          # 切到该版本源码
# 重新构建后安装；回滚前记得把未提交改动先 git stash 或提交
git checkout master          # 回到主线
```

> 注：`.gitignore` 已排除 `bin/`、`obj/`、`backups/`、`*.vsix`，仓库只保存源码；
> 安装包请从 Releases 页面下载，或本地 `dotnet build -c Release` 后打包。

## 许可证

MIT（作者：Marvin），详见 LICENSE.txt。
