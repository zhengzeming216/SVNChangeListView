using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.PlatformUI;

namespace SvnChangelistView
{
    public sealed class FileEntry : INotifyPropertyChanged
    {
        public string Status;
        public string RelativePath;
        public string FullPath;
        public string Changelist;   // null = 不属于任何 changelist
        public bool Unversioned;

        /// <summary>编辑器里有未保存修改（磁盘文件本身可能没变），显示时加 * 标记。</summary>
        public bool DirtyOnly;

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

    public sealed partial class SvnChangelistControl : UserControl, IVsSolutionEvents
    {
        private const string IgnoreChangelistName = "ignore-on-commit";
        /// <summary>关闭原生选中背景，由文件行统一绘制主题高亮。</summary>
        private static void SoftenSelectionHighlight(TreeViewItem node)
        {
            node.Resources[SystemColors.HighlightBrushKey] = Brushes.Transparent;
            node.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Brushes.Transparent;
        }

        private readonly SvnChangelistWindow _window;
        private TreeView _tree;
        private TextBlock _statusText;
        private Button _commitButton;
        private Button _updateButton;

        private uint _solutionEventsCookie;
        private string _lastRoot;
        private bool _refreshBusy;
        private bool _pendingRefresh;
        private bool _isLoaded;
        private string _solutionRoot;
        private int _solutionGeneration;
        private int _populateGeneration;
        private bool _keyboardContextMenu;
        private int _pendingChangesCount = -1;
        private int _ignoreCount = -1;

        /// <summary>忽略组（ignore-on-commit）文件数变化时通知包，用于刷新工具栏徽标。</summary>
        public static event Action<int> IgnoreCountChanged;

        /// <summary>（待提交数, 本地专用数）变化时通知包，用于刷新状态栏徽标。</summary>
        public static event Action<int, int> CountsChanged;

        /// <summary>控件请求立即刷新（例如文档保存时由包触发）。</summary>
        public static event Action RefreshRequested;

        /// <summary>触发一次刷新请求（无实例在显示时无效）。</summary>
        public static void RequestRefresh()
        {
            RefreshRequested?.Invoke();
        }

        private static int _liveInstances;

        /// <summary>工具窗口当前是否有实例在显示（打开时由控件自己刷新，包不必重复轮询）。</summary>
        public static bool HasLiveInstance
        {
            get { return _liveInstances > 0; }
        }

        private List<FileEntry> _currentFiles = new List<FileEntry>();
        private readonly HashSet<FileEntry> _selected = new HashSet<FileEntry>();
        private FileEntry _lastClicked;
        private readonly Dictionary<FileEntry, TextBlock> _rowTexts = new Dictionary<FileEntry, TextBlock>();

        public SvnChangelistControl(SvnChangelistWindow window)
        {
            _window = window;

            BuildUi();
            _tree.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnTreePreviewKeyDown), true);
            _tree.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(OnTreeNavigationKeyDown), true);

            Loaded += (s, e) =>
            {
                if (!_isLoaded) { _isLoaded = true; _liveInstances++; }
                RefreshRequested -= OnRefreshRequested;
                RefreshRequested += OnRefreshRequested;
                VSColorTheme.ThemeChanged -= OnThemeChanged;
                VSColorTheme.ThemeChanged += OnThemeChanged;
                AdviseSolutionEvents();
                ApplyRowHighlight();
                StartRefresh();
            };
            Unloaded += (s, e) =>
            {
                if (_isLoaded) { _isLoaded = false; _liveInstances--; }
                RefreshRequested -= OnRefreshRequested;
                VSColorTheme.ThemeChanged -= OnThemeChanged;
                UnadviseSolutionEvents();
            };
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

