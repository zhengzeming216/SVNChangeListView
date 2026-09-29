using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

[assembly: AssemblyTitle("SVN ChangeList View")]
[assembly: AssemblyDescription("按 SVN changelist 分组显示待提交文件，ignore-on-commit 置底灰显")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Marvin")]
[assembly: AssemblyProduct("SvnChangelistView")]
[assembly: AssemblyCopyright("Copyright © Marvin")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: CLSCompliant(false)]
[assembly: AssemblyVersion("1.4.2.0")]
[assembly: AssemblyFileVersion("1.4.2.0")]

namespace SvnChangelistView
{
    internal static class Guids
    {
        public const string PackageGuidString = "8a5c2f31-4b7d-4e2a-9c1b-3f6d8a2e5c74";
        public const string CommandSetString = "9d3f1a47-2e86-4c5b-a1d9-7c4b8e2f6a31";
        public const string ToolWindowString = "a1c4e8d2-3f5b-4d97-8e2a-6b9c0d4f1e85";
    }
}
