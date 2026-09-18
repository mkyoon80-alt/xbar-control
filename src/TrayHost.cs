using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XbarControl {
    // Windows notification area integration. All callbacks run on the UI thread.
    public sealed class TrayHost : IDisposable {
        readonly NotifyIcon notify;
        readonly ContextMenuStrip menu;
        readonly Icon icon;
        internal readonly ToolStripMenuItem OpenItem, CancelItem, ExitItem;
        bool disposed;
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        static Icon CreateIcon() {
            using(var bitmap=new Bitmap(32,32)) {
                using(var g=Graphics.FromImage(bitmap))
                using(var fill=new SolidBrush(Color.FromArgb(49,93,206)))
                using(var pen=new Pen(Color.White,2.5f)) {
                    g.SmoothingMode=SmoothingMode.AntiAlias;
                    g.FillEllipse(fill,1,1,30,30);
                    pen.StartCap=LineCap.Round; pen.EndCap=LineCap.Round;
                    g.DrawLine(pen,9,8,23,24); g.DrawLine(pen,23,8,9,24); g.DrawLine(pen,8,16,24,16);
                }
                IntPtr handle=bitmap.GetHicon();
                try { using(var borrowed=Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        public TrayHost(Action open,Action cancel,Action exit) {
            try {
                icon=CreateIcon(); menu=new ContextMenuStrip();
                OpenItem=new ToolStripMenuItem("XBAR Control 열기",null,delegate { open(); });
                CancelItem=new ToolStripMenuItem("이번 자동 적용 취소",null,delegate { cancel(); });
                ExitItem=new ToolStripMenuItem("종료",null,delegate { exit(); });
                menu.Items.Add(OpenItem); menu.Items.Add(CancelItem); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(ExitItem);
                notify=new NotifyIcon { Icon=icon,Text="XBAR Control",ContextMenuStrip=menu };
                notify.DoubleClick+=delegate { open(); };
                notify.BalloonTipClicked+=delegate { open(); };
                Update("시작 준비 중",false,true);
                notify.Visible=true;
            } catch { Dispose(); throw; }
        }
        public bool Visible { get { return !disposed && notify!=null && notify.Visible; } }
        public void Update(string status,bool cancellable,bool canExit) {
            if(disposed) return;
            string tooltip="XBAR Control\n"+status;
            notify.Text=tooltip.Length>63?tooltip.Substring(0,63):tooltip;
            CancelItem.Enabled=cancellable; ExitItem.Enabled=canExit;
        }
        public void Notify(string message,bool error=false) {
            if(!disposed) notify.ShowBalloonTip(5000,"XBAR Control",message,error?ToolTipIcon.Warning:ToolTipIcon.Info);
        }
        public void Dispose() {
            if(disposed) return;
            disposed=true;
            if(notify!=null) { notify.Visible=false; notify.Dispose(); }
            if(menu!=null) menu.Dispose();
            if(icon!=null) icon.Dispose();
        }
    }
}
