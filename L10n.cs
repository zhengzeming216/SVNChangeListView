using System;
using System.Globalization;
using Microsoft.VisualStudio.Shell.Interop;

namespace SvnChangelistView
{
    /// <summary>
    /// 轻量双语支持。语言跟随 Visual Studio 的环境语言设置
    /// （工具 → 选项 → 环境 → 国际设置），不再由插件内部手动切换。
    /// </summary>
    internal static class L10n
    {
        private static string _lang;

        /// <summary>"zh" | "en"。未显式初始化时按当前 UI 区域性推断。</summary>
        public static string Lang
        {
            get { return _lang ?? (_lang = DetectFromCulture()); }
        }

        /// <summary>
        /// 读取 VS 的 UI 语言（IUIHostLocale.GetUILocale）；取不到时退回当前 UI 区域性。
        /// 应在包初始化（主线程）时调用一次。
        /// </summary>
        public static void Initialize(IServiceProvider serviceProvider)
        {
            try
            {
                if (serviceProvider != null)
                {
                    var hostLocale = serviceProvider.GetService(typeof(SUIHostLocale)) as IUIHostLocale;
                    uint lcid;
                    if (hostLocale != null && hostLocale.GetUILocale(out lcid) == 0 && lcid != 0)
                    {
                        _lang = FromTwoLetter(new CultureInfo((int)lcid).TwoLetterISOLanguageName);
                        return;
                    }
                }
            }
            catch
            {
                // 忽略，退回区域性判断
            }

            _lang = DetectFromCulture();
        }

        private static string DetectFromCulture()
        {
            try
            {
                return FromTwoLetter(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            }
            catch
            {
                return "zh";
            }
        }

        private static string FromTwoLetter(string two)
        {
            return string.Equals(two, "zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
        }

        public static string T(string zh, string en)
        {
            return Lang == "en" ? en : zh;
        }
    }
}
