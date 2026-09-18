using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace XbarControl {
    public static class Theme {
        public static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XbarControl", "theme.txt");
        // Semantic roles let control states, popups and existing log entries update together.
        public static readonly string[,] Palette = {
            {"Workspace", "#F1F3F5", "#101820"}, {"Surface", "#FFFFFF", "#1B2732"},
            {"Sidebar", "#E9EEF2", "#16212B"}, {"Ink", "#182731", "#E5EDF5"},
            {"Muted", "#53646E", "#ADBDCC"}, {"Accent", "#315DCE", "#8AAEFF"},
            {"OnAccent", "#FFFFFF", "#102142"}, {"Line", "#CDD5DC", "#586D80"},
            {"InputLine", "#8B9BA8", "#6F8498"}, {"Readout", "#192F3D", "#0E1D28"},
            {"ReadoutMuted", "#BDD0DE", "#BDD0DE"}, {"ReadoutInk", "#FFFFFF", "#F0F6FC"},
            {"PendingInk", "#AFC6FF", "#AFC6FF"}, {"PendingSurface", "#EAF0FF", "#273E66"},
            {"StatusSurface", "#EDF1F5", "#293A4A"}, {"StatusInk", "#405B70", "#C6DBEE"},
            {"DisabledSurface", "#E0E5EA", "#2A3743"}, {"DisabledInk", "#657480", "#9BACBC"},
            {"Error", "#A53432", "#FFACAA"}, {"Logo", "#182E3C", "#294456"},
            {"LogoInk", "#FFFFFF", "#F0F6FC"}, {"Track", "#CBD4DE", "#61758A"},
            {"ReadoutLine", "#405763", "#405763"}, {"ReadoutArrow", "#93AEBD", "#93AEBD"},
            {"SidebarLine", "#C5D0D9", "#3D5163"}
        };
        public static bool Load(string path) {
            try { return File.ReadAllText(path).Trim() == "dark"; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public static void Save(string path, bool dark) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, dark ? "dark" : "light");
        }
        public static void Apply(Window window, bool dark) {
            for(int i=0;i<Palette.GetLength(0);i++) {
                var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[i,dark?2:1]));
                brush.Freeze(); window.Resources[Palette[i,0]]=brush;
            }
            ApplyTitleBar(window,dark);
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        public static void ApplyTitleBar(Window window,bool dark) {
            IntPtr handle=new WindowInteropHelper(window).Handle;
            if(handle==IntPtr.Zero) return;
            try {
                int flag=dark?1:0;
                DwmSetWindowAttribute(handle,20,ref flag,4);
                // Windows 11 supports explicit caption colors, independent of OS theme.
                Color bg=((SolidColorBrush)window.Resources["Workspace"]).Color;
                Color fg=((SolidColorBrush)window.Resources["Ink"]).Color;
                int background=bg.R | (bg.G<<8) | (bg.B<<16), foreground=fg.R | (fg.G<<8) | (fg.B<<16);
                DwmSetWindowAttribute(handle,35,ref background,4);
                DwmSetWindowAttribute(handle,36,ref foreground,4);
            } catch(DllNotFoundException) { } catch(EntryPointNotFoundException) { }
        }
    }
}
