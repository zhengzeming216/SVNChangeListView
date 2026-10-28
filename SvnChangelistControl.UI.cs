using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.PlatformUI;
using ShapePath = System.Windows.Shapes.Path;

namespace SvnChangelistView
{
    public sealed partial class SvnChangelistControl
    {
        private readonly Dictionary<FileEntry, Border> _rowBorders = new Dictionary<FileEntry, Border>();
        private readonly Dictionary<Button, TextBlock> _uiButtonLabels = new Dictionary<Button, TextBlock>();
        private readonly List<Action> _uiAggregateDetach = new List<Action>();
        private TextBlock _rootText;
        private TextBlock _rootCountText;
        private TextBlock _summaryText;
        private TextBlock _refreshTimeText;
        private TextBlock _checkedText;
        private TextBlock _emptyText;
        private Button _refreshButton;
        private Button _collapseButton;
        private Button _moreButton;
        private Button _addButton;
        private Button _clearButton;
        private Button _syncButton;
        private TextBox _commitMessageBox;
        private TextBlock _commitHintText;
        private TextBlock _commitCountText;
        private TextBlock _commitLabelText;
        private bool _committing;
        private Border _selectionBar;
        private DateTime? _lastUiRefresh;
        private int _summaryChanges;
        private int _summaryIgnore;
        private int _summaryUnversioned;
        private int _summaryCustom;
        private bool _hasUiSummary;
        private static readonly ControlTemplate UiRowTemplate = CreateUiRowTemplate(false);
        private static readonly ControlTemplate UiFolderRowTemplate = CreateUiRowTemplate(true);
        private static readonly ControlTemplate UiExpanderTemplate = CreateUiExpanderTemplate();
        private static readonly ControlTemplate UiButtonTemplate = CreateUiButtonTemplate();

