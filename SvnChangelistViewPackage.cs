using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SvnChangelistView
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(Guids.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(SvnChangelistWindow))]
    public sealed class SvnChangelistViewPackage : AsyncPackage
    {
        private OleMenuCommand _badgeCommand;
        private StatusBarPillController _pill;
        private int _lastChanges;
        private int _lastIgnore;
        private DispatcherTimer _dirtyTimer;
        private FileSystemWatcher _fsw;
        private uint _rdtCookie;
        private bool _badgeBusy;
        private string _lastDirtyKey;
        private DateTime _lastFswTrigger = DateTime.MinValue;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            L10n.Initialize(this);

            var mcs = await this.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (mcs != null)
            {
                var menuCommandId = new CommandID(new Guid(Guids.CommandSetString), 0x0100);
                var menuItem = new OleMenuCommand((s, e) => ShowToolWindow(), menuCommandId);
                mcs.AddCommand(menuItem);

                // 工具栏徽标：显示 ignore-on-commit 文件数，点击打开工具窗口
                var badgeId = new CommandID(new Guid(Guids.CommandSetString), 0x0200);
                _badgeCommand = new OleMenuCommand((s, e) => ShowToolWindow(), badgeId)
                {
                    Text = L10n.T("本地专用: 0", "Local-only: 0")
                };
                mcs.AddCommand(_badgeCommand);
            }

            SvnChangelistControl.IgnoreCountChanged += OnIgnoreCountChanged;
            SvnChangelistControl.CountsChanged += OnCountsChanged;

            // 状态栏徽标：主窗口可视树就绪后注入，最多重试 10 次（每 2 秒）
            _ = this.JoinableTaskFactory.RunAsync(async () =>
            {
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    await Task.Delay(2000);
                    await this.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_pill == null)
                    {
                        _pill = new StatusBarPillController(this);
                    }

                    if (_pill.TryAttach())
                    {
                        // 注入时把已拉到的计数推给徽标（工具窗口可能还没打开过）
                        _pill.UpdateCounts(_lastChanges, _lastIgnore);
                        return;
                    }
                }
            });

            // 实时刷新（不再有手动"刷新"按钮和"自动刷新"复选框）：
            //   1) 编辑器脏标记检测（400ms，只做轻量 RDT 枚举，不跑 svn）→ 未保存也即时反映
            //   2) 文件系统监听（解决方案目录）→ 外部工具（如 WorkBuddy）改文件即时反映
            //   3) 文档保存事件（见下方 RDT 订阅）
            _dirtyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _dirtyTimer.Tick += (s, e) => CheckDirtyDocuments();
            _dirtyTimer.Start();

            // 文档保存即刷新（比轮询更即时）
            AdviseRunningDocTableEvents();

            // 启动即拉取一次 SVN 计数：让右下角徽标在工具窗口打开前就显示待提交数量。
            // 工具窗口未打开时不会触发 StartRefresh，所以这里用无 UI 实例的静态方法主动拉一次。
            _ = this.JoinableTaskFactory.RunAsync(async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await Task.Delay(1500);
                    await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    var solDir = GetSolutionDir();
                    if (!string.IsNullOrEmpty(solDir))
                    {
                        WatchSolutionDir(solDir);
                        await SvnChangelistControl.ComputeBadgeCountsAsync(solDir);
                        return;
                    }
                }
            });
        }

        /// <summary>检测"打开的文档是否有未保存修改"的变化，一变就触发刷新。</summary>
        private void CheckDirtyDocuments()
        {
            try
            {
                var solDir = GetSolutionDir();
                if (string.IsNullOrEmpty(solDir))
                {
                    return;
                }

                var dirty = SvnChangelistControl.GetDirtyDocumentPaths(solDir);
                var key = string.Join("|", dirty.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                if (key == _lastDirtyKey)
                {
                    return;
                }

                _lastDirtyKey = key;
                TriggerRefresh();
            }
            catch
            {
                // 忽略：下一 tick 再试
            }
        }

        private void TriggerRefresh()
        {
            if (SvnChangelistControl.HasLiveInstance)
            {
                SvnChangelistControl.RequestRefresh(); // 控件自己刷树并 raise CountsChanged
                return;
            }

            _ = RefreshBadgeAsync();
        }

        /// <summary>监听解决方案目录，捕获外部工具改文件（不经过 VS 保存）。</summary>
        private void WatchSolutionDir(string solDir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (_fsw != null)
                {
                    _fsw.EnableRaisingEvents = false;
                    _fsw.Dispose();
                    _fsw = null;
                }

                if (string.IsNullOrEmpty(solDir) || !Directory.Exists(solDir))
                {
                    return;
                }

                _fsw = new FileSystemWatcher(solDir)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 65536,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                _fsw.Changed += OnFileSystemChanged;
                _fsw.Created += OnFileSystemChanged;
                _fsw.Deleted += OnFileSystemChanged;
                _fsw.Renamed += OnFileSystemChanged;
                _fsw.EnableRaisingEvents = true;
            }
            catch
            {
                // 监听失败不影响其他刷新途径
            }
        }

        private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            // 防抖：一次批量改动只触发一次刷新
            var now = DateTime.UtcNow;
            if ((now - _lastFswTrigger).TotalMilliseconds < 800)
            {
                return;
            }

            _lastFswTrigger = now;
            _ = this.JoinableTaskFactory.RunAsync(async () =>
            {
                await this.JoinableTaskFactory.SwitchToMainThreadAsync();
                TriggerRefresh();
            });
        }

        private void OnIgnoreCountChanged(int count)
        {
            // 事件来自后台刷新线程，切回 UI 线程更新工具栏按钮文字
            _ = this.JoinableTaskFactory.RunAsync(async () =>
            {
                await this.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_badgeCommand != null)
                {
                    _badgeCommand.Text = L10n.T("本地专用: ", "Local-only: ") + count;
                }
            });
        }

        private void OnCountsChanged(int changes, int ignore)
        {
            _lastChanges = changes;
            _lastIgnore = ignore;
            _ = this.JoinableTaskFactory.RunAsync(async () =>
            {
                await this.JoinableTaskFactory.SwitchToMainThreadAsync();
                _pill?.UpdateCounts(changes, ignore);
            });
        }

        internal void ShowToolWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var shell = GetService(typeof(SVsUIShell)) as IVsUIShell;
            if (shell == null)
            {
                return;
            }

            var persistenceSlot = new Guid(Guids.ToolWindowString);
            if (ErrorHandler.Succeeded(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref persistenceSlot, out IVsWindowFrame windowFrame)))
            {
                windowFrame.Show();
            }
        }

        private string GetSolutionDir()
        {
            try
            {
                var solution = GetService(typeof(SVsSolution)) as IVsSolution;
                if (solution != null && ErrorHandler.Succeeded(solution.GetSolutionInfo(out string dir, out _, out _)))
                {
                    return dir;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        /// <summary>无 UI 实例地刷一次徽标计数（工具窗口未打开时使用）。</summary>
        private async Task RefreshBadgeAsync()
        {
            if (_badgeBusy)
            {
                return;
            }

            var solDir = GetSolutionDir();
            if (string.IsNullOrEmpty(solDir))
            {
                return;
            }

            _badgeBusy = true;
            try
            {
                await SvnChangelistControl.ComputeBadgeCountsAsync(solDir);
            }
            finally
            {
                _badgeBusy = false;
            }
        }

        private void AdviseRunningDocTableEvents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var rdt = GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
                if (rdt != null)
                {
                    rdt.AdviseRunningDocTableEvents(new RunningDocTableEvents(this), out _rdtCookie);
                }
            }
            catch
            {
                // 忽略：拿不到 RDT 就只靠定时轮询
            }
        }

        /// <summary>文档保存：立即刷新（切回 UI 线程）。</summary>
        private void OnDocumentSaved()
        {
            _ = this.JoinableTaskFactory.RunAsync(async () =>
            {
                await this.JoinableTaskFactory.SwitchToMainThreadAsync();
                TriggerRefresh();
            });
        }

        private sealed class RunningDocTableEvents : IVsRunningDocTableEvents
        {
            private readonly SvnChangelistViewPackage _pkg;

            public RunningDocTableEvents(SvnChangelistViewPackage pkg)
            {
                _pkg = pkg;
            }

            public int OnAfterSave(uint docCookie)
            {
                _pkg.OnDocumentSaved();
                return 0;
            }

            public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocks, uint dwEditLocks)
            {
                return 0;
            }

            public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocks, uint dwEditLocks)
            {
                return 0;
            }

            public int OnAfterAttributeChange(uint docCookie, uint grfAttribs)
            {
                return 0;
            }

            public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
            {
                return 0;
            }

            public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
            {
                return 0;
            }

            public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// 把"待提交数量"徽标注入 VS 主窗口状态栏（仿 VisualSVN 右下角数字，点击打开本工具窗口）。
    /// 做法：在主窗口可视树中找到 StatusBarContainer 元素，把控件挂到其父容器（DockPanel）里。
    /// </summary>
    internal sealed class StatusBarPillController
    {
        private readonly SvnChangelistViewPackage _package;
        private Panel _panel;
        private Border _pill;
        private System.Windows.Shapes.Path _icon;
        private TextBlock _text;
        private Color _statusBarColor = Color.FromRgb(0x00, 0x00, 0x00);
        private bool _hasStatusBarColor;
        private int _changes;
        private int _ignore;

        public StatusBarPillController(SvnChangelistViewPackage package)
        {
            _package = package;
        }

        public bool TryAttach()
        {
            try
            {
                if (_pill != null)
                {
                    return true;
                }

                var window = Application.Current?.MainWindow;
                if (window == null)
                {
                    return false;
                }

                var container = FindChildByName(window, "StatusBarContainer");
                var panel = (container as FrameworkElement)?.Parent as Panel;
                if (panel == null)
                {
                    return false;
                }

                BuildPill();

                // 记下状态栏底色（用于文字/图标明暗与无缝底色）
                if ((container as Border)?.Background is SolidColorBrush barBrush)
                {
                    _statusBarColor = barBrush.Color;
                    _hasStatusBarColor = true;
                }

                _panel = panel;
                DockPanel.SetDock(_pill, Dock.Right);
                // 不能作为最后一个子元素（会被 LastChildFill 拉伸），插到倒数第二个位置，
                // 徽标会贴在状态栏最右侧、紧挨右侧图标区
                var insertAt = Math.Max(0, panel.Children.Count - 1);
                panel.Children.Insert(insertAt, _pill);

                ApplyTheme();
                Update();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void BuildPill()
        {
            // 设计稿中的“文档 + 铅笔”线性入口图标。
            _icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2.5,2 L 8.5,2 L 11,4.7 L 11,6.7 M 2.5,2 L 2.5,14 L 7.5,14 M 8,4.7 L 11,4.7 M 8,7.2 L 9,7.2 M 6.8,11.2 L 11.6,6.4 L 13.6,8.4 L 8.8,13.2 L 6.3,13.7 Z"),
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.1,
                StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };

            _text = new TextBlock
            {
                FontWeight = FontWeights.Normal,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(_icon);
            content.Children.Add(_text);

            _pill = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(9, 0, 9, 0),
                Margin = new Thickness(0),
                Height = 27,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
            _pill.MouseEnter += (s, e) =>
            {
                var darkBar = (_statusBarColor.R * 0.299 + _statusBarColor.G * 0.587 + _statusBarColor.B * 0.114) <= 186;
                _pill.Background = new SolidColorBrush(darkBar
                    ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
            };
            _pill.MouseLeave += (s, e) => ApplyTheme();
            _pill.MouseLeftButtonDown += (s, e) =>
            {
                try { _package.ShowToolWindow(); } catch { }
            };
        }

        public void UpdateCounts(int changes, int ignore)
        {
            if (_pill == null)
            {
                return;
            }

            _changes = changes;
            _ignore = ignore;
            Update();
        }

        private void Update()
        {
            // 徽标只显示待提交（Changes）数量，与 VisualSVN 口径一致；
            // 本地专用数量放 tooltip 里。
            _text.Text = _changes.ToString();
            _pill.ToolTip = L10n.T("SVN 待提交: ", "SVN pending: ") + _changes +
                            L10n.T(", 本地专用: ", ", local-only: ") + _ignore +
                            L10n.T("（点击打开 SVN ChangeList View）", " (click to open SVN ChangeList View)");
            _pill.Visibility = (_changes + _ignore) > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>按状态栏底色亮度选择配色：底色与状态栏完全一致（无缝），文字/图标按明暗取色。</summary>
        private void ApplyTheme()
        {
            var c = _statusBarColor;
            if (!_hasStatusBarColor && _panel?.Background is SolidColorBrush solid)
            {
                c = solid.Color;
            }

            var dark = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) <= 186;

            // 底色与状态栏一致 → 徽标看起来就是状态栏上的一组文字+图标，不再是一块补丁
            _pill.Background = _hasStatusBarColor
                ? new SolidColorBrush(c)
                : (Brush)(dark
                    ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)));

            if (dark)
            {
                _text.Foreground = Brushes.White;
                _icon.Stroke = Brushes.White;
            }
            else
            {
                _text.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
                _icon.Stroke = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            }
        }

        private static DependencyObject FindChildByName(DependencyObject parent, string name)
        {
            if (parent == null)
            {
                return null;
            }

            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && fe.Name == name)
                {
                    return fe;
                }

                var hit = FindChildByName(child, name);
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
        }
    }
}
