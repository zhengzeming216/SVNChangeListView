using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SvnChangelistView
{
    /// <summary>
    /// 记录各分组（按 GroupKinds 的稳定 key）的展开/折叠状态，
    /// 存到 %LocalAppData%\SvnChangelistView\viewstate.ini，跨 VS 会话保留。
    /// </summary>
    internal static class ViewState
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, bool> _expanded;

        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SvnChangelistView", "viewstate.ini");
            }
        }

        private static void EnsureLoaded()
        {
            if (_expanded != null)
            {
                return;
            }

            var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(SettingsPath))
                {
                    foreach (var line in File.ReadAllLines(SettingsPath))
                    {
                        var i = line.IndexOf('=');
                        if (i <= 0)
                        {
                            continue;
                        }

                        var key = line.Substring(0, i).Trim();
                        if (key.Length == 0)
                        {
                            continue;
                        }

                        d[key] = line.Substring(i + 1).Trim() == "1";
                    }
                }
            }
            catch
            {
                // 读取失败按无记录处理
            }

            _expanded = d;
        }

        /// <summary>取某分组的展开状态；没有记录时返回 null（由调用方决定默认值）。</summary>
        public static bool? GetExpanded(string kind)
        {
            if (string.IsNullOrEmpty(kind))
            {
                return null;
            }

            lock (Gate)
            {
                EnsureLoaded();
                return _expanded.TryGetValue(kind, out var v) ? v : (bool?)null;
            }
        }

        public static void SetExpanded(string kind, bool value)
        {
            if (string.IsNullOrEmpty(kind))
            {
                return;
            }

            lock (Gate)
            {
                EnsureLoaded();
                if (_expanded.TryGetValue(kind, out var cur) && cur == value)
                {
                    return;
                }

                _expanded[kind] = value;
                Save();
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var sb = new StringBuilder();
                foreach (var kv in _expanded)
                {
                    sb.Append(kv.Key).Append('=').Append(kv.Value ? "1" : "0").Append(Environment.NewLine);
                }

                File.WriteAllText(SettingsPath, sb.ToString());
            }
            catch
            {
                // 写失败不影响功能
            }
        }
    }
}