        private void BuildUi()
        {
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = SystemFonts.MessageFontFamily;
            FontSize = SystemFonts.MessageFontSize;
            SetResourceReference(FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
            SetResourceReference(FontSizeProperty, VsFonts.EnvironmentFontSizeKey);
            Background = SystemColors.WindowBrush;
            Foreground = SystemColors.WindowTextBrush;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var layout = new Grid { ClipToBounds = true };
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tools = new DockPanel { LastChildFill = true, Margin = new Thickness(5, 0, 5, 0) };
            _moreButton = MakeUiButton("more", (s, e) => OpenUiMoreMenu(), false);
            DockPanel.SetDock(_moreButton, Dock.Right);
            tools.Children.Add(_moreButton);
            var leftTools = new StackPanel { Orientation = Orientation.Horizontal };
            _commitButton = MakeUiButton("push", (s, e) => CommitChecked(), true);
            _updateButton = MakeUiButton("pull", (s, e) => OpenTortoise("update", null), true);
            _syncButton = MakeUiButton("sync", (s, e) => SyncChecked(), true);
            _refreshButton = MakeUiButton("refresh", (s, e) => StartRefresh(), false);
            _collapseButton = MakeUiButton("collapse", (s, e) => CollapseUiTree(), false);
            leftTools.Children.Add(_commitButton);
            leftTools.Children.Add(_updateButton);
            leftTools.Children.Add(_syncButton);
            var divider = new Border { Width = 1, Height = 18, Margin = new Thickness(5, 0, 5, 0) };
            divider.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            leftTools.Children.Add(divider);
            leftTools.Children.Add(_refreshButton);
            leftTools.Children.Add(_collapseButton);
            tools.Children.Add(leftTools);
            layout.Children.Add(MakeUiBand(tools, 0, false));

            var path = MakeUiColumns(25, double.NaN, double.NaN);
            path.Margin = new Thickness(9, 0, 9, 0);
            path.Children.Add(MakeUiIcon("folder"));
            _rootText = MakeUiText("");
            _rootText.Margin = new Thickness(0, 0, 8, 0);
            _rootText.SetBinding(ToolTipProperty, new Binding("Text") { Source = _rootText });
            Grid.SetColumn(_rootText, 1);
            path.Children.Add(_rootText);
            _rootCountText = MakeUiText("");
            Grid.SetColumn(_rootCountText, 2);
            path.Children.Add(_rootCountText);
            layout.Children.Add(MakeUiBand(path, 1, false));
            layout.Children.Add(MakeUiBand(CreateCommitComposer(), 2, true));

            _tree = new TreeView
            {
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                ContextMenu = new ContextMenu()
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(_tree, ScrollBarVisibility.Auto);
            ScrollViewer.SetCanContentScroll(_tree, false);
            _tree.Template = CreateUiTreeTemplate();
            ((System.Collections.Specialized.INotifyCollectionChanged)_tree.Items).CollectionChanged += (s, e) =>
            {
                if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset) return;
                foreach (var detach in _uiAggregateDetach) detach();
                _uiAggregateDetach.Clear();
            };
            _tree.PreviewMouseLeftButtonDown += OnTreeLeftDown;
            _tree.MouseDoubleClick += OnDoubleClick;
            _tree.ContextMenuOpening += OnContextMenuOpening;
            _tree.KeyDown += OnTreeKeyDown;
            var treeArea = new Grid { ClipToBounds = true };
            treeArea.Children.Add(_tree);
            _emptyText = MakeUiText("");
            _emptyText.Margin = new Thickness(20);
            _emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyText.VerticalAlignment = VerticalAlignment.Center;
            _emptyText.TextWrapping = TextWrapping.Wrap;
            _emptyText.TextAlignment = TextAlignment.Center;
            _emptyText.IsHitTestVisible = false;
            treeArea.Children.Add(_emptyText);
            Grid.SetRow(treeArea, 3);
            layout.Children.Add(treeArea);

            var selection = new DockPanel { Margin = new Thickness(9, 0, 6, 0), LastChildFill = true };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            _addButton = MakeUiButton("add", (s, e) => AddUnversioned(CheckedUiFiles(true)), true);
            _clearButton = MakeUiButton("clear", (s, e) =>
            {
                foreach (var file in _currentFiles) file.IsChecked = false;
                UpdateSelectionUi();
            }, true);
            actions.Children.Add(_addButton);
            actions.Children.Add(_clearButton);
            DockPanel.SetDock(actions, Dock.Right);
            selection.Children.Add(actions);
            _checkedText = MakeUiText("");
            selection.Children.Add(_checkedText);
            _selectionBar = MakeUiBand(selection, 4, true);
            _selectionBar.MinHeight = 30;
            layout.Children.Add(_selectionBar);

            var footer = MakeUiColumns(double.NaN, double.NaN);
            footer.MinHeight = 28;
            footer.Margin = new Thickness(9, 0, 9, 0);
            _summaryText = MakeUiText("");
            _summaryText.Margin = new Thickness(0, 0, 8, 0);
            // 状态与摘要共用左侧文本；SetStatus 可即时显示操作或错误，下一次汇总恢复摘要。
            _statusText = _summaryText;
            _statusText.SetBinding(ToolTipProperty, new Binding("Text") { Source = _statusText });
            footer.Children.Add(_summaryText);
            _refreshTimeText = MakeUiText("");
            Grid.SetColumn(_refreshTimeText, 1);
            footer.Children.Add(_refreshTimeText);
            layout.Children.Add(MakeUiBand(footer, 5, true));
            Content = layout;
            ApplyLanguage();
        }

        /// <summary>
        /// 列表上方的提交说明区。说明是可选输入：留空也能提交（svn commit --force-log），
        /// 点击工具栏“提交”后直接把已勾选文件写入 SVN 仓库，不再弹出第二个提交编辑窗口。
        /// </summary>
        private FrameworkElement CreateCommitComposer()
        {
            var stack = new StackPanel { Margin = new Thickness(9, 6, 9, 7) };

            var labelRow = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _commitLabelText = MakeUiText(L10n.T("提交说明", "Commit message"));
            _commitLabelText.FontWeight = FontWeights.SemiBold;
            labelRow.Children.Add(_commitLabelText);
            _commitCountText = MakeUiText("0");
            _commitCountText.Margin = new Thickness(6, 0, 0, 0);
            _commitCountText.Opacity = 0.75;
            Grid.SetColumn(_commitCountText, 1);
            labelRow.Children.Add(_commitCountText);

            _commitMessageBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 58,
                MaxHeight = 140,
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Top,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _commitMessageBox.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
            _commitMessageBox.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ComboBoxTextBrushKey);
            _commitMessageBox.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ComboBoxBorderBrushKey);
            _commitMessageBox.TextChanged += (s, e) => UpdateCommitComposerUi();
            _commitMessageBox.PreviewKeyDown += (s, e) =>
            {
                // Ctrl+Enter 直接提交；单独 Enter 仍用于换行。
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                CommitChecked();
            };

            _commitHintText = MakeUiText("");
            _commitHintText.TextWrapping = TextWrapping.Wrap;
            _commitHintText.TextTrimming = TextTrimming.None;
            _commitHintText.Margin = new Thickness(0, 4, 0, 0);
            _commitHintText.Opacity = 0.8;
            AutomationProperties.SetLiveSetting(_commitHintText, AutomationLiveSetting.Polite);

            stack.Children.Add(labelRow);
            stack.Children.Add(_commitMessageBox);
            stack.Children.Add(_commitHintText);
            return stack;
        }

