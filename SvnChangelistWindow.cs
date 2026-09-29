using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SvnChangelistView
{
    [Guid(Guids.ToolWindowString)]
    public sealed class SvnChangelistWindow : ToolWindowPane
    {
        public SvnChangelistWindow() : base(null)
        {
            Caption = "SVN ChangeList View";
            Content = new SvnChangelistControl(this);
        }
    }
}
