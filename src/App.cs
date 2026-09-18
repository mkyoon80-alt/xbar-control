using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace XbarControl {
    [DataContract] public sealed class Diagnostic {
        [DataMember] public string Gpu;
        [DataMember] public string Driver;
        [DataMember] public string ImplementationSha256;
        [DataMember] public bool Administrator;
        [DataMember] public bool StructureValidated;
        [DataMember] public int OffsetKhz;
        [DataMember] public double? MeasuredClockMhz;
        [DataMember] public string Timestamp;
        [DataMember] public string Error;
        [DataMember] public bool HardwareWritesPerformed = false;
    }
    public static class Program {
        [STAThread] public static int Main(string[] args) {
            try {
                if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : "self-test.txt");
                if (args.Length > 1 && args[0] == "--diagnose") {
                    var d = new Diagnostic(); int exit = 0;
                    try { using (var nv = new NvApi()) { var s = nv.Sample(); d.Gpu=nv.Selected.Name; d.Driver=nv.Driver; d.ImplementationSha256=nv.ImplementationHash; d.Administrator=NvApi.IsAdmin; d.StructureValidated=s.Compatible; d.OffsetKhz=s.OffsetKhz; d.MeasuredClockMhz=s.ClockMhz; d.Timestamp=s.Time.ToString("o"); } }
                    catch (Exception ex) { d.Error=ex.Message; exit=1; }
                    using (var f = File.Create(args[1])) new DataContractJsonSerializer(typeof(Diagnostic)).WriteObject(f,d);
                    return exit;
                }
                bool capture = args.Length > 1 && args[0] == "--capture";
                // Only one interactive instance can issue writes for this user.
                bool created;
                using (var mutex = new Mutex(true, "Local\\XbarControl.Desktop.v1", out created)) {
                    if (!created && args.Contains("--elevated")) {
                        try { created=mutex.WaitOne(5000); } catch(AbandonedMutexException) { created=true; }
                    }
                    if (!created && !capture) { MessageBox.Show("XBAR Control이 이미 실행 중입니다.", "XBAR Control"); return 0; }
                    var app = new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { MessageBox.Show(e.Exception.Message,"XBAR Control"); e.Handled=true; };
                    var panel = new ControlPanel(capture ? args : new string[0]);
                    app.Run(panel.Window);
                }
                return 0;
            } catch (Exception ex) {
                if (args.Length > 0) { try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt"),ex.ToString()); } catch {} }
                else MessageBox.Show(ex.Message, "XBAR Control을 열지 못했습니다");
                return 1;
            }
        }
    }
    public sealed class ControlPanel {
        public Window Window;
        NvApi nv; Reading last; int? startingKhz; int? target;
        bool syncing, reading, applying, failed, closing, selecting, initializing;
        long editRevision;
        bool darkTheme, changingTheme;
        readonly string[] captureArgs; readonly object gpuLock = new object();
        readonly DispatcherTimer poll = new DispatcherTimer();
        readonly List<string> history = new List<string>();
        T UI<T>(string name) where T : class { return Window.FindName(name) as T; }
        void Text(string name,string value) { UI<TextBlock>(name).Text=value; }
        static string Signed(double value) { return value.ToString("+0.###;−0.###;0",CultureInfo.InvariantCulture); }
        public ControlPanel(string[] capture) {
            captureArgs=capture;
            using (var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window=(Window)XamlReader.Load(stream);
            ChangeTheme(capture.Length>0 ? capture.Contains("dark") : Theme.Load(Theme.SettingsPath),false);
            UI<RadioButton>("LightTheme").Checked += delegate { if(!changingTheme) ChangeTheme(false,true); };
            UI<RadioButton>("DarkTheme").Checked += delegate { if(!changingTheme) ChangeTheme(true,true); };
            Window.SourceInitialized += delegate { Theme.ApplyTitleBar(Window,darkTheme); };
            if (capture.Length >= 4) { Window.Width=int.Parse(capture[2]); Window.Height=int.Parse(capture[3]); }
            UI<Button>("RefreshButton").Click += async delegate { if(nv==null) await Initialize(); else await Refresh(true); };
            UI<Button>("ApplyButton").Click += async delegate { await Apply(); };
            UI<Button>("PlusButton").Click += delegate { Nudge(15); };
            UI<Button>("MinusButton").Click += delegate { Nudge(-15); };
            UI<Button>("ZeroButton").Click += delegate { SetTarget(0); };
            UI<Button>("StartButton").Click += delegate { if(startingKhz.HasValue) SetTarget((int)Math.Round(startingKhz.Value/1000.0)); };
            UI<Button>("AdminButton").Click += delegate { Elevate(); };
            UI<TextBox>("OffsetInput").TextChanged += delegate {
                if (syncing) return;
                editRevision++;
                int v; bool valid=int.TryParse(UI<TextBox>("OffsetInput").Text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out v) && v>=-1000 && v<=1000;
                target=valid ? (int?)v : null;
                if (valid) UpdateSlider(v);
                Update();
            };
            UI<Slider>("OffsetSlider").ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> e) { if(!syncing) SetTarget((int)Math.Round(e.NewValue)); };
            UI<ComboBox>("GpuSelector").SelectionChanged += async delegate {
                if(selecting || nv==null || applying || reading) return;
                var selected=UI<ComboBox>("GpuSelector").SelectedItem as GpuDevice;
                if(selected==null || selected==nv.Selected) return;
                lock(gpuLock) nv.Selected=selected;
                last=null; target=null; startingKhz=null; failed=false;
                Text("CurrentValue","—"); Text("TargetValue","—"); Text("PhysicalClock","읽는 중…");
                SetDevice(); await Refresh(true);
            };
            var refreshCommand=new RoutedCommand();
            Window.CommandBindings.Add(new CommandBinding(refreshCommand,async delegate { if(nv==null) await Initialize(); else await Refresh(true); },delegate(object s,CanExecuteRoutedEventArgs e){e.CanExecute=!reading&&!applying&&!initializing;}));
            Window.InputBindings.Add(new KeyBinding(refreshCommand,new KeyGesture(Key.F5)));
            Window.Loaded += async delegate { await Initialize(); };
            Window.Closing += delegate(object s,System.ComponentModel.CancelEventArgs e) {
                if(applying) { e.Cancel=true; return; }
                closing=true; poll.Stop();
            };
            poll.Interval=TimeSpan.FromSeconds(2);
            poll.Tick += async delegate { if(Window.WindowState!=WindowState.Minimized) await Refresh(false); };
        }
        async Task Initialize() {
            if(initializing) return;
            initializing=true; Update();
            try {
                nv=await Task.Run(()=>new NvApi());
                if(closing) { nv.Dispose(); return; }
                selecting=true;
                UI<ComboBox>("GpuSelector").ItemsSource=nv.Devices;
                UI<ComboBox>("GpuSelector").SelectedItem=nv.Selected;
                UI<ComboBox>("GpuSelector").Visibility=nv.Devices.Count>1?Visibility.Visible:Visibility.Collapsed;
                selecting=false;
                SetDevice(); await Refresh(false); poll.Start();
                if(!failed) AddEvent("GPU 연결 · 설정 읽기 완료");
            } catch(Exception ex) { Error(ex.Message); }
            initializing=false; Update();
            if(captureArgs.Length>1) {
                if(captureArgs.Contains("ui-check")) await RunUiChecks(Path.ChangeExtension(captureArgs[1],".txt"));
                if(captureArgs.Contains("pending") && last!=null) SetTarget((int)Math.Round(last.OffsetKhz/1000.0)+15);
                if(captureArgs.Contains("stress")) {
                    UI<Expander>("CompatibilityExpander").IsExpanded=true;
                    for(int i=0;i<3;i++) AddEvent("화면 검증 기록 "+(i+1)+": 긴 상태 메시지가 표시되어도 아래 안내와 컨트롤이 겹치지 않는지 확인합니다. 이 기록은 화면 검사 전용입니다.");
                }
                await Task.Delay(500); Capture(captureArgs[1]); Window.Close();
            }
        }
        void SetDevice() {
            Text("GpuName",nv.Selected.Name.Replace("NVIDIA ",""));
            Text("DriverText","NVIDIA 드라이버  " + nv.Driver);
            Text("BusText","PCI 버스  " + nv.Selected.Bus);
            Text("TechnicalDetail","제어 대상: XBAR (도메인 1)\n"+(nv.DriverValidated?"드라이버 구조 확인됨":"검증 목록에 없는 드라이버")+"\n읽기 응답은 연결 시마다 검사합니다.\n쓰기 성공 및 오버클럭 안정성은 별도 확인이 필요합니다.");
        }
        async Task Refresh(bool explicitRefresh, Func<Task<Reading>> sampleOverride = null) {
            if(nv==null || reading || applying || closing) return;
            reading=true; Update();
            bool pristine=last==null || (target.HasValue && target.Value*1000==last.OffsetKhz);
            long revisionAtRead=editRevision;
            try {
                Reading next=await (sampleOverride==null ? Task.Run(()=> { lock(gpuLock) return nv.Sample(); }) : sampleOverride());
                if(closing) return;
                bool changed=last!=null && next.OffsetKhz!=last.OffsetKhz;
                last=next; failed=false;
                if(!startingKhz.HasValue) startingKhz=next.OffsetKhz;
                if(pristine && revisionAtRead==editRevision) SetTarget((int)Math.Round(next.OffsetKhz/1000.0));
                ShowReading();
                if(changed) AddEvent("현재 XBAR  " + Signed(next.OffsetKhz/1000.0)+" MHz");
                else if(explicitRefresh) AddEvent("현재 값을 다시 읽었습니다");
            } catch(Exception ex) { Error(ex.Message); }
            finally { reading=false; Update(); }
        }
        void ShowReading() {
            Text("CurrentValue",Signed(last.OffsetKhz/1000.0));
            Text("PhysicalClock",last.ClockMhz.HasValue ? last.ClockMhz.Value.ToString("N0",CultureInfo.InvariantCulture)+" MHz" : "측정 불가");
            Text("LastRead","마지막 읽기  "+last.Time.ToString("HH:mm:ss")+"   ·   2초마다 갱신");
            Text("ConnectionTitle",last.Compatible?"XBAR 연결됨":"읽기 전용");
            Text("ConnectionDetail",!last.Compatible?"현재 드라이버 구조는 미검증 상태입니다. 적용은 사용할 수 없습니다.":NvApi.IsAdmin?"현재 값과 실측 클럭을 읽고 있습니다.":"값을 살펴볼 수 있습니다. 적용할 때는 관리자 권한이 필요합니다.");
            UI<Button>("AdminButton").Visibility=last.Compatible&&!NvApi.IsAdmin?Visibility.Visible:Visibility.Collapsed;
        }
        void UpdateSlider(int v) {
            bool old=syncing; syncing=true;
            var slider=UI<Slider>("OffsetSlider"); slider.Minimum=Math.Min(-300,v); slider.Maximum=Math.Max(500,v); slider.Value=v;
            Text("RangeMin",Signed(slider.Minimum)); Text("RangeMax",Signed(slider.Maximum)); syncing=old;
        }
        void SetTarget(int v) {
            editRevision++;
            v=Math.Max(-1000,Math.Min(1000,v)); target=v; syncing=true;
            UI<TextBox>("OffsetInput").Text=v.ToString(CultureInfo.InvariantCulture); UpdateSlider(v); syncing=false; Update();
        }
        void Nudge(int delta) { if(target.HasValue) SetTarget(target.Value+delta); }
        void Update() {
            bool has=last!=null&&!failed; bool valid=target.HasValue;
            bool dirty=has && valid && target.Value*1000!=last.OffsetKhz;
            foreach(string name in new[]{"OffsetInput","OffsetSlider","PlusButton","MinusButton","ZeroButton","StartButton"}) UI<Control>(name).IsEnabled=has&&!applying;
            UI<Button>("RefreshButton").IsEnabled=!reading&&!applying&&!initializing;
            UI<ComboBox>("GpuSelector").IsEnabled=!reading&&!applying;
            UI<Button>("ApplyButton").IsEnabled=dirty&&!reading&&!applying&&last.Compatible&&NvApi.IsAdmin&&captureArgs.Length==0;
            UI<Button>("ApplyButton").Content=applying?"적용 확인 중…":"XBAR 적용";
            Text("TargetValue",valid?Signed(target.Value):"—");
            bool invalid=has&&!valid;
            Text("InputHint",invalid?"−1000~+1000 사이의 정수를 입력해 주세요.":"1 MHz 단위로 입력 · 버튼으로 15 MHz씩 조정");
            UI<TextBlock>("InputHint").SetResourceReference(TextBlock.ForegroundProperty,invalid?"Error":"Muted");
            UI<TextBox>("OffsetInput").SetResourceReference(Control.BorderBrushProperty,invalid?"Error":"InputLine");
            Text("DeltaText",!has?"현재 값 확인 필요":!valid?"입력값 확인 필요":dirty?"현재 대비 "+Signed(target.Value-last.OffsetKhz/1000.0)+" MHz":"현재 값과 같습니다");
            string badge=failed?"연결 확인 필요":applying?"적용 확인 중":!has?"연결 중":!last.Compatible?"읽기 전용":dirty?"변경 대기":"현재 값";
            Text("StateBadgeText",badge);
            UI<Border>("StateBadge").SetResourceReference(Border.BackgroundProperty,dirty?"PendingSurface":"StatusSurface");
            Text("ApplyStatus",failed?"읽기 실패 · 새로고침해 주세요":!has?"GPU 연결 중":applying?"드라이버 응답을 확인합니다":!valid?"입력값을 확인해 주세요":!last.Compatible?"미검증 드라이버 · 적용 불가":!NvApi.IsAdmin?"적용하려면 관리자 권한 필요":dirty?"XBAR "+Signed(target.Value)+" MHz 준비됨":"변경 사항 없음");
        }
        async Task Apply() {
            if(!UI<Button>("ApplyButton").IsEnabled || captureArgs.Length!=0) return;
            int requested=target.Value, expected=last.OffsetKhz;
            applying=true; Update();
            string failure=null;
            try {
                Reading result=await Task.Run(()=>{lock(gpuLock){nv.Apply(requested,expected); return nv.Sample();}});
                last=result; failed=false; ShowReading();
                SetTarget((int)Math.Round(result.OffsetKhz/1000.0));
                AddEvent("적용 확인  "+Signed(result.OffsetKhz/1000.0)+" MHz");
                if(SystemParameters.ClientAreaAnimation) UI<Border>("StateBadge").BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0.55,1,TimeSpan.FromMilliseconds(220)));
            } catch(Exception ex) {
                failure=ex.Message;
            }
            if(failure!=null) {
                // Re-read after any failure. Never auto-rollback a whole control buffer.
                try { last=await Task.Run(()=>{lock(gpuLock)return nv.Sample();}); ShowReading(); } catch {}
                Error(failure);
            }
            applying=false; Update();
        }
        void Error(string message) {
            failed=true; Text("ConnectionTitle","상태 확인 필요"); Text("ConnectionDetail",message);
            Text("PhysicalClock","—"); Text("LastRead","읽기/적용 오류 · 마지막 표시값을 확인해 주세요");
            UI<Button>("AdminButton").Visibility=Visibility.Collapsed;
            AddEvent(message); Update();
        }
        void AddEvent(string message) {
            string entry=DateTime.Now.ToString("HH:mm:ss")+"\n"+message;
            if(history.Count==0 || history[0]!=entry) history.Insert(0,entry);
            while(history.Count>3) history.RemoveAt(history.Count-1);
            var panel=UI<StackPanel>("EventPanel"); panel.Children.Clear();
            foreach(string item in history) {
                var block=new TextBlock { Text=item,FontSize=12,Margin=new Thickness(0,0,0,12),TextWrapping=TextWrapping.Wrap,LineHeight=19 };
                block.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
                panel.Children.Add(block);
            }
        }
        void Elevate() {
            if(applying || captureArgs.Length!=0) return;
            try {
                // Relaunch starts from live readings; it never passes a pending offset.
                Process.Start(new ProcessStartInfo { FileName=Assembly.GetExecutingAssembly().Location,UseShellExecute=true,Verb="runas",Arguments="--elevated" });
                Window.Close();
            } catch(System.ComponentModel.Win32Exception) { AddEvent("관리자 실행이 취소되었습니다"); }
        }
        void ChangeTheme(bool dark,bool persist) {
            changingTheme=true;
            try {
                darkTheme=dark; Theme.Apply(Window,dark);
                UI<RadioButton>("LightTheme").IsChecked=!dark;
                UI<RadioButton>("DarkTheme").IsChecked=dark;
            } finally { changingTheme=false; }
            if(persist && captureArgs.Length==0) {
                try { Theme.Save(Theme.SettingsPath,dark); }
                catch(IOException) { AddEvent("테마는 변경됐지만 저장하지 못했습니다. 다음 실행 때 다시 선택해 주세요."); }
                catch(UnauthorizedAccessException) { AddEvent("테마 저장 권한이 없습니다. 다음 실행 때 다시 선택해 주세요."); }
            }
        }
        void Capture(string path) {
            poll.Stop(); Window.UpdateLayout();
            var root=Window.Content as FrameworkElement;
            var size=new Size(Window.ActualWidth,Window.ActualHeight);
            var visual=new DrawingVisual();
            using(var drawing=visual.RenderOpen()) {
                drawing.DrawRectangle(Window.Background,null,new Rect(size));
                var brush=new VisualBrush(Window) { Stretch=Stretch.Fill };
                drawing.DrawRectangle(brush,null,new Rect(size));
            }
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(size.Width),(int)Math.Ceiling(size.Height),96,96,PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string full=Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full));
            using(var stream=File.Create(full)) encoder.Save(stream);
        }
        async Task RunUiChecks(string path) {
            poll.Stop();
            var results=new List<string>();
            Action<bool,string> check=(ok,name)=>{results.Add((ok?"PASS ":"FAIL ")+name);};
            check(last!=null&&!failed,"Live reading reaches the UI");
            if(last!=null) {
                int initial=(int)Math.Round(last.OffsetKhz/1000.0);
                string current=UI<TextBlock>("CurrentValue").Text;
                SetTarget(initial);
                UI<Button>("PlusButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(target==Math.Min(1000,initial+15),"Plus button prepares +15 MHz");
                check(UI<TextBlock>("CurrentValue").Text==current,"Preparing a value does not change driver readback");
                UI<Button>("MinusButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(target==initial,"Minus button returns to prior target");
                UI<TextBox>("OffsetInput").Text="invalid";
                check(!target.HasValue&&!UI<Button>("ApplyButton").IsEnabled,"Non-numeric entry blocks apply");
                UI<TextBox>("OffsetInput").Text="1001";
                check(!target.HasValue&&!UI<Button>("ApplyButton").IsEnabled,"Out-of-range entry blocks apply");
                UI<TextBox>("OffsetInput").Text="-150";
                check(target==-150 && UI<Slider>("OffsetSlider").Value==-150,"Signed number synchronizes slider");
                UI<Slider>("OffsetSlider").Value=30;
                check(target==30&&UI<TextBox>("OffsetInput").Text=="30","Slider synchronizes number input");
                UI<Button>("ZeroButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(target==0&&UI<TextBlock>("CurrentValue").Text==current,"Zero prepares only, no implicit apply");
                UI<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(target==initial,"Session starting value is recoverable");
                check(!UI<Button>("ApplyButton").IsEnabled,"Capture / inspection mode cannot write hardware");
                SetTarget(initial);
                var delayed=new TaskCompletionSource<Reading>();
                Task inFlight=Refresh(false,()=>delayed.Task);
                SetTarget(initial+15);
                delayed.SetResult(last); await inFlight;
                check(target==Math.Min(1000,initial+15),"An edit during a delayed poll is preserved");
                SetTarget(initial);
                var delayedInvalid=new TaskCompletionSource<Reading>();
                Task secondRead=Refresh(false,()=>delayedInvalid.Task);
                UI<TextBox>("OffsetInput").Text="unfinished";
                delayedInvalid.SetResult(last); await secondRead;
                check(!target.HasValue&&UI<TextBox>("OffsetInput").Text=="unfinished","Incomplete input survives an in-flight refresh");
                SetTarget(initial);
            }
            results.Add("Hardware writes performed: 0");
            string full=Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllLines(full,results);
        }
    }
}