                if (command == "commit" || command == "update" || command == "revert" || command == "add")
                {
                    var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    proc.Exited += (s, e) =>
                    {
                        proc.Dispose();
                        Dispatcher.BeginInvoke(new Action(StartRefresh));
                    };
                    proc.Start();
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

        /// <summary>包在文档保存等时机请求刷新；可能来自非 UI 线程，切回 UI 线程再刷。</summary>
        private void OnRefreshRequested()
        {
            Dispatcher.BeginInvoke(new Action(StartRefresh));
        }

        private async void StartRefresh()
        {
            if (_refreshBusy)
            {
                _pendingRefresh = true;
                return;
            }

            _refreshBusy = true;
            var generation = _solutionGeneration;
            try
            {
                var solutionDir = GetSolutionDirectory();
                solutionDir = string.IsNullOrEmpty(solutionDir) ? null : solutionDir.TrimEnd('\\', '/');
                if (!string.Equals(_solutionRoot, solutionDir, StringComparison.OrdinalIgnoreCase))
                {
                    ClearSolutionView();
                    _solutionRoot = solutionDir;
                }
                generation = _solutionGeneration;
                if (string.IsNullOrEmpty(solutionDir))
                {
                    ClearSolutionView();
                    SetStatus(L10n.T("未打开解决方案", "No solution open"));
                    return;
                }

                var svnExe = FindSvnExe();
                if (svnExe == null)
                {
                    SetStatus(L10n.T("找不到 svn.exe，请确认已安装 TortoiseSVN", "svn.exe not found; please install TortoiseSVN"));
                    return;
                }

                SetStatus(L10n.T("正在读取 SVN 状态…", "Reading SVN status…"));
                await RefreshCoreAsync(svnExe, solutionDir, generation);
            }
            catch (Exception ex)
            {
                if (generation == _solutionGeneration)
                    SetStatus(L10n.T("刷新失败: ", "Refresh failed: ") + ex.Message);
            }
            finally
            {
                _refreshBusy = false;
                if (_pendingRefresh)
                {
                    _pendingRefresh = false;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartRefresh));
                }
            }
        }

        private async System.Threading.Tasks.Task RefreshCoreAsync(string svnExe, string solutionDir, int generation)
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
                        if (generation != _solutionGeneration) return;
                        SetStatus("svn status 失败 (rc=" + statusRc + "): " + (string.IsNullOrWhiteSpace(statusError) ? "无输出" : statusError.Trim()));
                    });
                    return;
                }

                if (clRc != 0 || string.IsNullOrWhiteSpace(clXml))
                    throw new InvalidOperationException("svn status --cl " + IgnoreChangelistName + " 失败 (rc=" + clRc + ")");
                if (XDocument.Parse(xml).Root?.Name.LocalName != "status" ||
                    XDocument.Parse(clXml).Root?.Name.LocalName != "status")
                    throw new InvalidOperationException("svn status 返回了无效的 XML");

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
                    if (generation == _solutionGeneration) Populate(solutionDir, merged);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (generation != _solutionGeneration) return;
                    SetStatus(L10n.T("读取 SVN 状态失败: ", "Failed to read SVN status: ") + ex.Message);
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
        /// <summary>枚举当前打开且有未保存修改的文档（相对路径，'/' 分隔），只取给定目录下的。</summary>
        internal static HashSet<string> GetDirtyDocumentPaths(string root)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(root))
            {
                return result;
            }

            var rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (rdt == null)
            {
                return result;
            }

            IEnumRunningDocuments enumDocs;
            if (!ErrorHandler.Succeeded(rdt.GetRunningDocumentsEnum(out enumDocs)) || enumDocs == null)
            {
                return result;
            }

            var cookies = new uint[32];
            while (enumDocs.Next((uint)cookies.Length, cookies, out uint fetched) == 0 && fetched > 0)
            {
                for (var i = 0; i < fetched; i++)
                {
                    IntPtr docData = IntPtr.Zero;
                    try
                    {
                        if (!ErrorHandler.Succeeded(rdt.GetDocumentInfo(cookies[i], out _, out _, out _,
                                out string fileName, out _, out _, out docData)))
                        {
                            continue;
                        }

                        var rel = ToRelativePath(root, fileName);
                        if (rel == null || docData == IntPtr.Zero)
                        {
                            continue;
                        }

                        var persist = Marshal.GetObjectForIUnknown(docData) as IVsPersistDocData2;
                        if (persist != null &&
                            ErrorHandler.Succeeded(persist.IsDocDataDirty(out var isDirty)) && isDirty != 0)
                        {
                            result.Add(rel);
                        }
                    }
                    catch
                    {
                        // 单个文档异常不影响其他
                    }
                    finally
                    {
                        if (docData != IntPtr.Zero)
                        {
                            Marshal.Release(docData);
                        }
                    }
                }
            }

            return result;
        }

        private static string ToRelativePath(string root, string fullPath)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath))
            {
                return null;
            }

            root = root.TrimEnd('\\', '/');
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var rel = fullPath.Substring(root.Length).TrimStart('\\', '/');
            return rel.Length == 0 ? null : rel.Replace('\\', '/');
        }

        private static string CombineFullPath(string root, string rel)
        {
            try
            {
                return Path.Combine(root.TrimEnd('\\', '/'), rel.Replace('/', '\\'));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把"编辑器有未保存修改"的文档合并进文件列表：已存在的打上 DirtyOnly 标记；
        /// svn status 里没出现但文件存在的（已版本控制、磁盘无改动）按 modified 追加。
        /// </summary>
        private static List<FileEntry> DetectUnsavedEdits(string root, IEnumerable<FileEntry> knownFiles)
        {
            var added = new List<FileEntry>();
            HashSet<string> dirty;
            try
            {
                dirty = GetDirtyDocumentPaths(root);
            }
            catch
            {
                return added;
            }

            if (dirty.Count == 0)
            {
                return added;
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in knownFiles)
            {
                var rel = NormalizeRelPath(f.RelativePath);
                known.Add(rel);
                if (!f.Unversioned && dirty.Contains(rel))
                {
                    f.DirtyOnly = true;
                }
            }

            foreach (var rel in dirty)
            {
                if (known.Contains(rel))
                {
                    continue;
                }

                var full = CombineFullPath(root, rel);
                if (full == null || !File.Exists(full))
                {
                    continue;
                }

                added.Add(new FileEntry
                {
                    Status = "modified",
                    RelativePath = rel,
                    FullPath = full,
                    Changelist = null,
                    Unversioned = false,
                    IsChecked = false,
                    DirtyOnly = true
                });
            }

            return added;
        }

        /// 由包内的 StatusBarPillController 接收并更新右下角徽标。
        /// </summary>
        public static async System.Threading.Tasks.Task<int[]> ComputeBadgeCountsAsync(string solutionDir)
        {
            var svnExe = FindSvnExe();
            if (svnExe == null || string.IsNullOrWhiteSpace(solutionDir))
            {
                // 没有可查询的条件：不抛事件，徽标保留上一次的数值，避免闪成 0
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
                // svn 查询失败（例如瞬时错误）：不抛事件，保留上一次的徽标数值
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

            // 合并编辑器里未保存的修改，让右下角数字不等保存就变
            foreach (var extra in DetectUnsavedEdits(solutionDir, byPath.Values.ToList()))
            {
                byPath[NormalizeRelPath(extra.RelativePath)] = extra;
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

        private void Populate(string workingCopyRoot, List<FileEntry> files, bool refreshStatus = true)
        {
            var sameRoot = string.Equals(_lastRoot, workingCopyRoot, StringComparison.OrdinalIgnoreCase);
            var checkedPaths = new HashSet<string>(_currentFiles.Where(f => sameRoot && f.IsChecked)
                .Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
            var selectedPaths = new HashSet<string>(_selected.Where(f => sameRoot)
                .Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
            var lastClickedPath = sameRoot ? _lastClicked?.FullPath : null;
            var treeSelectedPath = sameRoot ? GetSelectedFile()?.FullPath : null;
            var hadTreeFocus = _tree.IsKeyboardFocusWithin;
            var scroll = FindVisualChild<ScrollViewer>(_tree);
            var verticalOffset = sameRoot && scroll != null ? scroll.VerticalOffset : 0;
            var horizontalOffset = sameRoot && scroll != null ? scroll.HorizontalOffset : 0;
            var populateGeneration = ++_populateGeneration;
            var nextFiles = files == null ? new List<FileEntry>() : new List<FileEntry>(files);
            nextFiles.AddRange(DetectUnsavedEdits(workingCopyRoot, nextFiles));
            foreach (var file in _currentFiles) file.PropertyChanged -= OnFilePropertyChanged;

            _lastRoot = workingCopyRoot;
            _selected.Clear();
            _lastClicked = null;
            _rowTexts.Clear();
            _rowBorders.Clear();
            _tree.Items.Clear();
            _currentFiles = nextFiles;
            foreach (var file in _currentFiles)
            {
                file.IsChecked = !IsUiIgnoredFile(file) && checkedPaths.Contains(file.FullPath);
                if (selectedPaths.Contains(file.FullPath)) _selected.Add(file);
                if (string.Equals(file.FullPath, lastClickedPath, StringComparison.OrdinalIgnoreCase)) _lastClicked = file;
                file.PropertyChanged -= OnFilePropertyChanged;
                file.PropertyChanged += OnFilePropertyChanged;
            }

            var changesGroup = new GroupEntry { Name = "Changes", Kind = GroupKinds.Changes };
            var unversionedGroup = new GroupEntry { Name = L10n.T("未版本控制", "Unversioned"), Kind = GroupKinds.Unversioned };
            var changelistGroups = new Dictionary<string, GroupEntry>(StringComparer.OrdinalIgnoreCase);
            var changelistOrder = new List<string>();

            foreach (var file in _currentFiles)
            {
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
                            Name = isIgnoreCl ? IgnoreChangelistName : file.Changelist,
                            Kind = GroupKinds.Changelist(file.Changelist)
                        };
                        changelistGroups[file.Changelist] = group;
                        changelistOrder.Add(file.Changelist);
                    }

                    group.Files.Add(file);
                }
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
                var node = CreateGroupNode(group);
                _tree.Items.Add(node);
            }

            _pendingChangesCount = changesGroup.Files.Count;
            _ignoreCount = ignoreGroup != null ? ignoreGroup.Files.Count : 0;
            UpdateCaption();
            IgnoreCountChanged?.Invoke(_ignoreCount);
            CountsChanged?.Invoke(_pendingChangesCount, _ignoreCount);

            var customFileCount = changelistGroups.Values.Where(g => !IsUiIgnoreKind(g.Kind)).Sum(g => g.Files.Count);
            UpdateSummaryUi(_pendingChangesCount, _ignoreCount, unversionedGroup.Files.Count, customFileCount, refreshStatus);
            ApplyRowHighlight();
            UpdateSelectionUi();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (populateGeneration != _populateGeneration) return;
                var selectedNode = FindFileNode(_tree, treeSelectedPath);
                if (selectedNode != null)
                {
                    selectedNode.IsSelected = true;
                    if (hadTreeFocus && selectedNode.IsVisible) selectedNode.Focus();
                }
                var viewer = FindVisualChild<ScrollViewer>(_tree);
                if (viewer == null) return;
                viewer.ScrollToHorizontalOffset(horizontalOffset);
                viewer.ScrollToVerticalOffset(verticalOffset);
            }));
        }

        private void OnFilePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "IsChecked") UpdateSelectionUi();
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var match = child as T ?? FindVisualChild<T>(child);
                if (match != null) return match;
            }
            return null;
        }

        private static TreeViewItem FindFileNode(ItemsControl parent, string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            foreach (TreeViewItem node in parent.Items)
            {
                var file = node.DataContext as FileEntry;
                if (file != null && string.Equals(file.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return node;
                var match = FindFileNode(node, fullPath);
                if (match != null) return match;
            }
            return null;
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

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyRowHighlight));
        }

        private void ApplyRowHighlight()
        {
            var highlight = TryFindResource(EnvironmentColors.SystemHighlightBrushKey) as SolidColorBrush ??
                TryFindResource(SystemColors.HighlightBrushKey) as SolidColorBrush ?? SystemColors.HighlightBrush;
            var background = new SolidColorBrush(highlight.Color) { Opacity = SystemParameters.HighContrast ? 0.4 : 0.16 };
            var outline = new SolidColorBrush(highlight.Color) { Opacity = SystemParameters.HighContrast ? 1 : 0.65 };
            foreach (var kv in _rowBorders)
            {
                var selected = _selected.Contains(kv.Key);
                kv.Value.Background = selected ? background : Brushes.Transparent;
                if (selected) kv.Value.BorderBrush = outline;
                else kv.Value.BorderBrush = Brushes.Transparent;
                kv.Value.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
            }
            foreach (var text in _rowTexts.Values)
            {
                text.Background = Brushes.Transparent;
                text.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
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

        private void OnTreePreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
            {
                _keyboardContextMenu = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _keyboardContextMenu = false));
            }
            if (e.Key == Key.Space || e.Key == Key.Enter) OnTreeKeyDown(sender, e);
        }

        private void OnTreeNavigationKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Up && e.Key != Key.Down && e.Key != Key.Left && e.Key != Key.Right &&
                e.Key != Key.Home && e.Key != Key.End && e.Key != Key.PageUp && e.Key != Key.PageDown) return;
            // 文件夹行只用于分组展示，不允许通过键盘收起；仅三个顶层组可折叠。
            if ((e.Key == Key.Left || e.Key == Key.Right) && GetSelectedFile() == null)
            {
                var focusedNode = FindAncestor<TreeViewItem>(Keyboard.FocusedElement as DependencyObject) ?? _tree.SelectedItem as TreeViewItem;
                if (focusedNode?.DataContext is FolderEntry)
                {
                    focusedNode.IsExpanded = true;
                    e.Handled = true;
                    return;
                }
            }
            var generation = _populateGeneration;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (generation != _populateGeneration) return;
                _selected.Clear();
                var file = GetSelectedFile();
                if (file != null) _selected.Add(file);
                _lastClicked = file;
                ApplyRowHighlight();
            }));
        }

        private void OnTreeKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) != null) return;
            var node = FindAncestor<TreeViewItem>(Keyboard.FocusedElement as DependencyObject) ?? _tree.SelectedItem as TreeViewItem;
            var file = node?.DataContext as FileEntry;
            if (file == null) return;
            if (e.Key == Key.Space)
            {
                if (!IsUiIgnoredFile(file)) file.IsChecked = !file.IsChecked;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (File.Exists(file.FullPath)) OpenFile(file.FullPath);
                e.Handled = true;
            }
        }

        /// <summary>批量目标必须与当前文件同类别，且当前文件属于对应选择集。</summary>
        private List<FileEntry> MenuTargets(FileEntry clicked)
        {
            if (clicked == null) return new List<FileEntry>();
            var kind = InferUiKind(new List<FileEntry> { clicked }, IsUiIgnoredFile(clicked));
            Func<FileEntry, bool> sameKind = f => string.Equals(kind,
                InferUiKind(new List<FileEntry> { f }, IsUiIgnoredFile(f)), StringComparison.OrdinalIgnoreCase);
            if (_selected.Contains(clicked) && _selected.Count > 1)
                return _selected.Where(sameKind).ToList();
            if (clicked.IsChecked)
                return _currentFiles.Where(f => f.IsChecked && sameKind(f)).ToList();
            return new List<FileEntry> { clicked };
        }

        private List<FileEntry> GetVersionedCommitTargets(List<FileEntry> files)
        {
            return files.Where(f => !f.Unversioned && !IsUiIgnoredFile(f)).ToList();
        }

        private void CommitChecked()
        {
            CommitWithTortoise(_currentFiles.Where(f => f.IsChecked).ToList());
        }

        private void AddUnversioned(List<FileEntry> files)
        {
            var targets = files.Where(f => f.Unversioned).Select(f => f.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (targets.Count == 0) return;
            var message = L10n.T("将以下项目加入 SVN：", "Add these items to SVN:") + Environment.NewLine +
                Environment.NewLine + string.Join(Environment.NewLine, targets) + Environment.NewLine + Environment.NewLine +
                L10n.T("若包含目录，请在 TortoiseSVN Add 对话框中检查子项范围；此处不会执行递归 svn add。是否继续？",
                    "For directories, review the child-item scope in the TortoiseSVN Add dialog. No recursive svn add is run here. Continue?");
            if (MessageBox.Show(message, L10n.T("加入 SVN", "Add to SVN"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                OpenTortoise("add", "\"" + string.Join("*", targets) + "\"");
        }

        private void CommitWithTortoise(List<FileEntry> files)
        {
            var targets = GetVersionedCommitTargets(files);
            if (targets.Count == 0)
            {
                SetStatus(L10n.T("没有可提交的文件：请先移出 ignore-on-commit；未版本控制项目请先加入 SVN。",
                    "Nothing to commit: remove items from ignore-on-commit first; add unversioned items to SVN first."));
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
                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.Exited += (s, e) =>
                {
                    proc.Dispose();
                    Dispatcher.BeginInvoke(new Action(StartRefresh));
                };
                proc.Start();
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
            if (FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) != null) return;
            var node = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            var file = node?.DataContext as FileEntry;
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
            var keyboard = _keyboardContextMenu || (e.CursorLeft < 0 && e.CursorTop < 0);
            _keyboardContextMenu = false;
            var item = keyboard
                ? FindAncestor<TreeViewItem>(Keyboard.FocusedElement as DependencyObject) ?? _tree.SelectedItem as TreeViewItem
                : FindAncestor<TreeViewItem>(Mouse.DirectlyOver as DependencyObject);
            var data = item?.DataContext ?? (item?.Header as FrameworkElement)?.DataContext;
            var file = data as FileEntry;
            var group = data as GroupEntry;
            var folder = data as FolderEntry;

            var menu = CreateStyledContextMenu();

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
                    if (versionedTargets.Count > 0)
                        menu.Items.Add(MakeItem(
                            L10n.T("Commit...（提交 ", "Commit... (") + versionedTargets.Count + L10n.T(" 个文件）", " files)"),
                            () => CommitWithTortoise(versionedTargets)));
                    menu.Items.Add(MakeItem(L10n.T("Revert Changes...（还原 ", "Revert Changes... (") + n +
                        L10n.T(" 个文件）", " files)"), () => OpenTortoise("revert",
                        "\"" + string.Join("*", targets.Select(f => f.FullPath)) + "\"")));

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

                if (file.Unversioned)
                    menu.Items.Add(MakeItem(L10n.T("加入 SVN…", "Add to SVN…"), () => AddUnversioned(targets)));
                if (!IsUiIgnoredFile(file))
                {
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeItem(L10n.T("勾选目标文件", "Check target files"), () =>
                    {
                        foreach (var target in targets) target.IsChecked = true;
                        UpdateSelectionUi();
                    }));
                    menu.Items.Add(MakeItem(L10n.T("清除目标勾选", "Clear target checkmarks"), () =>
                    {
                        foreach (var target in targets) target.IsChecked = false;
                        UpdateSelectionUi();
                    }));
                }
            }
            else if (group != null || folder != null)
            {
                // 扁平目录行只操作该行实际展示的文件，不扩展到其他目录或分组。
                var targets = group != null ? group.Files : folder.Files;
                var ignored = IsUiIgnoreKind(group != null ? group.Kind : item.Tag as string) ||
                    targets.Any(IsUiIgnoredFile);
                if (ignored)
                {
                    menu.Items.Add(MakeItem(L10n.T("移出 ignore-on-commit", "Remove from ignore-on-commit"),
                        () => RunChangelistBatch("--remove", targets.Where(IsUiIgnoredFile).ToList())));
                }
                else
                {
                    menu.Items.Add(MakeItem(group != null ? L10n.T("勾选本组", "Check whole group") : L10n.T("勾选此目录", "Check this folder"), () =>
                    {
                        foreach (var target in targets) target.IsChecked = true;
                        UpdateSelectionUi();
                    }));
                    menu.Items.Add(MakeItem(L10n.T("清除勾选", "Clear checkmarks"), () =>
                    {
                        foreach (var target in targets) target.IsChecked = false;
                        UpdateSelectionUi();
                    }));
                    var eligible = GetVersionedCommitTargets(targets);
                    if (eligible.Count > 0)
                    {
                        menu.Items.Add(MakeItem(L10n.T("提交全部可提交文件…", "Commit all eligible files…"),
                            () => CommitWithTortoise(eligible)));
                        menu.Items.Add(MakeItem(L10n.T("加入 ignore-on-commit", "Add to ignore-on-commit"),
                            () => RunChangelistBatch(IgnoreChangelistName, eligible)));
                    }
                    if (targets.Any(f => f.Unversioned))
                        menu.Items.Add(MakeItem(L10n.T("加入 SVN…", "Add to SVN…"), () => AddUnversioned(targets)));
                }
            }

            if (menu.Items.Count == 0) { e.Handled = true; return; }
            if (keyboard && item != null)
            {
                menu.PlacementTarget = item.Header as UIElement ?? item;
                menu.Placement = PlacementMode.Bottom;
            }
            _tree.ContextMenu = menu;
        }

        private static ContextMenu CreateStyledContextMenu()
        {
            var menu = new ContextMenu
            {
                MinWidth = 252,
                MaxWidth = 340,
                Padding = new Thickness(0, 4, 0, 4),
                Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xAE, 0xB0, 0xB7)),
                BorderThickness = new Thickness(1),
                HasDropShadow = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    Direction = 315,
                    ShadowDepth = 1,
                    BlurRadius = 4,
                    Opacity = 0.18
                }
            };
            var itemStyle = new Style(typeof(MenuItem));
            itemStyle.Setters.Add(new Setter(Control.MinHeightProperty, 26.0));
            itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(31, 4, 12, 4)));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xE2, 0xEA, 0xF4))));
            hover.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            itemStyle.Triggers.Add(hover);
            menu.Resources.Add(typeof(MenuItem), itemStyle);

            var separatorStyle = new Style(typeof(Separator));
            separatorStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 1.0));
            separatorStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(29, 4, 0, 4)));
            separatorStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xDC))));
            separatorStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            menu.Resources.Add(typeof(Separator), separatorStyle);
            return menu;
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
                var content = from as FrameworkContentElement;
                from = content != null ? content.Parent : VisualTreeHelper.GetParent(from);
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

        private void ClearSolutionView()
        {
            _solutionGeneration++;
            _populateGeneration++;
            _pendingRefresh = false;
            foreach (var file in _currentFiles) file.PropertyChanged -= OnFilePropertyChanged;
            _currentFiles.Clear();
            _selected.Clear();
            _lastClicked = null;
            _rowTexts.Clear();
            _rowBorders.Clear();
            _tree.Items.Clear();
            _lastRoot = null;
            _solutionRoot = null;
            _pendingChangesCount = 0;
            _ignoreCount = 0;
            _lastUiRefresh = null;
            UpdateSummaryUi(0, 0, 0, 0, false);
            _hasUiSummary = false;
            _summaryText.Text = "";
            _rootCountText.Text = "";
            UpdateCaption();
            IgnoreCountChanged?.Invoke(0);
            CountsChanged?.Invoke(0, 0);
        }

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            Dispatcher.Invoke(() =>
            {
                ClearSolutionView();
                var root = GetSolutionDirectory();
                _solutionRoot = string.IsNullOrEmpty(root) ? null : root.TrimEnd('\\', '/');
                StartRefresh();
            });
            return VSConstants.S_OK;
        }

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            Dispatcher.Invoke(() =>
            {
                ClearSolutionView();
                SetStatus(L10n.T("未打开解决方案", "No solution open"));
            });
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
