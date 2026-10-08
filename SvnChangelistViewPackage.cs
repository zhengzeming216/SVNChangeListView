using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    [ProvideToolWindowVisibility(typeof(SvnChangelistWindow), VSConstants.UICONTEXT.SolutionExists_string)]
    public sealed class SvnChangelistViewPackage : AsyncPackage
    {
        private OleMenuCommand _badgeCommand;
        private StatusBarPillController _pill;
        private int _lastChanges;
        private int _lastIgnore;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            L10n.Load();

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
                        await SvnChangelistControl.ComputeBadgeCountsAsync(solDir);
                        return;
                    }
                }
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
        private TextBlock _icon;
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
            // 小图标：系统图标字体的对勾（Segoe Fluent Icons 优先，老系统回退 Segoe MDL2 Assets）
            _icon = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            };

            _text = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(_icon);
            content.Children.Add(_text);

            _pill = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(2, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
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
                _icon.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xD3, 0x5F));
            }
            else
            {
                _text.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
                _icon.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x7F, 0x37));
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
