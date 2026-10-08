using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SvnChangelistView
{
    public sealed class FileEntry : INotifyPropertyChanged
    {
        public string Status;
        public string RelativePath;
        public string FullPath;
        public string Changelist;   // null = 不属于任何 changelist
        public bool Unversioned;

        private bool _isChecked;
        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    var h = PropertyChanged;
                    if (h != null) h(this, new PropertyChangedEventArgs("IsChecked"));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class GroupEntry
    {
        public string Name;
        public string Kind; // "changes" | "cl:<name>" | "unversioned" | "ignore"
        public List<FileEntry> Files = new List<FileEntry>();
    }

    internal static class GroupKinds
    {
        public const string Changes = "changes";
        public const string Unversioned = "unversioned";
        public const string Ignore = "ignore";

        public static string Changelist(string name) { return "cl:" + name; }
    }

    /// <summary>文件夹树节点（树状展示用）。</summary>
    public sealed class FolderEntry
    {
        public string Name;        // 本层文件夹名（最后一段）
        public string FullRelative; // 相对工作副本根的路径
        public List<FileEntry> Files = new List<FileEntry>();
        public List<FolderEntry> SubFolders = new List<FolderEntry>();
    }

    public sealed class SvnChangelistControl : UserControl, IVsSolutionEvents
    {
        private const string IgnoreChangelistName = "ignore-on-commit";
        private static readonly Brush SelectedRowBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0x00, 0x78, 0xD4));

        /// <summary>关掉 TreeViewItem 自带的系统高亮（亮蓝色，太扎眼），选中效果统一用 SelectedRowBrush 呈现。</summary>
        private static void SoftenSelectionHighlight(TreeViewItem node)
        {
            node.Resources[SystemColors.HighlightBrushKey] = Brushes.Transparent;
            node.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Brushes.Transparent;
        }

        private readonly SvnChangelistWindow _window;
        private readonly TreeView _tree;
        private readonly TextBlock _statusText;
        private readonly Button _refreshButton;
        private readonly Button _commitButton;
        private readonly Button _updateButton;
        private readonly CheckBox _autoRefreshBox;
        private readonly Button _langButton;
        private readonly DispatcherTimer _timer;

        private uint _solutionEventsCookie;
        private string _lastRoot;
        private bool _refreshBusy;
        private int _pendingChangesCount = -1;
        private int _ignoreCount = -1;

        /// <summary>忽略组（ignore-on-commit）文件数变化时通知包，用于刷新工具栏徽标。</summary>
        public static event Action<int> IgnoreCountChanged;

        /// <summary>（待提交数, 本地专用数）变化时通知包，用于刷新状态栏徽标。</summary>
        public static event Action<int, int> CountsChanged;

        private List<FileEntry> _currentFiles = new List<FileEntry>();
        private readonly HashSet<FileEntry> _selected = new HashSet<FileEntry>();
        private FileEntry _lastClicked;
        private readonly Dictionary<FileEntry, TextBlock> _rowTexts = new Dictionary<FileEntry, TextBlock>();

        public SvnChangelistControl(SvnChangelistWindow window)
        {
            _window = window;
            L10n.Load();

            _refreshButton = MakeButton("刷新", "Refresh", (s, e) => StartRefresh());
            _commitButton = MakeButton("提交…", "Commit…", (s, e) => OpenTortoise("commit", null));
            ToolTipService.SetToolTip(_commitButton, L10n.T("打开 TortoiseSVN 提交对话框（ignore-on-commit 默认不勾选）",
                "Open the TortoiseSVN commit dialog (ignore-on-commit is unchecked by default)"));
            _updateButton = MakeButton("更新", "Update", (s, e) => OpenTortoise("update", null));

            _autoRefreshBox = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                IsChecked = true
            };
            _autoRefreshBox.Click += (s, e) => ApplyLanguage();

            _langButton = MakeButton("EN", "EN", (s, e) => ToggleLanguage());
            ToolTipService.SetToolTip(_langButton, L10n.T("切换为英文", "Switch to Chinese"));

            _statusText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 2, 4, 2),
                Foreground = Brushes.DimGray,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(_refreshButton);
            header.Children.Add(_commitButton);
            header.Children.Add(_updateButton);
            header.Children.Add(_autoRefreshBox);
            header.Children.Add(_langButton);
            header.Children.Add(_statusText);

            _tree = new TreeView
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent
            };
            _tree.PreviewMouseLeftButtonDown += OnTreeLeftDown;
            _tree.MouseDoubleClick += OnDoubleClick;
            _tree.ContextMenuOpening += OnContextMenuOpening;

            var rootPanel = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            rootPanel.Children.Add(header);
            rootPanel.Children.Add(_tree);

            Content = rootPanel;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _timer.Tick += (s, e) =>
            {
                if (_autoRefreshBox.IsChecked == true)
                {
                    StartRefresh();
                }
            };
            _timer.Start();

            ApplyLanguage();
            UpdateCaption();

            Loaded += (s, e) => { AdviseSolutionEvents(); StartRefresh(); };
            Unloaded += (s, e) => { UnadviseSolutionEvents(); _timer.Stop(); };
        }

        private Button MakeButton(string zh, string en, RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = L10n.T(zh, en),
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(4, 2, 2, 2)
            };
            b.Click += onClick;
            return b;
        }

        private void ApplyLanguage()
        {
            _refreshButton.Content = L10n.T("刷新", "Refresh");
            _commitButton.Content = L10n.T("提交…", "Commit…");
            _updateButton.Content = L10n.T("更新", "Update");
            _autoRefreshBox.Content = L10n.T("自动刷新", "Auto refresh");
            _langButton.Content = L10n.Lang == "en" ? "中文" : "EN";
            ToolTipService.SetToolTip(_autoRefreshBox, L10n.T("每 10 秒自动刷新一次 SVN 状态", "Refresh SVN status every 10 seconds"));
            UpdateCaption();
            RepopulateFromCache();
        }

        private void ToggleLanguage()
        {
            L10n.Lang = L10n.Lang == "zh" ? "en" : "zh";
            L10n.Save();
            ApplyLanguage();
        }

        private void UpdateCaption()
        {
            var changes = _pendingChangesCount >= 0 ? _pendingChangesCount.ToString() : "…";
            var ignore = _ignoreCount >= 0 ? _ignoreCount.ToString() : "…";
            _window.Caption = "SVN ChangeList View  (" + L10n.T("待提交", "Changes") + ": " + changes +
                              ", " + L10n.T("本地专用", "Local-only") + ": " + ignore + ")";
        }

        private void RepopulateFromCache()
        {
            // 语言切换后用缓存数据即时重画（保留勾选与选择状态）
            if (_lastRoot != null && _currentFiles.Count > 0)
            {
                Populate(_lastRoot, _currentFiles, refreshStatus: false);
            }
        }

        // ---------- 工具定位 ----------

        private static string FindTortoiseTool(string fileName)
        {
            var candidates = new[]
            {
                @"D:\Program Files\TortoiseSVN\bin\" + fileName,
                @"C:\Program Files\TortoiseSVN\bin\" + fileName
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    return c;
                }
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                try
                {
                    var p = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(p))
                    {
                        return p;
                    }
                }
                catch
                {
                    // ignore bad PATH entries
                }
            }

            return null;
        }

        private static string FindSvnExe()
        {
            return FindTortoiseTool("svn.exe");
        }

        private static string FindTortoiseProc()
        {
            return FindTortoiseTool("TortoiseProc.exe");
        }

        // ---------- TortoiseSVN 调用 ----------

        private void OpenTortoise(string command, string pathArg)
        {
            try
            {
                var exe = FindTortoiseProc();
                if (exe == null)
                {
                    SetStatus(L10n.T("找不到 TortoiseProc.exe", "TortoiseProc.exe not found"));
                    return;
                }

                var target = pathArg;
                if (string.IsNullOrEmpty(target))
                {
                    var root = _lastRoot;
                    if (string.IsNullOrEmpty(root))
                    {
                        root = GetSolutionDirectory();
                        root = string.IsNullOrEmpty(root) ? null : root.TrimEnd('\\', '/');
                    }

                    if (string.IsNullOrEmpty(root))
                    {
                        SetStatus(L10n.T("未打开解决方案", "No solution open"));
                        return;
                    }

                    target = "\"" + root + "\"";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "/command:" + command + " /path:" + target,
                    UseShellExecute = true
                };

                if (command == "commit" || command == "update" || command == "revert")
                {
                    var proc = Process.Start(psi);
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (s, e) => Dispatcher.BeginInvoke(new Action(StartRefresh));
                }
                else
                {
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                SetStatus(L10n.T("打开 TortoiseSVN 失败: ", "Failed to open TortoiseSVN: ") + ex.Message);
            }
        }

        // ---------- svn 命令执行 ----------

        private static string RunSvn(string svnExe, string arguments, string workingDir, out string error, out int exitCode)
        {
            var psi = new ProcessStartInfo
            {
                FileName = svnExe,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Environment.CurrentDirectory : workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("svn.exe 无法启动");
                }

                var stdOut = process.StandardOutput.ReadToEndAsync();
                var stdErr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); } catch { }
                    throw new InvalidOperationException("svn 命令超时");
                }

                exitCode = process.ExitCode;
                error = stdErr.Result;
                return stdOut.Result;
            }
        }

        private string GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var serviceProvider = _window?.Package as IServiceProvider;
            var solution = serviceProvider?.GetService(typeof(SVsSolution)) as IVsSolution;
            if (solution == null)
            {
                return null;
            }

            solution.GetSolutionInfo(out string directory, out string solutionFile, out string userOptsFile);
            return directory;
        }

        private void StartRefresh()
        {
            if (_refreshBusy)
            {
                return;
            }

            try
            {
                var svnExe = FindSvnExe();
                if (svnExe == null)
                {
                    SetStatus(L10n.T("找不到 svn.exe，请确认已安装 TortoiseSVN", "svn.exe not found; please install TortoiseSVN"));
                    _tree.Items.Clear();
                    return;
                }

                var solutionDir = GetSolutionDirectory();
                if (string.IsNullOrEmpty(solutionDir))
                {
                    SetStatus(L10n.T("未打开解决方案", "No solution open"));
                    _tree.Items.Clear();
                    return;
                }

                solutionDir = solutionDir.TrimEnd('\\', '/');
                SetStatus(L10n.T("正在读取 SVN 状态…", "Reading SVN status…"));
                _refreshBusy = true;
                _ = RefreshCoreAsync(svnExe, solutionDir);
            }
            catch (Exception ex)
            {
                _refreshBusy = false;
                SetStatus(L10n.T("刷新失败: ", "Refresh failed: ") + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task RefreshCoreAsync(string svnExe, string solutionDir)
        {
            try
            {
                // 树只聚焦解决方案目录（如 D:\GFMS\code）：cwd=solutionDir、不带 target，
                // svn 返回的 entry path 就是相对该目录的路径。
                string xml = null, statusError = null;
                var statusRc = -1;
                await System.Threading.Tasks.Task.Run(() =>
                    xml = RunSvn(svnExe, "status --xml", solutionDir, out statusError, out statusRc));

                // 普通 svn status 不报告 changelist 成员（即使已修改），
                // ignore-on-commit 的成员必须用 --cl 单独查询，否则该组会显示为空。
                string clXml = null;
                var clRc = -1;
                await System.Threading.Tasks.Task.Run(() =>
                    clXml = RunSvn(svnExe, "status --xml --cl " + IgnoreChangelistName, solutionDir, out _, out clRc));

                if (statusRc != 0 || string.IsNullOrWhiteSpace(xml) || !xml.Contains("<status"))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _tree.Items.Clear();
                        SetStatus("svn status 失败 (rc=" + statusRc + "): " + (string.IsNullOrWhiteSpace(statusError) ? "无输出" : statusError.Trim()));
                        _refreshBusy = false;
                    });
                    return;
                }

                var files = ParseStatusXml(xml, solutionDir);
                var clFiles = ParseChangelistXml(clXml, solutionDir, IgnoreChangelistName);

                // 合并：changelist 成员优先（覆盖普通 status 里的同名条目）
                var byPath = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    byPath[NormalizeRelPath(f.RelativePath)] = f;
                }

                foreach (var f in clFiles)
                {
                    byPath[NormalizeRelPath(f.RelativePath)] = f;
                }

                var merged = new List<FileEntry>(byPath.Values);

                await Dispatcher.InvokeAsync(() =>
                {
                    Populate(solutionDir, merged);
                    _refreshBusy = false;
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _tree.Items.Clear();
                    SetStatus(L10n.T("读取 SVN 状态失败: ", "Failed to read SVN status: ") + ex.Message);
                    _refreshBusy = false;
                });
            }
        }

        /// <summary>把 svn 返回的路径统一成相对 anchorDir 的路径（容错处理绝对路径）。</summary>
        private static string MakeRelative(string path, string anchorDir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(anchorDir))
            {
                return path;
            }

            var normalized = path.Replace('/', '\\');
            var root = anchorDir.Replace('/', '\\').TrimEnd('\\');
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var rest = normalized.Substring(root.Length).TrimStart('\\');
                if (rest.Length > 0)
                {
                    return rest;
                }
            }

            return path;
        }

        private static string NormalizeRelPath(string path)
        {
            return (path ?? "").Replace('/', '\\').ToLowerInvariant();
        }

        private static List<FileEntry> ParseStatusXml(string xml, string anchorDir)
        {
            var result = new List<FileEntry>();
            if (string.IsNullOrWhiteSpace(xml))
            {
                return result;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch
            {
                return result;
            }

            foreach (var target in doc.Root?.Elements("target") ?? Enumerable.Empty<XElement>())
            {
                foreach (var entry in target.Elements("entry"))
                {
                    var relativePath = MakeRelative((string)entry.Attribute("path"), anchorDir);
                    var wcStatus = entry.Element("wc-status");
                    if (wcStatus == null || string.IsNullOrEmpty(relativePath))
                    {
                        continue;
                    }

                    var item = (string)wcStatus.Attribute("item") ?? "";
                    var changelist = (string)wcStatus.Attribute("changelist");

                    if (item == "external" || item == "ignored")
                    {
                        continue;
                    }

                    // item="none" 表示与 BASE 完全相同（无本地改动）。
                    // 普通未改动文件 svn 不会列出；只有显式属于某个 changelist 才保留。
                    if (item == "none" && string.IsNullOrEmpty(changelist))
                    {
                        continue;
                    }

                    result.Add(new FileEntry
                    {
                        Status = item,
                        RelativePath = relativePath,
                        FullPath = Path.Combine(anchorDir, relativePath.TrimStart('\\', '/')),
                        Changelist = changelist,
                        Unversioned = item == "unversioned"
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 解析 "svn status --xml --cl &lt;name&gt;" 的输出：changelist 成员在 &lt;changelist&gt; 元素下
        /// （与 &lt;target&gt; 平级），包含未在普通 status 中出现的成员。
        /// </summary>
        private static List<FileEntry> ParseChangelistXml(string xml, string anchorDir, string changelistName)
        {
            var result = new List<FileEntry>();
            if (string.IsNullOrWhiteSpace(xml))
            {
                return result;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch
            {
                return result;
            }

            foreach (var cl in doc.Root?.Elements("changelist") ?? Enumerable.Empty<XElement>())
            {
                var name = (string)cl.Attribute("name");
                if (!string.Equals(name, changelistName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var entry in cl.Elements("entry"))
                {
                    var relativePath = MakeRelative((string)entry.Attribute("path"), anchorDir);
                    var wcStatus = entry.Element("wc-status");
                    if (wcStatus == null || string.IsNullOrEmpty(relativePath))
                    {
                        continue;
                    }

                    var item = (string)wcStatus.Attribute("item") ?? "";
                    if (item == "external" || item == "ignored")
                    {
                        continue;
                    }

                    result.Add(new FileEntry
                    {
                        Status = item,
                        RelativePath = relativePath,
                        FullPath = Path.Combine(anchorDir, relativePath.TrimStart('\\', '/')),
                        Changelist = changelistName,
                        Unversioned = item == "unversioned"
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 无 UI 实例地拉取一次 SVN 计数，供状态栏徽标在工具窗口尚未打开时也能显示待提交数量。
        /// 复用与 Populate 相同的分组口径：Changes = 未归入任何 changelist 且已版本控制的条目；
        /// ignore-on-commit = 归入 ignore-on-commit 的条目。结尾会 raise IgnoreCountChanged / CountsChanged，
        /// 由包内的 StatusBarPillController 接收并更新右下角徽标。
        /// </summary>
        public static async System.Threading.Tasks.Task<int[]> ComputeBadgeCountsAsync(string solutionDir)
        {
            var svnExe = FindSvnExe();
            if (svnExe == null || string.IsNullOrWhiteSpace(solutionDir))
            {
                IgnoreCountChanged?.Invoke(0);
                CountsChanged?.Invoke(0, 0);
                return new[] { 0, 0 };
            }

            solutionDir = solutionDir.TrimEnd('\\', '/');

            string xml = null, statusError = null;
            var statusRc = -1;
            await System.Threading.Tasks.Task.Run(() =>
                xml = RunSvn(svnExe, "status --xml", solutionDir, out statusError, out statusRc));

            string clXml = null;
            var clRc = -1;
            await System.Threading.Tasks.Task.Run(() =>
                clXml = RunSvn(svnExe, "status --xml --cl " + IgnoreChangelistName, solutionDir, out _, out clRc));

            if (statusRc != 0 || string.IsNullOrWhiteSpace(xml) || !xml.Contains("<status"))
            {
                IgnoreCountChanged?.Invoke(0);
                CountsChanged?.Invoke(0, 0);
                return new[] { 0, 0 };
            }

            var files = ParseStatusXml(xml, solutionDir);
            var clFiles = ParseChangelistXml(clXml, solutionDir, IgnoreChangelistName);

            var byPath = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                byPath[NormalizeRelPath(f.RelativePath)] = f;
            }

            foreach (var f in clFiles)
            {
                byPath[NormalizeRelPath(f.RelativePath)] = f;
            }

            var changes = 0;
            var ignore = 0;
            foreach (var f in byPath.Values)
            {
                if (f.Unversioned)
                {
                    continue;
                }

                if (string.Equals(f.Changelist, IgnoreChangelistName, StringComparison.OrdinalIgnoreCase))
                {
                    ignore++;
                }
                else if (f.Changelist == null)
                {
                    changes++;
                }
            }

            IgnoreCountChanged?.Invoke(ignore);
            CountsChanged?.Invoke(changes, ignore);
            return new[] { changes, ignore };
        }

        // ---------- 树形展示 ----------

        private Dictionary<string, bool> CaptureExpandedState()
        {
            var d = new Dictionary<string, bool>();
            foreach (TreeViewItem node in _tree.Items)
            {
                CaptureExpandedState(node, d);
            }

            return d;
        }

        private static void CaptureExpandedState(TreeViewItem node, Dictionary<string, bool> d)
        {
            // 只有顶级分组（GroupEntry）可折叠，文件夹行不可折叠，无需记录其状态
            var group = (node.Header as FrameworkElement)?.DataContext as GroupEntry;
            if (group != null)
            {
                d[group.Kind] = node.IsExpanded;
            }

            foreach (TreeViewItem child in node.Items)
            {
                CaptureExpandedState(child, d);
            }
        }

        private void Populate(string workingCopyRoot, List<FileEntry> files, bool refreshStatus = true)
        {
            var expanded = CaptureExpandedState();

            _lastRoot = workingCopyRoot;
            _currentFiles = files ?? new List<FileEntry>();
            _selected.Clear();
            _rowTexts.Clear();
            _tree.Items.Clear();

            var changesGroup = new GroupEntry { Name = L10n.T("Changes（待提交）", "Changes (to commit)"), Kind = GroupKinds.Changes };
            var unversionedGroup = new GroupEntry { Name = L10n.T("未版本控制（?）", "Unversioned (?)"), Kind = GroupKinds.Unversioned };
            var changelistGroups = new Dictionary<string, GroupEntry>();
            var changelistOrder = new List<string>();

            foreach (var file in _currentFiles)
            {
                file.IsChecked = false;

                if (file.Unversioned)
                {
                    unversionedGroup.Files.Add(file);
                }
                else if (file.Changelist == null)
                {
                    changesGroup.Files.Add(file);
                }
                else
                {
                    if (!changelistGroups.TryGetValue(file.Changelist, out var group))
                    {
                        var isIgnoreCl = string.Equals(file.Changelist, IgnoreChangelistName, StringComparison.OrdinalIgnoreCase);
                        group = new GroupEntry
                        {
                            Name = isIgnoreCl
                                ? L10n.T("Changelist \"ignore-on-commit\"", "Changelist \"ignore-on-commit\"")
                                : file.Changelist,
                            Kind = GroupKinds.Changelist(file.Changelist)
                        };
                        changelistGroups[file.Changelist] = group;
                        changelistOrder.Add(file.Changelist);
                    }

                    group.Files.Add(file);
                }
            }

            if (refreshStatus)
            {
                SetStatus(workingCopyRoot + "    (" + _currentFiles.Count + " " + L10n.T("项", "items") + ", " +
                          L10n.T("刷新于", "refreshed at ") + DateTime.Now.ToString("HH:mm:ss") + ")");
            }

            var ignoreGroup = changelistGroups.TryGetValue(IgnoreChangelistName, out var ig) ? ig : null;

            // 顺序仿 VisualSVN Pending Changes：Changes → 其他 changelist → ignore-on-commit → Unversioned
            var ordered = new List<GroupEntry>();
            if (changesGroup.Files.Count > 0)
            {
                ordered.Add(changesGroup);
            }

            foreach (var name in changelistOrder.Where(n => !string.Equals(n, IgnoreChangelistName, StringComparison.OrdinalIgnoreCase))
                                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(changelistGroups[name]);
            }

            if (ignoreGroup != null)
            {
                ordered.Add(ignoreGroup);
            }

            if (unversionedGroup.Files.Count > 0)
            {
                ordered.Add(unversionedGroup);
            }

            foreach (var group in ordered)
            {
                var node = CreateGroupNode(group, expanded);
                bool isOpen;
                if (!expanded.TryGetValue(group.Kind, out isOpen))
                {
                    isOpen = true;
                }

                node.IsExpanded = isOpen;
                _tree.Items.Add(node);
            }

            _pendingChangesCount = changesGroup.Files.Count;
            _ignoreCount = ignoreGroup != null ? ignoreGroup.Files.Count : 0;
            UpdateCaption();
            IgnoreCountChanged?.Invoke(_ignoreCount);
            CountsChanged?.Invoke(_pendingChangesCount, _ignoreCount);

            if (ordered.Count == 0 && refreshStatus)
            {
                SetStatus(workingCopyRoot + "    (" + L10n.T("工作副本干净, 刷新于 ", "clean working copy, refreshed at ") +
                          DateTime.Now.ToString("HH:mm:ss") + ")");
            }
        }

        private TreeViewItem CreateGroupNode(GroupEntry group, Dictionary<string, bool> expanded)
        {
            var isIgnore = string.Equals(group.Kind, GroupKinds.Changelist(IgnoreChangelistName), StringComparison.OrdinalIgnoreCase);

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.DataContext = group;

            var selectAllBox = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = L10n.T("全选/取消本组（用于批量提交）", "Select/deselect whole group (for batch commit)")
            };
            selectAllBox.Checked += (s, e) => { foreach (var f in group.Files) f.IsChecked = true; };
            selectAllBox.Unchecked += (s, e) => { foreach (var f in group.Files) f.IsChecked = false; };

            var nameText = new TextBlock
            {
                Text = isIgnore
                    ? group.Name + L10n.T("（本地专用，提交时不勾选）", " (local only, unchecked on commit)")
                    : group.Name,
                FontWeight = FontWeights.SemiBold
            };

            var countText = new TextBlock
            {
                Text = "  " + group.Files.Count,
                Foreground = Brushes.DimGray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };

            headerPanel.Children.Add(selectAllBox);
            headerPanel.Children.Add(nameText);
            headerPanel.Children.Add(countText);

            if (isIgnore)
            {
                nameText.Foreground = Brushes.Gray;
                nameText.FontStyle = FontStyles.Italic;
                countText.Foreground = Brushes.Gray;
            }

            var node = new TreeViewItem
            {
                Header = headerPanel
            };
            SoftenSelectionHighlight(node);

            // 仿 VisualSVN Pending Changes：只有顶级分组可折叠。
            // 含文件的目录输出一行不可折叠的面包屑行（全路径），文件挂在该行下；
            // 只含子目录的目录不单独成行，并入子目录的全路径显示。
            var rootFolder = BuildFolderTree(group.Files);
            AddFolderRows(node, rootFolder, isIgnore);
            foreach (var file in rootFolder.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                node.Items.Add(CreateFileNode(file, isIgnore));
            }

            return node;
        }

        /// <summary>递归输出文件夹行：有直接文件的目录出一行面包屑，纯目录层合并进子目录路径。</summary>
        private void AddFolderRows(TreeViewItem parent, FolderEntry folder, bool inIgnoreGroup)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            if (folder.Files.Count > 0)
            {
                parent.Items.Add(CreateFolderRow(folder, inIgnoreGroup));
            }

            foreach (var sub in folder.SubFolders.OrderBy(f => f.Name, cmp))
            {
                AddFolderRows(parent, sub, inIgnoreGroup);
            }
        }

        /// <summary>文件夹行：不可折叠（无展开箭头），显示全路径面包屑与文件数。</summary>
        private TreeViewItem CreateFolderRow(FolderEntry folder, bool inIgnoreGroup)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.DataContext = folder;

            var selectAllBox = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = L10n.T("全选/取消本文件夹", "Select/deselect this folder")
            };
            selectAllBox.Checked += (s, e) => SetFolderChecked(folder, true);
            selectAllBox.Unchecked += (s, e) => SetFolderChecked(folder, false);

            var nameText = new TextBlock
            {
                Text = folder.FullRelative.Replace("\\", " \\ ") + "  (" + CountFolderFiles(folder) + ")",
                FontWeight = FontWeights.SemiBold,
                Foreground = inIgnoreGroup ? Brushes.Gray : Brushes.Black
            };

            headerPanel.Children.Add(selectAllBox);
            headerPanel.Children.Add(nameText);

            var node = new TreeViewItem
            {
                Header = headerPanel,
                IsExpanded = true,
                // 自定义模板：无展开箭头、不可折叠，子项（文件）始终显示
                Template = FlatRowTemplate
            };
            SoftenSelectionHighlight(node);
            ToolTipService.SetToolTip(node, folder.FullRelative);

            foreach (var file in folder.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                node.Items.Add(CreateFileNode(file, inIgnoreGroup));
            }

            return node;
        }

        /// <summary>文件夹行的精简模板：头部 + 子项（固定缩进），没有展开箭头因此无法折叠。</summary>
        private static readonly ControlTemplate FlatRowTemplate = CreateFlatRowTemplate();

        private static ControlTemplate CreateFlatRowTemplate()
        {
            var template = new ControlTemplate(typeof(TreeViewItem));
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            // 模板内绑 Header 必须指向模板父级（TreeViewItem）；
            // 普通 Binding 会解析到继承的 DataContext（没有 Header 属性）导致头部空白
            header.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Header")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            panel.AppendChild(header);
            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            items.SetValue(FrameworkElement.MarginProperty, new Thickness(18, 0, 0, 0));
            panel.AppendChild(items);
            template.VisualTree = panel;
            return template;
        }

        private static void SetFolderChecked(FolderEntry folder, bool value)
        {
            foreach (var f in folder.Files)
            {
                f.IsChecked = value;
            }

            foreach (var sub in folder.SubFolders)
            {
                SetFolderChecked(sub, value);
            }
        }

        private static int CountFolderFiles(FolderEntry folder)
        {
            int n = folder.Files.Count;
            foreach (var sub in folder.SubFolders)
            {
                n += CountFolderFiles(sub);
            }

            return n;
        }

        /// <summary>
        /// 把一组文件按目录分组；每个目录占一行（面包屑式 "A \ B \ C"，仿 VisualSVN 源代码管理面板），
        /// 不再逐级嵌套，避免树过深看着头晕。根目录下的文件直接挂在 root.Files。
        /// </summary>
        private static FolderEntry BuildFolderTree(IEnumerable<FileEntry> files)
        {
            var root = new FolderEntry { Name = "", FullRelative = "" };

            foreach (var group in files
                .GroupBy(f => (Path.GetDirectoryName(f.RelativePath) ?? "").Trim().TrimEnd('\\', '/'),
                         StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var orderedFiles = group.OrderBy(f => Path.GetFileName(f.RelativePath), StringComparer.OrdinalIgnoreCase);

                if (group.Key.Length == 0)
                {
                    foreach (var f in orderedFiles)
                    {
                        root.Files.Add(f);
                    }

                    continue;
                }

                var folder = new FolderEntry
                {
                    Name = string.Join(" \\ ", group.Key.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)),
                    FullRelative = group.Key
                };
                foreach (var f in orderedFiles)
                {
                    folder.Files.Add(f);
                }

                root.SubFolders.Add(folder);
            }

            return root;
        }

        private TreeViewItem CreateFileNode(FileEntry file, bool inIgnoreGroup)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
            panel.DataContext = file;

            var checkBox = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            checkBox.SetBinding(CheckBox.IsCheckedProperty, new Binding("IsChecked")
            {
                Source = file,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });

            var statusText = new TextBlock
            {
                Text = StatusLabel(file),
                Width = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = StatusBrush(file)
            };

            var pathText = new TextBlock
            {
                // 目录已在面包屑行里显示，文件行只显示文件名
                Text = Path.GetFileName(file.RelativePath),
                Foreground = inIgnoreGroup ? Brushes.Gray : Brushes.Black
            };

            panel.Children.Add(checkBox);
            panel.Children.Add(statusText);
            panel.Children.Add(pathText);

            _rowTexts[file] = pathText;

            var node = new TreeViewItem
            {
                Header = panel
            };
            SoftenSelectionHighlight(node);

            ToolTipService.SetToolTip(node, file.FullPath);
            return node;
        }

        private static string StatusLabel(FileEntry file)
        {
            switch (file.Status)
            {
                case "modified": return "M";
                case "added": return "A";
                case "deleted": return "D";
                case "replaced": return "R";
                case "conflicted": return "C";
                case "missing": return "!";
                case "obstructed": return "~";
                case "unversioned": return "?";
                case "none": return "·";   // 属于 changelist 但未改动（如 ignore-on-commit 里的干净文件）
                default: return file.Status.Substring(0, 1).ToUpperInvariant();
            }
        }

        private static Brush StatusBrush(FileEntry file)
        {
            switch (file.Status)
            {
                case "modified": return new SolidColorBrush(Color.FromRgb(0xB0, 0x6D, 0x00));
                case "added": return new SolidColorBrush(Color.FromRgb(0x1D, 0x7A, 0x35));
                case "deleted": return new SolidColorBrush(Color.FromRgb(0xA3, 0x2D, 0x2D));
                case "conflicted": return new SolidColorBrush(Color.FromRgb(0xA3, 0x2D, 0x2D));
                case "unversioned": return new SolidColorBrush(Color.FromRgb(0x18, 0x5F, 0xA5));
                case "none": return Brushes.Gray;
                default: return Brushes.DimGray;
            }
        }

        // ---------- 多选（Ctrl / Shift 点击） ----------

        private void ApplyRowHighlight()
        {
            foreach (var kv in _rowTexts)
            {
                kv.Value.Background = _selected.Contains(kv.Key) ? SelectedRowBrush : Brushes.Transparent;
            }
        }

        private void OnTreeLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src && FindAncestor<CheckBox>(src) != null)
            {
                return; // 点勾选框不改变选择
            }

            var tvi = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            var fe = (tvi?.Header as FrameworkElement)?.DataContext as FileEntry;
            if (fe == null)
            {
                return;
            }

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (ctrl)
            {
                if (!_selected.Add(fe))
                {
                    _selected.Remove(fe);
                }
            }
            else if (shift && _lastClicked != null && _currentFiles.Contains(_lastClicked) && _currentFiles.Contains(fe))
            {
                int i1 = _currentFiles.IndexOf(_lastClicked);
                int i2 = _currentFiles.IndexOf(fe);
                _selected.Clear();
                for (int i = Math.Min(i1, i2); i <= Math.Max(i1, i2); i++)
                {
                    _selected.Add(_currentFiles[i]);
                }
            }
            else
            {
                _selected.Clear();
                _selected.Add(fe);
            }

            _lastClicked = fe;
            ApplyRowHighlight();
        }

        // ---------- 交互 ----------

        private FileEntry GetSelectedFile()
        {
            if (_tree.SelectedItem is TreeViewItem item)
            {
                return (item.Header as FrameworkElement)?.DataContext as FileEntry;
            }

            return null;
        }

        /// <summary>右键操作的目标文件集合：Ctrl 选中集 &gt; 勾选集 &gt; 当前文件。</summary>
        private List<FileEntry> MenuTargets(FileEntry clicked)
        {
            if (clicked != null && _selected.Contains(clicked) && _selected.Count > 1)
            {
                return _selected.ToList();
            }

            var checkedFiles = _currentFiles.Where(f => f.IsChecked).ToList();
            if (checkedFiles.Count > 0)
            {
                return checkedFiles;
            }

            if (clicked != null)
            {
                return new List<FileEntry> { clicked };
            }

            return new List<FileEntry>();
        }

        private List<FileEntry> GetVersionedCommitTargets(List<FileEntry> files)
        {
            return files.Where(f => !f.Unversioned).ToList();
        }

        private void CommitWithTortoise(List<FileEntry> files)
        {
            var targets = GetVersionedCommitTargets(files);
            if (targets.Count == 0)
            {
                SetStatus(L10n.T("没有可提交的文件（未版本控制文件需要先 svn add）",
                    "Nothing to commit (unversioned files need svn add first)"));
                return;
            }

            var exe = FindTortoiseProc();
            if (exe == null)
            {
                SetStatus(L10n.T("找不到 TortoiseProc.exe", "TortoiseProc.exe not found"));
                return;
            }

            string args;
            if (targets.Count == 1)
            {
                args = "/command:commit /path:\"" + targets[0].FullPath + "\"";
            }
            else
            {
                // TortoiseSVN 支持用 * 分隔的多个路径
                args = "/command:commit /path:\"" + string.Join("*", targets.Select(f => f.FullPath)) + "\"";
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = true
                };
                var proc = Process.Start(psi);
                proc.EnableRaisingEvents = true;
                proc.Exited += (s, e) => Dispatcher.BeginInvoke(new Action(StartRefresh));
                SetStatus(L10n.T("已打开 TortoiseSVN 提交对话框（", "Opened TortoiseSVN commit dialog (") +
                          targets.Count + L10n.T(" 个文件）", " files)"));
            }
            catch (Exception ex)
            {
                SetStatus(L10n.T("打开提交对话框失败: ", "Failed to open commit dialog: ") + ex.Message);
            }
        }

        private void OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var file = GetSelectedFile();
            if (file == null || !File.Exists(file.FullPath))
            {
                return;
            }

            OpenFile(file.FullPath);
        }

        private void OpenFile(string fullPath)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var serviceProvider = _window?.Package as IServiceProvider;
                if (serviceProvider != null)
                {
                    VsShellUtilities.OpenDocument(serviceProvider, fullPath);
                }
            }
            catch (Exception ex)
            {
                SetStatus(L10n.T("打开文件失败: ", "Failed to open file: ") + ex.Message);
            }
        }

        // ---------- 内置 diff（VS 内嵌对比窗口） ----------

        private void ShowBuiltInDiff(FileEntry file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var svn = FindSvnExe();
                if (svn == null)
                {
                    SetStatus(L10n.T("找不到 svn.exe", "svn.exe not found"));
                    return;
                }

                var tmp = Path.Combine(Path.GetTempPath(), "svnbase_" + Guid.NewGuid().ToString("N") + Path.GetExtension(file.FullPath));

                var psi = new ProcessStartInfo
                {
                    FileName = svn,
                    Arguments = "cat -r BASE \"" + file.FullPath + "\"",
                    WorkingDirectory = _lastRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var p = Process.Start(psi))
                {
                    using (var fs = File.Create(tmp))
                    {
                        p.StandardOutput.BaseStream.CopyTo(fs);
                    }

                    var err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(60000))
                    {
                        try { p.Kill(); } catch { }
                        SetStatus(L10n.T("svn cat 超时", "svn cat timed out"));
                        return;
                    }

                    if (p.ExitCode != 0)
                    {
                        SetStatus("svn cat: " + err.Trim());
                        try { File.Delete(tmp); } catch { }
                        return;
                    }
                }

                var serviceProvider = _window?.Package as IServiceProvider;
                var diffService = serviceProvider?.GetService(typeof(SVsDifferenceService)) as IVsDifferenceService;
                if (diffService == null)
                {
                    SetStatus(L10n.T("无法获取 VS 对比服务", "Cannot get VS diff service"));
                    return;
                }

                diffService.OpenComparisonWindow2(
                    tmp,
                    file.FullPath,
                    Path.GetFileName(file.FullPath) + L10n.T(" (与 SVN BASE 对比)", " (vs SVN BASE)"),
                    Path.Combine(L10n.T("对比", "Compare") + ": ", file.RelativePath),
                    "SVN BASE",
                    L10n.T("本地工作副本", "Local working copy"),
                    Path.GetFileName(file.FullPath) + L10n.T(" 与 BASE 的差异", " vs BASE"),
                    null,
                    0u);
            }
            catch (Exception ex)
            {
                SetStatus(L10n.T("打开对比失败: ", "Failed to open diff: ") + ex.Message);
            }
        }

        // ---------- 右键菜单 ----------

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            DependencyObject hit = Mouse.DirectlyOver as DependencyObject;
            var item = FindAncestor<TreeViewItem>(hit);

            var headerElement = item == null ? null : item.Header as FrameworkElement;
            var file = headerElement?.DataContext as FileEntry;
            var group = headerElement?.DataContext as GroupEntry;

            var menu = new ContextMenu();

            if (file != null)
            {
                var targets = MenuTargets(file);
                var versionedTargets = GetVersionedCommitTargets(targets);
                var n = targets.Count;

                menu.Items.Add(MakeItem(L10n.T("Open（打开文件）", "Open"), () => OpenFile(file.FullPath)));

                if (!file.Unversioned)
                {
                    menu.Items.Add(MakeItem(L10n.T("Show Differences（内置 diff）", "Show Differences (built-in)"),
                        () => ShowBuiltInDiff(file)));
                    menu.Items.Add(MakeItem(L10n.T("Show Differences (TortoiseSVN)", "Show Differences (TortoiseSVN)"),
                        () => OpenTortoise("diff", "\"" + file.FullPath + "\"")));
                    menu.Items.Add(MakeItem(L10n.T("Show Log...", "Show Log..."),
                        () => OpenTortoise("log", "\"" + file.FullPath + "\"")));
                    menu.Items.Add(MakeItem(L10n.T("Update（更新此文件）", "Update (this file)"),
                        () => OpenTortoise("update", "\"" + file.FullPath + "\"")));
                    menu.Items.Add(MakeItem(
                        L10n.T("Commit...（提交 ", "Commit... (") + versionedTargets.Count + L10n.T(" 个文件）", " files)"),
                        () => CommitWithTortoise(targets)));
                    menu.Items.Add(MakeItem(L10n.T("Revert Changes...（还原 ", "Revert Changes... (") + n +
                        L10n.T(" 个文件）", " files)"), () => OpenTortoise("revert",
                        "\"" + string.Join("*", targets.Select(f => f.FullPath)) + "\"")));
                    menu.Items.Add(new Separator());

                    if (string.Equals(file.Changelist, IgnoreChangelistName, StringComparison.OrdinalIgnoreCase))
                    {
                        menu.Items.Add(MakeItem(L10n.T("移出 ignore-on-commit（恢复正常提交管理）", "Remove from ignore-on-commit"),
                            () => RunChangelistBatch("--remove", targets)));
                    }
                    else
                    {
                        menu.Items.Add(MakeItem(L10n.T("加入 ignore-on-commit（本地改，不提交）", "Add to ignore-on-commit (local changes, never commit)"),
                            () => RunChangelistBatch(IgnoreChangelistName, targets)));
                    }

                    menu.Items.Add(MakeItem(L10n.T("打开所在目录", "Reveal in Explorer"), () =>
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = "/select,\"" + file.FullPath + "\"",
                            UseShellExecute = true
                        });
                    }));
                }

                menu.Items.Add(new Separator());
            }
            else if (group != null)
            {
                menu.Items.Add(MakeItem(L10n.T("全选本组", "Select whole group"), () => { foreach (var f in group.Files) f.IsChecked = true; }));
                menu.Items.Add(MakeItem(L10n.T("取消本组", "Deselect whole group"), () => { foreach (var f in group.Files) f.IsChecked = false; }));
                menu.Items.Add(MakeItem(L10n.T("Commit...（提交本组全部可提交文件）", "Commit... (all committable files in group)"),
                    () => CommitWithTortoise(group.Files)));
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(MakeItem(L10n.T("刷新", "Refresh"), StartRefresh));
            _tree.ContextMenu = menu;
        }

        private MenuItem MakeItem(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => action();
            return mi;
        }

        private void RunChangelistBatch(string clArgs, List<FileEntry> files)
        {
            try
            {
                var svnExe = FindSvnExe();
                if (svnExe == null)
                {
                    SetStatus(L10n.T("找不到 svn.exe", "svn.exe not found"));
                    return;
                }

                var root = _lastRoot ?? (GetSolutionDirectory() ?? Environment.CurrentDirectory);
                var paths = string.Join(" ", files.Select(f => "\"" + f.FullPath + "\""));
                var output = RunSvn(svnExe, "changelist " + clArgs + " " + paths, root, out var error, out var rc);

                if (rc != 0 || !string.IsNullOrWhiteSpace(error))
                {
                    SetStatus("svn: " + (string.IsNullOrWhiteSpace(error) ? output : error.Trim()));
                }
                else
                {
                    SetStatus(L10n.T("已完成: ", "Done: ") + "svn changelist " + clArgs + " (" + files.Count + ")");
                }
            }
            catch (Exception ex)
            {
                SetStatus(L10n.T("操作失败: ", "Operation failed: ") + ex.Message);
            }

            StartRefresh();
        }

        private static T FindAncestor<T>(DependencyObject from) where T : DependencyObject
        {
            while (from != null && !(from is T))
            {
                from = System.Windows.Media.VisualTreeHelper.GetParent(from);
            }

            return from as T;
        }

        private void SetStatus(string message)
        {
            _statusText.Text = message;
        }

        // ---------- 解决方案事件 ----------

        private void AdviseSolutionEvents()
        {
            if (_solutionEventsCookie != 0)
            {
                return;
            }

            var serviceProvider = _window?.Package as IServiceProvider;
            var solution = serviceProvider?.GetService(typeof(SVsSolution)) as IVsSolution;
            if (solution != null)
            {
                solution.AdviseSolutionEvents(this, out _solutionEventsCookie);
            }
        }

        private void UnadviseSolutionEvents()
        {
            if (_solutionEventsCookie == 0)
            {
                return;
            }

            var serviceProvider = _window?.Package as IServiceProvider;
            var solution = serviceProvider?.GetService(typeof(SVsSolution)) as IVsSolution;
            if (solution != null)
            {
                solution.UnadviseSolutionEvents(_solutionEventsCookie);
                _solutionEventsCookie = 0;
            }
        }

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            Dispatcher.BeginInvoke(new Action(StartRefresh));
            return VSConstants.S_OK;
        }

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _tree.Items.Clear();
                _pendingChangesCount = -1;
                UpdateCaption();
                CountsChanged?.Invoke(0, 0);
                SetStatus(L10n.T("未打开解决方案", "No solution open"));
            }));
            return VSConstants.S_OK;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStaleHierarchy, IVsHierarchy pLoadedHierarchy) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
    }
}