        /// <summary>
        /// 提交前的二次确认（说明可选，但提交动作需要确认）：列出工作副本、说明与文件清单，
        /// 默认焦点落在“返回修改”，避免误回车直接提交。
        /// </summary>
        private bool ConfirmCommit(IList<FileEntry> targets, string message, string root, bool pullFirst = false)
        {
            var dialog = new Window
            {
                Title = L10n.T("确认提交到 SVN 仓库", "Confirm commit to SVN"),
                Width = 520,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Background,
                Foreground = Foreground,
                FontFamily = FontFamily,
                FontSize = FontSize,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            try
            {
                var owner = Window.GetWindow(this);
                if (owner != null && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
            }
            catch (InvalidOperationException)
            {
                // 宿主窗口尚未就绪时退化为屏幕居中显示，不让确认框失败。
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            var header = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 14, 18, 10) };
            header.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            var headerText = MakeUiText(L10n.T("确认提交到 SVN 仓库", "Confirm commit to SVN"));
            headerText.FontSize = FontSize + 2;
            headerText.FontWeight = FontWeights.SemiBold;
            header.Child = headerText;

            var body = new StackPanel { Margin = new Thickness(18, 12, 18, 6) };
            var lead = MakeUiText(pullFirst
                ? L10n.T("即将先拉取（Update）工作副本，再提交 ", "Will update (pull) the working copy, then commit ") +
                  targets.Count + L10n.T(" 个文件，请核对以下内容。", " file(s). Please review before continuing.")
                : L10n.T("即将提交 ", "Committing ") + targets.Count +
                  L10n.T(" 个文件，请核对以下内容。", " file(s). Please review before committing."));
            lead.TextWrapping = TextWrapping.Wrap;
            lead.TextTrimming = TextTrimming.None;
            lead.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(lead);
            body.Children.Add(MakeConfirmField(L10n.T("工作副本", "Working copy")));
            body.Children.Add(MakeConfirmValue(root, true));
            body.Children.Add(MakeConfirmField(L10n.T("提交说明", "Commit message")));
            body.Children.Add(MakeConfirmValue(
                string.IsNullOrWhiteSpace(message)
                    ? L10n.T("（未填写，将提交空说明）", "(left empty; committed with an empty log message)")
                    : message, false));
            body.Children.Add(MakeConfirmField(L10n.T("本次文件 · ", "Files · ") + targets.Count));
            body.Children.Add(MakeConfirmFileList(targets));
            var warning = MakeUiText(
                (pullFirst
                    ? L10n.T("确认后先拉取工作副本：拉取失败或产生冲突将中止，不会提交。", "After confirming, the working copy is updated first; a failed update or conflicts abort the commit. ")
                    : "") +
                L10n.T("确认后将提交至 SVN 仓库并产生新修订；不会提交未勾选或本地忽略的条目。",
                    "Confirming commits these files to the SVN repository and creates a new revision. Unchecked or ignored items are not included."));
            warning.TextWrapping = TextWrapping.Wrap;
            warning.TextTrimming = TextTrimming.None;
            warning.Margin = new Thickness(0, 10, 0, 0);
            warning.Opacity = 0.85;
            body.Children.Add(warning);

            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height * 0.85 - 170),
                Content = body
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button
            {
                Content = L10n.T("返回修改", "Back to edit"), MinWidth = 88, Height = 26,
                Margin = new Thickness(8, 0, 0, 0), IsCancel = true
            };
            var confirm = new Button
            {
                Content = L10n.T("确认提交", "Commit"), MinWidth = 88, Height = 26,
                Margin = new Thickness(8, 0, 0, 0), IsDefault = false
            };
            confirm.Click += (s, e) => dialog.DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 12), Child = buttons };
            footer.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);

            var rootPanel = new StackPanel();
            rootPanel.Children.Add(header);
            rootPanel.Children.Add(scroller);
            rootPanel.Children.Add(footer);
            dialog.Content = rootPanel;
            dialog.Loaded += (s, e) => cancel.Focus();

            return dialog.ShowDialog() == true;
        }

        private TextBlock MakeConfirmField(string text)
        {
            var field = MakeUiText(text);
            field.FontSize = Math.Max(11, FontSize - 1);
            field.Opacity = 0.75;
            field.Margin = new Thickness(0, 8, 0, 4);
            return field;
        }

        private FrameworkElement MakeConfirmValue(string text, bool isPath)
        {
            var value = MakeUiText(text ?? "");
            value.TextWrapping = TextWrapping.Wrap;
            value.TextTrimming = TextTrimming.None;
            if (isPath) value.FontSize = Math.Max(11, FontSize - 1);
            var border = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), Child = value };
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 132,
                Content = border
            };
        }

        private FrameworkElement MakeConfirmFileList(IList<FileEntry> targets)
        {
            var list = new ListBox
            {
                MaxHeight = 180, BorderThickness = new Thickness(1), Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            list.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            list.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            foreach (var file in targets)
            {
                var item = MakeUiText(string.IsNullOrEmpty(file.RelativePath) ? file.FullPath : file.RelativePath);
                item.Margin = new Thickness(6, 2, 6, 2);
                item.ToolTip = file.FullPath;
                list.Items.Add(item);
            }
            return list;
        }

        private void UpdateCommitComposerUi()
        {
            if (_commitCountText == null || _commitHintText == null) return;
            var text = _commitMessageBox != null ? _commitMessageBox.Text ?? "" : "";
            _commitCountText.Text = text.Length + L10n.T(" 字", " chars");

            var versioned = CheckedUiFiles(false);
            if (_committing)
            {
                _commitHintText.Text = L10n.T("正在提交，请稍候…", "Committing, please wait…");
            }
            else if (versioned.Count == 0)
            {
                _commitHintText.Text = L10n.T("勾选 Changes 中的文件后点击“推送”即可提交到 SVN；说明可留空。",
                    "Check files under Changes, then click Push to commit them to SVN; the message may be empty.");
            }
            else
            {
                _commitHintText.Text = L10n.T("将提交 ", "Commits ") + versioned.Count +
                    L10n.T(" 个文件到 SVN，说明可留空；点击后需二次确认。", " file(s) to SVN; the message is optional and confirmation is required.");
            }

            var dirty = versioned.Count(f => f.DirtyOnly);
            if (!_committing && dirty > 0)
            {
                _commitHintText.Text += L10n.T(" 其中 " + dirty + " 个文件有未保存修改，将提交磁盘上已保存的内容。",
                    " " + dirty + " file(s) have unsaved edits; the saved on-disk content will be committed.");
            }

            if (_commitMessageBox != null) _commitMessageBox.IsEnabled = !_committing;
        }

        private void ApplyLanguage()
        {
            if (_tree == null) return;
            SetUiButtonText(_commitButton, L10n.T("推送", "Push"), L10n.T("推送（SVN Commit）：提交已勾选的版本控制文件；提交说明可留空", "Push (SVN Commit): commit the checked versioned files; the message is optional"));
            SetUiButtonText(_updateButton, L10n.T("拉取", "Pull"), L10n.T("拉取（SVN Update）：更新工作副本", "Pull (SVN Update): update the working copy"));
            SetUiButtonText(_syncButton, L10n.T("同步", "Sync"), L10n.T("拉取并推送：先 Update 工作副本，再提交已勾选的文件", "Sync: update the working copy first, then commit the checked files"));
            SetUiButtonText(_refreshButton, L10n.T("刷新", "Refresh"), L10n.T("刷新 SVN 状态", "Refresh SVN status"));
            SetUiButtonText(_collapseButton, L10n.T("全部折叠", "Collapse All"), L10n.T("折叠所有分组和目录", "Collapse all groups and folders"));
            SetUiButtonText(_moreButton, L10n.T("更多", "More"), L10n.T("更多操作", "More actions"));
            SetUiButtonText(_addButton, L10n.T("加入 SVN", "Add to SVN"), L10n.T("将已勾选的未版本控制项目加入 SVN；目录范围由确认对话框确认", "Add checked unversioned items to SVN; confirm directory scope in the dialog"));
            SetUiButtonText(_clearButton, L10n.T("清除", "Clear"), L10n.T("清除全部勾选", "Clear all checkmarks"));
            if (_commitLabelText != null) _commitLabelText.Text = L10n.T("提交说明", "Commit message");
            UpdateCaption();
            RepopulateFromCache();
            if (!_hasUiSummary)
            {
                _rootText.Text = L10n.T("未打开工作副本", "No working copy open");
                _emptyText.Text = L10n.T("打开解决方案以查看 SVN 更改", "Open a solution to view SVN changes");
            }
            else
            {
                UpdateSummaryUi(_summaryChanges, _summaryIgnore, _summaryUnversioned, _summaryCustom, false);
            }
            UpdateSelectionUi();
        }

        private void UpdateCaption()
        {
            if (_window != null) _window.Caption = "SVN Changes";
        }

        private void RepopulateFromCache()
        {
            if (string.IsNullOrEmpty(_lastRoot)) return;
            var checkedFiles = _currentFiles.Where(f => f.IsChecked).ToList();
            var selectedFiles = _selected.ToList();
            var lastClicked = _lastClicked;
            Populate(_lastRoot, _currentFiles, refreshStatus: false);
            foreach (var file in checkedFiles)
                if (_currentFiles.Contains(file) && !IsUiIgnoredFile(file)) file.IsChecked = true;
            foreach (var file in selectedFiles)
                if (_currentFiles.Contains(file)) _selected.Add(file);
            _lastClicked = _currentFiles.Contains(lastClicked) ? lastClicked : null;
            ApplyRowHighlight();
            UpdateSelectionUi();
        }

        private TreeViewItem CreateGroupNode(GroupEntry group)
        {
            var ignored = IsUiIgnoreKind(group.Kind);
            var unversioned = string.Equals(group.Kind, GroupKinds.Unversioned, StringComparison.OrdinalIgnoreCase);
            var node = MakeUiNode(group);
            node.Tag = group.Kind;
            var header = MakeUiColumns(24, ignored ? 4 : 22, double.NaN, double.NaN, double.NaN);
            header.Children.Add(MakeUiExpander(node, 24));
            if (!ignored)
            {
                var check = MakeAggregateCheckBox(group.Files);
                check.ToolTip = L10n.T("全选或取消本组", "Select or clear this group");
                Grid.SetColumn(check, 1);
                header.Children.Add(check);
            }
            var title = MakeUiText(ignored ? IgnoreChangelistName :
                unversioned ? L10n.T("未版本控制", "Unversioned") :
                string.Equals(group.Kind, GroupKinds.Changes, StringComparison.OrdinalIgnoreCase) ? "Changes" : group.Name);
            title.FontWeight = FontWeights.SemiBold;
            title.Inlines.Add(new System.Windows.Documents.Run("  " + group.Files.Count)
            {
                FontWeight = FontWeights.Normal
            });
            title.ToolTip = title.Text;
            Grid.SetColumn(title, 2);
            header.Children.Add(title);
            var annotation = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (ignored) annotation.Children.Add(MakeUiIcon("lock"));
            annotation.Children.Add(MakeUiText(ignored ? L10n.T("本地专用", "Local only") : unversioned ? L10n.T("未加入 SVN", "Not in SVN") : ""));
            annotation.Margin = new Thickness(4, 0, 9, 0);
            Grid.SetColumn(annotation, 4);
            header.Children.Add(annotation);
            // 标题含计数并占剩余宽度，右侧注释始终可见。
            header.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            header.ColumnDefinitions[3].Width = GridLength.Auto;
            header.ColumnDefinitions[4].Width = GridLength.Auto;
            var band = MakeUiBand(header, 0, true);
            band.Height = 29;
            band.DataContext = group;
            node.Header = band;
            if (ignored || unversioned)
            {
                var hint = MakeUiText(ignored
                    ? L10n.T("不参与默认提交 · 本地忽略", "Excluded from default commit · local only")
                    : L10n.T("勾选项目以加入 SVN · 文件夹加入范围需确认", "Select items to add · confirm folder scope"));
                hint.Margin = new Thickness(29, 0, 9, 0);
                var hintNode = MakeUiNode(null);
                hintNode.Focusable = false;
                hintNode.IsHitTestVisible = false;
                hintNode.Header = new Border { Height = 29, Child = hint };
                node.Items.Add(hintNode);
            }
            AddFolderRows(node, BuildFolderTree(group.Files), ignored);
            RememberUiExpansion(node, group.Kind, null, string.Equals(group.Kind, GroupKinds.Changes, StringComparison.OrdinalIgnoreCase));
            return node;
        }

        private void AddFolderRows(TreeViewItem parent, FolderEntry folder, bool inIgnoreGroup)
        {
            var kind = parent.Tag as string;
            if (string.IsNullOrEmpty(kind)) kind = InferUiKind(folder.Files, inIgnoreGroup);
            if (folder.Files.Count > 0) parent.Items.Add(CreateFolderRow(folder, inIgnoreGroup, kind));
            foreach (var child in folder.SubFolders.OrderBy(f => f.FullRelative, StringComparer.OrdinalIgnoreCase))
                AddFolderRows(parent, child, inIgnoreGroup);
        }

        private TreeViewItem CreateFolderRow(FolderEntry folder, bool inIgnoreGroup)
        {
            return CreateFolderRow(folder, inIgnoreGroup, InferUiKind(folder.Files, inIgnoreGroup));
        }

        private TreeViewItem CreateFolderRow(FolderEntry folder, bool inIgnoreGroup, string kind)
        {
            var node = MakeUiNode(folder);
            node.Template = UiFolderRowTemplate;
            node.Tag = kind;
            // 目录行只是分组标签，不提供折叠交互；仅顶层三类主分组可折叠。
            var header = MakeUiColumns(40, 22, 20, double.NaN, double.NaN);
            if (!inIgnoreGroup)
            {
                var check = MakeAggregateCheckBox(folder.Files);
                check.ToolTip = L10n.T("全选或取消此目录内显示的文件", "Select or clear the files displayed in this folder");
                Grid.SetColumn(check, 1);
                header.Children.Add(check);
            }
            var icon = MakeUiIcon("folder");
            Grid.SetColumn(icon, 2);
            header.Children.Add(icon);
            var relative = folder.FullRelative ?? "";
            var text = MakeUiText(relative.Length == 0 ? L10n.T("（工作副本）", "(working copy)") :
                string.Join(" \\ ", relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)));
            text.FontWeight = FontWeights.Normal;
            text.ToolTip = relative.Length == 0 ? _lastRoot : relative;
            Grid.SetColumn(text, 3);
            header.Children.Add(text);
            var count = MakeUiText("(" + folder.Files.Count + ")");
            count.Margin = new Thickness(8, 0, 10, 0);
            Grid.SetColumn(count, 4);
            header.Children.Add(count);
            var border = new Border { Height = 23, Background = Brushes.Transparent, BorderThickness = new Thickness(0), DataContext = folder, Child = header };
            node.Header = border;
            foreach (var file in folder.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
                node.Items.Add(CreateFileNode(file, inIgnoreGroup));
            // 文件夹永远展开，折叠/展开只作用于 Changes、ignore-on-commit、Unversioned 顶层组。
            node.IsExpanded = true;
            return node;
        }

        private TreeViewItem CreateFileNode(FileEntry file, bool inIgnoreGroup)
        {
            var header = MakeUiColumns(40, 22, 20, double.NaN, 27);
            if (inIgnoreGroup)
            {
                var locked = MakeUiIcon("lock");
                locked.ToolTip = L10n.T("本地专用，不参与提交", "Local only; excluded from commit");
                Grid.SetColumn(locked, 1);
                header.Children.Add(locked);
            }
            else
            {
                var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                check.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsChecked")
                {
                    Source = file, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });
                AutomationProperties.SetName(check, file.RelativePath ?? "");
                Grid.SetColumn(check, 1);
                header.Children.Add(check);
            }
            var directory = file.Unversioned && !string.IsNullOrEmpty(file.FullPath) && Directory.Exists(file.FullPath);
            var icon = MakeUiIcon(directory ? "folder" : "file");
            Grid.SetColumn(icon, 2);
            header.Children.Add(icon);
            var text = MakeUiText(Path.GetFileName(file.RelativePath) + (file.DirtyOnly ? " *" : ""));
            text.FontStyle = FontStyles.Normal;
            Grid.SetColumn(text, 3);
            header.Children.Add(text);
            var status = MakeUiText(StatusLabel(file));
            status.HorizontalAlignment = HorizontalAlignment.Center;
            if (SystemParameters.HighContrast) status.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.WindowTextBrushKey);
            else status.Foreground = StatusBrush(file);
            Grid.SetColumn(status, 4);
            header.Children.Add(status);
            var border = new Border
            {
                Height = 23, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0), DataContext = file, Child = header
            };
            border.ToolTip = file.FullPath + (file.DirtyOnly ? L10n.T("（有未保存的修改）", " (unsaved changes)") : "");
            var node = MakeUiNode(file);
            node.Header = border;
            _rowTexts[file] = text;
            _rowBorders[file] = border;
            return node;
        }

        private CheckBox MakeAggregateCheckBox(List<FileEntry> files)
        {
            var targets = files.Where(f => !IsUiIgnoredFile(f)).Distinct().ToList();
            var check = new CheckBox
            {
                IsThreeState = true, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = targets.Count > 0
            };
            var applying = false;
            Action update = () =>
            {
                if (applying) return;
                var count = targets.Count(f => f.IsChecked);
                check.IsChecked = count == 0 ? false : count == targets.Count ? true : (bool?)null;
            };
            // 只在 Click 中向模型传播。模型通知只回算显示，不监听 Checked/Unchecked，避免三态循环。
            check.Click += (s, e) =>
            {
                var value = !targets.All(f => f.IsChecked);
                applying = true;
                try { foreach (var file in targets) file.IsChecked = value; }
                finally { applying = false; update(); }
                UpdateSelectionUi();
                e.Handled = true;
            };
            PropertyChangedEventHandler changed = (s, e) =>
            {
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "IsChecked") update();
            };
            foreach (var file in targets) file.PropertyChanged += changed;
            _uiAggregateDetach.Add(() =>
            {
                foreach (var file in targets) file.PropertyChanged -= changed;
            });
            update();
            return check;
        }

        private void UpdateSelectionUi()
        {
            if (_selectionBar == null) return;
            var versioned = CheckedUiFiles(false).Count;
            var unversioned = CheckedUiFiles(true).Count;
            _checkedText.Text = L10n.T("已勾选：", "Checked: ") + versioned + L10n.T(" 个可提交", " versioned") +
                (unversioned == 0 ? "" : " · " + unversioned + L10n.T(" 个未版本控制", " unversioned"));
            _checkedText.ToolTip = _checkedText.Text;
            _selectionBar.Visibility = versioned + unversioned == 0 ? Visibility.Collapsed : Visibility.Visible;
            _addButton.Visibility = unversioned > 0 ? Visibility.Visible : Visibility.Collapsed;
            _addButton.IsEnabled = unversioned > 0;
            _commitButton.IsEnabled = versioned > 0 && !_committing;
            _syncButton.IsEnabled = versioned > 0 && !_committing;
            _clearButton.IsEnabled = versioned + unversioned > 0 && !_committing;
            UpdateCommitComposerUi();
        }

        private void UpdateSummaryUi(int changes, int ignore, int unversioned, int custom, bool refreshed)
        {
            _hasUiSummary = true;
            _summaryChanges = changes;
            _summaryIgnore = ignore;
            _summaryUnversioned = unversioned;
            _summaryCustom = custom;
            if (refreshed) _lastUiRefresh = DateTime.Now;
            _rootText.Text = string.IsNullOrEmpty(_lastRoot) ? L10n.T("未打开工作副本", "No working copy open") : _lastRoot;
            var total = changes + ignore + unversioned + custom;
            _rootCountText.Text = total + L10n.T(" 项", " items");
            _summaryText.Text = changes + L10n.T(" 个更改", " changes") + " · " + ignore + L10n.T(" 个本地专用", " local only") +
                " · " + unversioned + L10n.T(" 个未版本控制", " unversioned") +
                (custom == 0 ? "" : " · " + custom + L10n.T(" 个自定义清单项目", " in changelists"));
            _refreshTimeText.Text = _lastUiRefresh.HasValue ? L10n.T("刷新于 ", "Refreshed ") + _lastUiRefresh.Value.ToString("HH:mm") : "";
            _refreshTimeText.ToolTip = _lastUiRefresh.HasValue ? _lastUiRefresh.Value.ToString("yyyy-MM-dd HH:mm:ss") : "";
            _emptyText.Text = string.IsNullOrEmpty(_lastRoot)
                ? L10n.T("打开解决方案以查看 SVN 更改", "Open a solution to view SVN changes")
                : L10n.T("工作副本干净，没有待处理的更改", "Working copy is clean; no pending changes");
            _emptyText.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelectionUi();
        }

        private List<FileEntry> CheckedUiFiles(bool unversioned)
        {
            return _currentFiles.Where(f => f.IsChecked && f.Unversioned == unversioned && !IsUiIgnoredFile(f)).ToList();
        }

        private static bool IsUiIgnoredFile(FileEntry file)
        {
            return string.Equals(file.Changelist, IgnoreChangelistName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUiIgnoreKind(string kind)
        {
            return string.Equals(kind, GroupKinds.Ignore, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, GroupKinds.Changelist(IgnoreChangelistName), StringComparison.OrdinalIgnoreCase);
        }

        private static string InferUiKind(List<FileEntry> files, bool ignored)
        {
            if (ignored) return GroupKinds.Ignore;
            var first = files.FirstOrDefault();
            return first == null ? GroupKinds.Changes : first.Unversioned ? GroupKinds.Unversioned :
                string.IsNullOrEmpty(first.Changelist) ? GroupKinds.Changes : GroupKinds.Changelist(first.Changelist);
        }

        private string UiExpansionKey(string kind, string relative)
        {
            return "ui2|" + Uri.EscapeDataString((_lastRoot ?? "").Replace('/', '\\').TrimEnd('\\').ToUpperInvariant()) +
                "|" + Uri.EscapeDataString(kind ?? "") + "|" + (relative == null ? "group" :
                "folder|" + Uri.EscapeDataString(relative.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant()));
        }

        private void RememberUiExpansion(TreeViewItem node, string kind, string relative, bool defaultExpanded)
        {
            var key = UiExpansionKey(kind, relative);
            node.IsExpanded = ViewState.GetExpanded(key) ?? defaultExpanded;
            node.Expanded += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, node)) ViewState.SetExpanded(key, true);
            };
            node.Collapsed += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, node)) ViewState.SetExpanded(key, false);
            };
        }

        private void CollapseUiTree()
        {
            // 只折叠三个顶层主分组；文件夹行永远展开，不参与“全部折叠”。
            foreach (TreeViewItem node in _tree.Items)
            {
                if (node.DataContext is GroupEntry && node.HasItems)
                    node.IsExpanded = false;
            }
        }

        private static void SetUiExpanded(TreeViewItem node, bool expanded)
        {
            foreach (TreeViewItem child in node.Items) SetUiExpanded(child, expanded);
            if (node.HasItems) node.IsExpanded = expanded;
        }

        private void OpenUiMoreMenu()
        {
            var menu = new ContextMenu { PlacementTarget = _moreButton, Placement = PlacementMode.Bottom };
            menu.Items.Add(MakeItem(L10n.T("刷新", "Refresh"), StartRefresh));
            menu.Items.Add(MakeItem(L10n.T("全部展开", "Expand All"), () =>
            {
                foreach (TreeViewItem node in _tree.Items) SetUiExpanded(node, true);
            }));
            menu.Items.Add(MakeItem(L10n.T("全部折叠", "Collapse All"), CollapseUiTree));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem(L10n.T("清除全部勾选", "Clear All Checkmarks"), () =>
            {
                foreach (var file in _currentFiles) file.IsChecked = false;
                UpdateSelectionUi();
            }));
            _moreButton.ContextMenu = menu;
            menu.IsOpen = true;
        }

        private Button MakeUiButton(string icon, RoutedEventHandler click, bool showText)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(MakeUiIcon(icon));
            var text = MakeUiText("");
            text.Margin = new Thickness(4, 0, 0, 0);
            text.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
            content.Children.Add(text);
            var button = new Button
            {
                Content = content, Template = UiButtonTemplate, Padding = new Thickness(5, 2, 5, 2),
                MinWidth = 27, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            _uiButtonLabels.Add(button, text);
            button.Click += click;
            return button;
        }

        private void SetUiButtonText(Button button, string label, string tooltip)
        {
            _uiButtonLabels[button].Text = label;
            button.ToolTip = tooltip;
            ToolTipService.SetShowOnDisabled(button, true);
            AutomationProperties.SetName(button, label);
        }

        private static TextBlock MakeUiText(string text)
        {
            return new TextBlock
            {
                Text = text, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
            };
        }

        private static Grid MakeUiColumns(params double[] widths)
        {
            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            var starAssigned = false;
            foreach (var width in widths)
            {
                var length = double.IsNaN(width) ? (starAssigned ? GridLength.Auto : new GridLength(1, GridUnitType.Star)) : new GridLength(width);
                if (double.IsNaN(width)) starAssigned = true;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
            }
            return grid;
        }

        private static Border MakeUiBand(UIElement child, int row, bool topBorder)
        {
            var border = new Border
            {
                Child = child, BorderThickness = topBorder ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1)
            };
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(border, row);
            return border;
        }

        private static TreeViewItem MakeUiNode(object data)
        {
            var node = new TreeViewItem
            {
                DataContext = data, Template = UiRowTemplate, Padding = new Thickness(0), Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), FocusVisualStyle = null
            };
            SoftenSelectionHighlight(node);
            return node;
        }

        private static ToggleButton MakeUiExpander(TreeViewItem node, double width)
        {
            var toggle = new ToggleButton
            {
                Width = width, Height = 22, Template = UiExpanderTemplate, Focusable = false,
                ClickMode = ClickMode.Press, VerticalAlignment = VerticalAlignment.Center
            };
            toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsExpanded") { Source = node, Mode = BindingMode.TwoWay });
            AutomationProperties.SetName(toggle, L10n.T("展开或折叠", "Expand or collapse"));
            return toggle;
        }

        private static FrameworkElement MakeUiIcon(string name)
        {
            string geometry;
            switch (name)
            {
                case "commit": geometry = "M 2,8 L 6,12 L 14,3"; break;
                case "push": geometry = "M 8,12 L 8,2 M 4,6 L 8,2 L 12,6 M 3,14 L 13,14"; break;
                case "pull": geometry = "M 8,2 L 8,12 M 4,8 L 8,12 L 12,8 M 3,14 L 13,14"; break;
                case "sync": geometry = "M 13,6 A 5.2,5.2 0 0 0 4,4 M 3,1 L 3,5 L 7,5 M 3,10 A 5.2,5.2 0 0 0 12,12 M 13,15 L 13,11 L 9,11"; break;
                case "update": geometry = "M 8,1 L 8,11 M 4,7 L 8,11 L 12,7 M 2,11 L 2,14 L 14,14 L 14,11"; break;
                case "refresh": geometry = "M 13,5 A 6,6 0 1 0 13,11 M 13,1 L 13,5 L 9,5"; break;
                case "collapse": geometry = "M 2,3 L 4,3 M 7,3 L 14,3 M 2,8 L 4,8 M 7,8 L 14,8 M 2,13 L 4,13 M 7,13 L 14,13"; break;
                case "more": geometry = "M 2,8 L 3,8 M 7,8 L 8,8 M 12,8 L 13,8"; break;
                case "folder": geometry = "M 1,4 L 6,4 L 8,6 L 15,6 L 15,13 L 1,13 Z M 1,7 L 15,7"; break;
                case "lock": geometry = "M 5,7 L 5,4 A 3,3 0 0 1 11,4 L 11,7 M 3,7 L 13,7 L 13,14 L 3,14 Z M 8,9 L 8,12"; break;
                case "add": geometry = "M 8,2 L 8,14 M 2,8 L 14,8"; break;
                case "clear": geometry = "M 4,4 L 12,12 M 12,4 L 4,12"; break;
                default: geometry = "M 4,1 L 10,1 L 14,5 L 14,15 L 4,15 Z M 10,1 L 10,5 L 14,5 M 7,8 L 5,10 L 7,12 M 10,8 L 12,10 L 10,12"; break;
            }
            var path = new ShapePath
            {
                Data = Geometry.Parse(geometry), StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round,
                Width = 16, Height = 16, Stretch = Stretch.Uniform, SnapsToDevicePixels = true,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(SvnChangelistControl), 1)
            });
            if (name == "folder")
            {
                path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, SystemColors.HotTrackBrushKey);
                if (!SystemParameters.HighContrast)
                {
                    path.Stroke = new SolidColorBrush(Color.FromRgb(0xA5, 0x73, 0x00));
                    path.Fill = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xCC, 0x45));
                }
            }
            var canvas = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            canvas.Children.Add(path);
            return canvas;
        }

        private static ControlTemplate CreateUiTreeTemplate()
        {
            var template = new ControlTemplate(typeof(TreeView));
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            scroll.SetValue(ScrollViewer.CanContentScrollProperty, false);
            scroll.SetValue(Control.PaddingProperty, new Thickness(0));
            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            items.SetValue(MarginProperty, new Thickness(0));
            items.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            scroll.AppendChild(items);
            template.VisualTree = scroll;
            return template;
        }

        private static ControlTemplate CreateUiRowTemplate(bool indentChildren)
        {
            var template = new ControlTemplate(typeof(TreeViewItem));
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            var header = new FrameworkElementFactory(typeof(ContentPresenter), "PART_Header");
            header.SetBinding(ContentPresenter.ContentProperty, new Binding("Header") { RelativeSource = RelativeSource.TemplatedParent });
            header.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            stack.AppendChild(header);
            var items = new FrameworkElementFactory(typeof(ItemsPresenter), "UiItemsHost");
            // 仅文件夹的子树（文件行）增加一个 tab；主分组下的目录不额外缩进。
            items.SetValue(MarginProperty, indentChildren ? new Thickness(20, 0, 0, 0) : new Thickness(0));
            items.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            stack.AppendChild(items);
            template.VisualTree = stack;
            var collapsed = new Trigger { Property = TreeViewItem.IsExpandedProperty, Value = false };
            collapsed.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed, "UiItemsHost"));
            template.Triggers.Add(collapsed);
            return template;
        }

        private static ControlTemplate CreateUiExpanderTemplate()
        {
            var template = new ControlTemplate(typeof(ToggleButton));
            var hit = new FrameworkElementFactory(typeof(Border));
            hit.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var arrow = new FrameworkElementFactory(typeof(ShapePath), "UiArrow");
            arrow.SetValue(ShapePath.DataProperty, Geometry.Parse("M 0,0 L 4,4 L 0,8"));
            arrow.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.0);
            arrow.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            arrow.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, EnvironmentColors.ToolWindowTextBrushKey);
            hit.AppendChild(arrow);
            template.VisualTree = hit;
            var expanded = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            expanded.Setters.Add(new Setter(ShapePath.DataProperty, Geometry.Parse("M 0,0 L 4,4 L 8,0"), "UiArrow"));
            template.Triggers.Add(expanded);
            return template;
        }

        private static ControlTemplate CreateUiButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "UiButtonBorder");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            template.VisualTree = border;
            foreach (var property in new[] { UIElement.IsMouseOverProperty, UIElement.IsKeyboardFocusWithinProperty })
            {
                var hover = new Trigger { Property = property, Value = true };
                hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey), "UiButtonBorder"));
                template.Triggers.Add(hover);
            }
            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(OpacityProperty, 0.5, "UiButtonBorder"));
            template.Triggers.Add(disabled);
            return template;
        }
    }
}
