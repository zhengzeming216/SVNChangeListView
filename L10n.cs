using System;
using System.IO;
using System.Linq;

namespace SvnChangelistView
{
    /// <summary>轻量双语支持 + 语言设置持久化（%LocalAppData%\SvnChangelistView\settings.ini）。</summary>
    internal static class L10n
    {
        public static string Lang = "zh"; // "zh" | "en"

        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SvnChangelistView", "settings.ini");
            }
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var line = File.ReadAllLines(SettingsPath)
                        .FirstOrDefault(l => l.StartsWith("lang=", StringComparison.OrdinalIgnoreCase));
                    if (line != null && line.Substring(5).Trim().Equals("en", StringComparison.OrdinalIgnoreCase))
                    {
                        Lang = "en";
                    }
                }
            }
            catch
            {
                // 读取失败用默认中文
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, "lang=" + Lang + Environment.NewLine);
            }
            catch
            {
                // 写失败不影响功能
            }
        }

        public static string T(string zh, string en)
        {
            return Lang == "en" ? en : zh;
        }
    }
}
