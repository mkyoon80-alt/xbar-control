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
                if (args.Length > 1 && args[0] == "--validate-startup-task") {
                    StartupTask.ValidateOnly(); bool registered=StartupTask.IsRegistered(); File.WriteAllText(args[1],"PASS Windows Task Scheduler validation and registration query. Existing task: "+registered+". No task created; no GPU API calls."); return 0;
                }
                int owner=Array.IndexOf(args,"--owner");
                if(owner>=0 && (owner+1>=args.Length || args[owner+1]!=StartupTask.UserSid)) throw new InvalidOperationException("같은 Windows 계정으로 관리자 권한을 승인해 주세요.");
                if (args.Length > 1 && args[0] == "--diagnose") {
                    var d = new Diagnostic(); int exit = 0;
                    try { using (var nv = new NvApi()) { var s = nv.Sample(); d.Gpu=nv.Selected.Name; d.Driver=nv.Driver; d.ImplementationSha256=nv.ImplementationHash; d.Administrator=NvApi.IsAdmin; d.StructureValidated=s.Compatible; d.OffsetKhz=s.OffsetKhz; d.MeasuredClockMhz=s.ClockMhz; d.Timestamp=s.Time.ToString("o"); } }
                    catch (Exception ex) { d.Error=ex.Message; exit=1; }
                    using (var f = File.Create(args[1])) new DataContractJsonSerializer(typeof(Diagnostic)).WriteObject(f,d);
                    return exit;
                }
                bool capture = args.Length > 1 && args[0] == "--capture";
                if(args.Contains("--startup") && !capture) {
                    try { if(!new StartupStore(StartupStore.DefaultDirectory).Load().WindowsLogon) return 0; } catch { /* Show the recovery UI for unreadable settings. */ }
                }
                // Only one interactive instance can issue writes for this user.
                bool created;
                using (var mutex = new Mutex(true, "Local\\XbarControl.Desktop.v1", out created)) {
                    if (!created && args.Contains("--elevated")) {
                        try { created=mutex.WaitOne(5000); } catch(AbandonedMutexException) { created=true; }
                    }
                    if (!created && !capture) { if(!args.Contains("--startup")) MessageBox.Show("XBAR Control이 이미 실행 중입니다. 트레이 아이콘이 있으면 더블클릭해 창을 열어 주세요.", "XBAR Control"); return 0; }
                    var app = new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { MessageBox.Show(e.Exception.Message,"XBAR Control"); e.Handled=true; };
                    var panel = new ControlPanel(args);
                    app.MainWindow=panel.Window;
                    app.Exit+=delegate { panel.DisposeTray(); };
                    app.Dispatcher.BeginInvoke(new Action(panel.Start));
                    app.Run();
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
        NvApi nv; Reading last; int? target;
        bool syncing, reading, applying, failed, closing, selecting, initializing;
        long editRevision;
        bool darkTheme, changingTheme;
        StartupSettings startup = new StartupSettings();
        readonly StartupStore startupStore = new StartupStore(StartupStore.DefaultDirectory);
        readonly string[] launchArgs;
        bool startupBusy, startupPending, startupCancelled, startupHandled;
        bool taskRegistered;
        TrayHost tray;
        bool initializationDispatched, exitRequested, closeToTray;
        string startupMessage = "현재 적용값을 저장한 뒤 켜 주세요.";
        readonly string[] captureArgs; readonly object gpuLock = new object();
        readonly DispatcherTimer poll = new DispatcherTimer();
        readonly List<string> history = new List<string>();
        T UI<T>(string name) where T : class { return Window.FindName(name) as T; }
        void Text(string name,string value) { UI<TextBlock>(name).Text=value; }
        static string Signed(double value) { return value.ToString("+0.###;−0.###;0",CultureInfo.InvariantCulture); }
        public ControlPanel(string[] capture) {
            launchArgs=capture;
            captureArgs=capture.Length>1 && capture[0]=="--capture" ? capture : new string[0];
            using (var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window=(Window)XamlReader.Load(stream);
            ChangeTheme(captureArgs.Length>0 ? captureArgs.Contains("dark") : Theme.Load(Theme.SettingsPath),false);
            if(captureArgs.Length==0) {
                try {
                    startup=startupStore.Load();
                    if(startupStore.Interrupted) {
                        startup.AppStart=false; startup.WindowsLogon=false; startupStore.Save(startup);
                        startupMessage="이전 자동 적용이 완료되지 않아 중지했습니다. 현재 값을 확인하고 다시 저장해 주세요.";
                    } else if(startup.HasProfile) startupMessage="저장한 값만 자동 적용합니다. 입력 중인 값은 저장되지 않습니다.";
                } catch(Exception ex) { startup=new StartupSettings(); startupMessage="설정을 읽지 못해 자동 적용을 껐습니다. "+ex.Message; }
            }
            UI<RadioButton>("LightTheme").Checked += delegate { if(!changingTheme) ChangeTheme(false,true); };
            UI<RadioButton>("DarkTheme").Checked += delegate { if(!changingTheme) ChangeTheme(true,true); };
            Window.SourceInitialized += delegate { Theme.ApplyTitleBar(Window,darkTheme); };
            if (captureArgs.Length >= 4) { Window.Width=int.Parse(captureArgs[2]); Window.Height=int.Parse(captureArgs[3]); }
            UI<Button>("RefreshButton").Click += async delegate { if(nv==null) await Initialize(); else await Refresh(true); };
            UI<Button>("ApplyButton").Click += async delegate { await Apply(); };
            UI<Button>("PlusButton").Click += delegate { Nudge(15); };
            UI<Button>("MinusButton").Click += delegate { Nudge(-15); };
            UI<Button>("ResetButton").Click += delegate { CancelStartup(); SetTarget(0); };
            UI<Button>("AdminButton").Click += delegate { Elevate(false); };
            UI<Button>("SaveStartupButton").Click += async delegate { await SaveStartupValue(); };
            UI<CheckBox>("AppStartCheck").Click += async delegate { await ChangeStartup(false); };
            UI<CheckBox>("WindowsStartCheck").Click += async delegate { await ChangeStartup(true); };
            UI<CheckBox>("TrayStartCheck").Click += async delegate { await ChangeTrayStartup(); };
            UI<Button>("CancelStartupButton").Click += delegate { CancelStartup(); };
            UI<TextBox>("OffsetInput").TextChanged += delegate {
                if (syncing) return;
                CancelStartup();
                editRevision++;
                int v; bool valid=int.TryParse(UI<TextBox>("OffsetInput").Text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out v) && v>=-1000 && v<=1000;
                target=valid ? (int?)v : null;
                if (valid) UpdateSlider(v);
                Update();
            };
            UI<Slider>("OffsetSlider").ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> e) { if(!syncing) { CancelStartup(); SetTarget((int)Math.Round(e.NewValue)); } };
            UI<ComboBox>("GpuSelector").SelectionChanged += async delegate {
                if(selecting || nv==null || applying || reading || startupBusy) return;
                var selected=UI<ComboBox>("GpuSelector").SelectedItem as GpuDevice;
                if(selected==null || selected==nv.Selected) return;
                CancelStartup(); lock(gpuLock) nv.Selected=selected;
                last=null; target=null; failed=false;
                Text("CurrentValue","—"); Text("TargetValue","—"); Text("PhysicalClock","읽는 중…");
                SetDevice(); await Refresh(true);
            };
            var refreshCommand=new RoutedCommand();
            Window.CommandBindings.Add(new CommandBinding(refreshCommand,async delegate { if(nv==null) await Initialize(); else await Refresh(true); },delegate(object s,CanExecuteRoutedEventArgs e){e.CanExecute=!reading&&!applying&&!initializing&&!startupBusy&&!startupPending;}));
            Window.InputBindings.Add(new KeyBinding(refreshCommand,new KeyGesture(Key.F5)));
            Window.Loaded += async delegate { if(!initializationDispatched) { initializationDispatched=true; await Initialize(); } };
            Window.StateChanged += delegate {
                if(Window.WindowState!=WindowState.Minimized) return;
                if(EnsureTray()) Window.Hide();
                else { Window.WindowState=WindowState.Normal; Window.Show(); }
            };
            Window.Closing += delegate(object s,System.ComponentModel.CancelEventArgs e) {
                if(applying || startupBusy) { e.Cancel=true; return; }
                CancelStartup();
                if(closeToTray && tray!=null && !exitRequested) { e.Cancel=true; Window.Hide(); return; }
                closing=true; poll.Stop();
            };
            Window.Closed += delegate { DisposeTray(); };
            poll.Interval=TimeSpan.FromSeconds(2);
            poll.Tick += async delegate { if(Window.IsVisible && Window.WindowState!=WindowState.Minimized) await Refresh(false); };
        }
        public async void Start() {
            bool skip=launchArgs.Contains("--skip-auto") || (Keyboard.Modifiers&ModifierKeys.Shift)!=0;
            if(startup.TrayRequested(launchArgs.Contains("--startup"),skip,captureArgs.Length!=0) || captureArgs.Contains("tray-check")) {
                if(!EnsureTray()) { Window.Show(); return; }
                closeToTray=true;
                initializationDispatched=true;
                await Initialize();
            } else Window.Show();
        }
        bool EnsureTray() {
            if(tray!=null) return true;
            try { tray=new TrayHost(ShowWindow,CancelStartup,ExitApplication); Update(); return true; }
            catch(Exception ex) { DisposeTray(); startupMessage="트레이를 열지 못해 창을 표시합니다. "+ex.Message; AddEvent(startupMessage); Update(); return false; }
        }
        void ShowWindow() {
            if(closing) return;
            Window.Show(); Window.WindowState=WindowState.Normal; Window.Activate();
        }
        void ExitApplication() {
            if(applying || startupBusy) return;
            exitRequested=true; Window.Close();
        }
        public void DisposeTray() {
            if(tray!=null) { tray.Dispose(); tray=null; }
        }
        void ShowStartupIssue() {
            if(tray==null || closing || Window.IsVisible) return;
            if(captureArgs.Length==0) tray.Notify(startupMessage,true);
            ShowWindow();
        }
        async Task Initialize() {
            if(initializing) return;
            initializing=true; Update();
            try {
                nv=await Task.Run(()=>new NvApi());
                if(closing) { nv.Dispose(); return; }
                if(startup.HasProfile && captureArgs.Length==0) {
                    var matches=nv.Devices.Where(g=>g.Name==startup.GpuName && g.Bus==startup.Bus).ToArray();
                    if(matches.Length==1) nv.Selected=matches[0];
                }
                if(captureArgs.Length==0) {
                    try {
                        taskRegistered=await Task.Run(()=>StartupTask.IsRegistered());
                        if(taskRegistered && startupStore.Interrupted && NvApi.IsAdmin) { await Task.Run(()=>StartupTask.Remove()); taskRegistered=false; }
                    }
                    catch(Exception ex) { startupMessage="Windows 시작 등록 상태를 확인하지 못했습니다. "+ex.Message; }
                }
                selecting=true;
                UI<ComboBox>("GpuSelector").ItemsSource=nv.Devices;
                UI<ComboBox>("GpuSelector").SelectedItem=nv.Selected;
                UI<ComboBox>("GpuSelector").Visibility=nv.Devices.Count>1?Visibility.Visible:Visibility.Collapsed;
                selecting=false;
                SetDevice(); await Refresh(false); poll.Start();
                if(!failed) AddEvent("GPU 연결 · 설정 읽기 완료");
            } catch(Exception ex) { Error(ex.Message); }
            initializing=false; Update();
            if(captureArgs.Length==0 && !startupHandled) { startupHandled=true; await ApplyAtStartup(); }
            if(captureArgs.Length>1) {
                if(captureArgs.Contains("tray-check")) await RunTrayChecks(Path.ChangeExtension(captureArgs[1],"tray.txt"));
                if(captureArgs.Contains("tray-minimize-check")) await RunMinimizeChecks(Path.ChangeExtension(captureArgs[1],"minimize.txt"));
                if(captureArgs.Contains("ui-check")) await RunUiChecks(Path.ChangeExtension(captureArgs[1],".txt"));
                if(captureArgs.Contains("pending") && last!=null) SetTarget((int)Math.Round(last.OffsetKhz/1000.0)+15);
                if(captureArgs.Contains("startup-preview") && last!=null) {
                    startup=new StartupSettings { HasProfile=true, AppStart=true, WindowsLogon=true, StartInTray=true, GpuName=nv.Selected.Name, Bus=nv.Selected.Bus, Driver=nv.Driver, ImplementationHash=nv.ImplementationHash, OffsetMhz=last.OffsetKhz/1000 };
                    startupPending=true; startupMessage="5초 후 저장값 "+Signed(startup.OffsetMhz)+" MHz를 적용합니다. (화면 검사)"; Update();
                }
                if(captureArgs.Contains("stress")) {
                    UI<Expander>("CompatibilityExpander").IsExpanded=true;
                    for(int i=0;i<3;i++) AddEvent("화면 검증 기록 "+(i+1)+": 긴 상태 메시지가 표시되어도 아래 안내와 컨트롤이 겹치지 않는지 확인합니다. 이 기록은 화면 검사 전용입니다.");
                }
                await Task.Delay(500); Capture(captureArgs[1]);
                if((captureArgs.Contains("tray-check") || captureArgs.Contains("tray-minimize-check")) && tray!=null) {
                    var exitingTray=tray;
                    bool manual=captureArgs.Contains("tray-minimize-check");
                    Window.Closed+=delegate { File.AppendAllText(Path.ChangeExtension(captureArgs[1],manual?"minimize.txt":"tray.txt"),(exitingTray.Visible?"FAIL ":"PASS ")+(manual?"Closing a manually launched window exits and removes its tray icon":"Tray Exit closes the app and removes the icon")+"\r\n"); };
                    if(manual) Window.Close(); else tray.ExitItem.PerformClick();
                } else ExitApplication();
            }
        }
        void SetDevice() {
            Text("GpuName",nv.Selected.Name.Replace("NVIDIA ",""));
            Text("DriverText","NVIDIA 드라이버  " + nv.Driver);
            Text("BusText","PCI 버스  " + nv.Selected.Bus);
            Text("TechnicalDetail","제어 대상: XBAR (도메인 1)\n"+(nv.DriverValidated?"드라이버 구조 확인됨":"검증 목록에 없는 드라이버")+"\n읽기 응답은 연결 시마다 검사합니다.\n쓰기 성공 및 오버클럭 안정성은 별도 확인이 필요합니다.");
        }
        async Task Refresh(bool explicitRefresh, Func<Task<Reading>> sampleOverride = null) {
            if(nv==null || reading || applying || closing || startupBusy) return;
            reading=true; Update();
            bool pristine=last==null || (target.HasValue && target.Value*1000==last.OffsetKhz);
            long revisionAtRead=editRevision;
            try {
                Reading next=await (sampleOverride==null ? Task.Run(()=> { lock(gpuLock) return nv.Sample(); }) : sampleOverride());
                if(closing) return;
                bool changed=last!=null && next.OffsetKhz!=last.OffsetKhz;
                last=next; failed=false;
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
        void Nudge(int delta) { CancelStartup(); if(target.HasValue) SetTarget(target.Value+delta); }
        void Update() {
            bool has=last!=null&&!failed; bool valid=target.HasValue;
            bool dirty=has && valid && target.Value*1000!=last.OffsetKhz;
            foreach(string name in new[]{"OffsetInput","OffsetSlider","PlusButton","MinusButton","ResetButton"}) UI<Control>(name).IsEnabled=has&&!applying&&!startupBusy;
            UI<Button>("RefreshButton").IsEnabled=!reading&&!applying&&!initializing&&!startupBusy&&!startupPending;
            UI<ComboBox>("GpuSelector").IsEnabled=!reading&&!applying&&!startupBusy;
            UI<Button>("ApplyButton").IsEnabled=dirty&&!reading&&!applying&&!startupBusy&&!startupPending&&last.Compatible&&NvApi.IsAdmin&&captureArgs.Length==0;
            UI<Button>("ApplyButton").Content=applying?"적용 확인 중…":"XBAR 적용";
            Text("TargetValue",valid?Signed(target.Value):"—");
            bool invalid=has&&!valid;
            Text("InputHint",invalid?"−1000~+1000 사이의 정수를 입력해 주세요.":"1 MHz 단위로 입력 · 버튼으로 15 MHz씩 조정");
            UI<TextBlock>("InputHint").SetResourceReference(TextBlock.ForegroundProperty,invalid?"Error":"Muted");
            UI<TextBox>("OffsetInput").SetResourceReference(Control.BorderBrushProperty,invalid?"Error":"InputLine");
            Text("DeltaText",!has?"현재 값 확인 필요":!valid?"입력값 확인 필요":dirty?"현재 대비 "+Signed(target.Value-last.OffsetKhz/1000.0)+" MHz":"현재 값과 같습니다");
            string badge=failed?"연결 확인 필요":applying?"적용 확인 중":!has?"연결 중":!last.Compatible?"읽기 전용":dirty?"변경 대기":"변경 없음";
            Text("StateBadgeText",badge);
            UI<Border>("StateBadge").SetResourceReference(Border.BackgroundProperty,dirty?"PendingSurface":"StatusSurface");
            Text("ApplyStatus",failed?"읽기 실패 · 새로고침해 주세요":!has?"GPU 연결 중":applying?"드라이버 응답을 확인합니다":!valid?"입력값을 확인해 주세요":!last.Compatible?"미검증 드라이버 · 적용 불가":!NvApi.IsAdmin?"적용하려면 관리자 권한 필요":dirty?"XBAR "+Signed(target.Value)+" MHz 준비됨":"변경 사항 없음");
            UpdateStartup();
        }
        async Task<bool> Apply(bool automatic=false) {
            if((!automatic && !UI<Button>("ApplyButton").IsEnabled) || captureArgs.Length!=0 || last==null || failed || !last.Compatible || !NvApi.IsAdmin || !target.HasValue || applying || reading) return false;
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
            applying=false; Update(); return failure==null;
        }
        void UpdateStartup() {
            bool idle=!startupBusy&&!applying&&!reading&&!initializing&&!startupPending;
            bool usable=last!=null&&!failed&&last.Compatible;
            UI<CheckBox>("AppStartCheck").IsChecked=startup.AppStart;
            UI<CheckBox>("WindowsStartCheck").IsChecked=startup.WindowsLogon;
            UI<CheckBox>("TrayStartCheck").IsChecked=startup.StartInTray;
            UI<CheckBox>("TrayStartCheck").IsEnabled=idle&&captureArgs.Length==0&&NvApi.IsAdmin&&startup.WindowsLogon;
            UI<CheckBox>("AppStartCheck").IsEnabled=idle&&captureArgs.Length==0&&(startup.AppStart || (startup.HasProfile&&usable));
            UI<CheckBox>("WindowsStartCheck").IsEnabled=idle&&captureArgs.Length==0&&NvApi.IsAdmin&&(startup.WindowsLogon || taskRegistered || (startup.HasProfile&&usable));
            UI<Button>("SaveStartupButton").IsEnabled=idle&&usable&&captureArgs.Length==0&&(!startup.WindowsLogon||NvApi.IsAdmin);
            UI<Button>("SaveStartupButton").Content=startupBusy?"처리 중…":"자동 적용값 저장";
            UI<Button>("CancelStartupButton").Visibility=startupPending?Visibility.Visible:Visibility.Collapsed;
            Text("SavedStartupValue",startup.HasProfile?Signed(startup.OffsetMhz)+" MHz":"미저장");
            bool edited=last!=null&&(!target.HasValue||target.Value*1000!=last.OffsetKhz);
            Text("SaveStartupHint",!usable?"GPU의 현재 적용값을 확인한 뒤 저장해 주세요.":edited?"현재 적용된 "+Signed(last.OffsetKhz/1000.0)+" MHz를 저장합니다. 입력 중인 값은 먼저 XBAR 적용을 눌러 주세요.":"현재 적용된 "+Signed(last.OffsetKhz/1000.0)+" MHz를 저장합니다.");
            Text("SavedStartupDevice",startup.HasProfile?startup.GpuName.Replace("NVIDIA ","")+" · PCI "+startup.Bus+"\n드라이버 "+startup.Driver:"현재 적용값을 저장해서 사용합니다.");
            Text("StartupDetail",startupBusy?"시작 설정을 저장하고 있습니다…":startupMessage);
            Text("StartupPermission",NvApi.IsAdmin?"":"Windows 시작 설정은 상단에서 관리자 권한으로 연 뒤 변경해 주세요.");
            if(!NvApi.IsAdmin && captureArgs.Length==0) UI<Button>("AdminButton").Visibility=Visibility.Visible;
            if(tray!=null) tray.Update(startupMessage,startupPending,!applying&&!startupBusy);
        }
        void CancelStartup() {
            if(!startupPending) return;
            startupCancelled=true; startupPending=false;
            startupMessage="이번 자동 적용을 취소했습니다. 다음 시작 설정은 유지됩니다.";
            Update();
        }
        async Task SaveStartupValue() {
            if(captureArgs.Length!=0 || !UI<Button>("SaveStartupButton").IsEnabled) return;
            startupBusy=true; Update();
            try {
                Reading current=await Task.Run(()=>{lock(gpuLock)return nv.Sample();});
                if(!current.Compatible || current.OffsetKhz%1000!=0) throw new InvalidOperationException("현재 드라이버와 오프셋을 확인한 뒤 저장해 주세요.");
                var next=startup.Copy(); next.HasProfile=true; next.OffsetMhz=current.OffsetKhz/1000;
                next.GpuName=nv.Selected.Name; next.Bus=nv.Selected.Bus; next.Driver=nv.Driver; next.ImplementationHash=nv.ImplementationHash;
                if(next.WindowsLogon) { await Task.Run(()=>StartupTask.Register()); taskRegistered=true; }
                startupStore.Save(next); startup=next; startupStore.ClearAttempt();
                last=current; failed=false; ShowReading();
                startupMessage="자동 적용값 "+Signed(next.OffsetMhz)+" MHz 저장 완료. "+(next.AppStart||next.WindowsLogon?"다음 시작부터 이 값을 사용합니다.":"사용할 시작 옵션을 켜 주세요.");
                AddEvent("자동 적용 값 저장  "+Signed(next.OffsetMhz)+" MHz");
            } catch(Exception ex) { startupMessage="저장 실패: "+ex.Message; }
            finally { startupBusy=false; Update(); }
        }
        async Task ChangeStartup(bool windows) {
            if(captureArgs.Length!=0) { Update(); return; }
            bool enabled=UI<CheckBox>(windows?"WindowsStartCheck":"AppStartCheck").IsChecked==true;
            if(startupBusy || applying || reading || initializing || startupPending) { Update(); return; }
            var next=startup.Copy(); bool newlyRegistered=false;
            startupBusy=true; Update();
            try {
                if(enabled && (!next.HasProfile || startupStore.Interrupted || last==null || failed || !last.Compatible || !next.Matches(nv.Selected,nv.Driver,nv.ImplementationHash)))
                    throw new InvalidOperationException("현재 GPU의 적용값을 먼저 저장해 주세요.");
                if(windows) {
                    if(!NvApi.IsAdmin) throw new UnauthorizedAccessException("관리자 권한으로 열고 다시 선택해 주세요.");
                    next.WindowsLogon=enabled;
                    if(enabled) { await Task.Run(()=>StartupTask.Register()); newlyRegistered=!startup.WindowsLogon; taskRegistered=true; }
                    else { startupStore.Save(next); startup=next; await Task.Run(()=>StartupTask.Remove()); taskRegistered=false; }
                } else next.AppStart=enabled;
                startupStore.Save(next); startup=next;
                startupMessage=(windows?"Windows 시작 시 적용":"앱 실행 시 자동 적용")+(enabled?(windows?"을 켰습니다. 다음 로그인 30초 후 실행됩니다.":"을 켰습니다. 다음 시작부터 동작합니다."):"을 껐습니다.");
            } catch(Exception ex) {
                if(newlyRegistered) { try { StartupTask.Remove(); taskRegistered=false; } catch { } }
                startupMessage="시작 설정 변경 실패: "+ex.Message;
            } finally { startupBusy=false; Update(); }
        }
        async Task ChangeTrayStartup() {
            if(captureArgs.Length!=0 || !UI<CheckBox>("TrayStartCheck").IsEnabled) { Update(); return; }
            var next=startup.Copy(); next.StartInTray=UI<CheckBox>("TrayStartCheck").IsChecked==true;
            startupBusy=true; Update();
            try {
                // Update the installed copy as well, so old logon tasks gain tray support.
                await Task.Run(()=>StartupTask.Register()); taskRegistered=true;
                startupStore.Save(next); startup=next;
                startupMessage=next.StartInTray?"다음 Windows 로그인부터 트레이로 시작합니다. 아이콘을 더블클릭하면 창이 열립니다.":"다음 Windows 로그인부터 창을 표시합니다.";
            } catch(Exception ex) { startupMessage="트레이 시작 설정 변경 실패: "+ex.Message; }
            finally { startupBusy=false; Update(); }
        }
        async Task ApplyAtStartup() {
            bool logon=launchArgs.Contains("--startup");
            bool skip=launchArgs.Contains("--skip-auto") || (Keyboard.Modifiers&ModifierKeys.Shift)!=0;
            if(!startup.Requested(logon,skip,captureArgs.Length!=0)) return;
            if(last==null || failed || !last.Compatible || !startup.Matches(nv.Selected,nv.Driver,nv.ImplementationHash) || startupStore.Interrupted) {
                startupMessage="자동 적용을 건너뛰었습니다. GPU·드라이버와 현재 값을 확인하고 다시 저장해 주세요."; Update(); ShowStartupIssue(); return;
            }
            if(!NvApi.IsAdmin) {
                if(!logon) Elevate(true);
                else { startupMessage="관리자 권한이 없어 자동 적용하지 않았습니다. Windows 시작 옵션을 다시 등록해 주세요."; Update(); ShowStartupIssue(); }
                return;
            }
            poll.Stop(); startupPending=true; startupCancelled=false;
            int planned=startup.OffsetMhz;
            try {
                for(int seconds=5;seconds>0;seconds--) {
                    startupMessage=seconds+"초 후 저장값 "+Signed(planned)+" MHz를 적용합니다."; Update();
                    if(seconds==5 && tray!=null) tray.Notify(startupMessage+" 트레이 우클릭 메뉴에서 취소할 수 있습니다.");
                    await Task.Delay(1000);
                    if(closing || startupCancelled) return;
                }
                var latest=startupStore.Load();
                if(!latest.Requested(logon,false,false) || latest.OffsetMhz!=planned || !latest.Matches(nv.Selected,nv.Driver,nv.ImplementationHash))
                    throw new InvalidOperationException("저장 설정이 변경되어 이번 자동 적용을 중지했습니다.");
                await Refresh(false);
                if(closing || startupCancelled) return;
                if(last==null || failed || !last.Compatible) { startupMessage="현재 값을 확인하지 못해 이번 자동 적용을 건너뛰었습니다."; ShowStartupIssue(); return; }
                SetTarget(planned); startupStore.BeginAttempt(); startupPending=false;
                if(!await Apply(true)) throw new InvalidOperationException("자동 적용을 확인하지 못했습니다. 현재 값을 확인하고 다시 저장해 주세요.");
                startupStore.ClearAttempt(); startupMessage="자동 적용 완료  "+Signed(planned)+" MHz";
                AddEvent(startupMessage);
                if(logon && tray==null) Window.WindowState=WindowState.Minimized;
            } catch(Exception ex) {
                startup.AppStart=false; startup.WindowsLogon=false;
                try { startupStore.Save(startup); } catch { /* The pending marker keeps subsequent launches fail-closed. */ }
                try { StartupTask.Remove(); taskRegistered=false; } catch { }
                startupMessage=ex.Message+" 자동 적용을 껐습니다."; AddEvent(startupMessage);
                ShowStartupIssue();
            } finally { startupPending=false; if(!closing) poll.Start(); Update(); }
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
        void Elevate(bool autoResume) {
            if(applying || startupBusy || captureArgs.Length!=0) return;
            try {
                Process.Start(new ProcessStartInfo { FileName=Assembly.GetExecutingAssembly().Location,UseShellExecute=true,Verb="runas",Arguments="--elevated --owner "+StartupTask.UserSid+(autoResume?"":" --skip-auto") });
                ExitApplication();
            } catch(System.ComponentModel.Win32Exception) { startupMessage="관리자 실행이 취소되어 이번 자동 적용을 건너뛰었습니다."; AddEvent("관리자 실행이 취소되었습니다"); Update(); }
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
                UI<Button>("ResetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(target==0&&UI<TextBlock>("CurrentValue").Text==current,"Single reset prepares zero without applying");
                check(Window.FindName("StartButton")==null && Window.FindName("ZeroButton")==null,"Duplicate reset controls are removed");
                check(!startup.AppStart&&!startup.WindowsLogon,"Both startup options default off in capture mode");
                await SaveStartupValue();
                check(!startup.HasProfile,"Capture mode cannot persist startup settings");
                UI<CheckBox>("WindowsStartCheck").IsChecked=true; await ChangeStartup(true);
                check(!startup.WindowsLogon&&UI<CheckBox>("WindowsStartCheck").IsChecked==false,"Capture mode cannot register a startup task");
                UI<CheckBox>("TrayStartCheck").IsChecked=true; await ChangeTrayStartup();
                check(!startup.StartInTray&&UI<CheckBox>("TrayStartCheck").IsChecked==false,"Capture mode cannot save tray startup or replace the installed app");
                startupPending=true; startupCancelled=false; Update();
                check(!((RoutedCommand)Window.CommandBindings[0].Command).CanExecute(null,Window),"F5 cannot race the startup countdown");
                UI<Button>("CancelStartupButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(startupCancelled&&!startupPending,"Countdown cancellation blocks automatic apply");
                startupPending=true; startupCancelled=false; SetTarget(0);
                UI<TextBox>("OffsetInput").Text="15";
                check(startupCancelled&&!startupPending,"Editing cancels the pending automatic apply");
                startupMessage="현재 적용값을 저장한 뒤 켜 주세요.";
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
            Window.UpdateLayout();
            var manualScroll=UI<ScrollViewer>("ManualScroll");
            check(manualScroll.ExtentHeight<=manualScroll.ViewportHeight+1,"Manual inputs remain visible without scrolling at this window size");
            results.Add("Hardware writes performed: 0");
            string full=Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllLines(full,results);
        }
        async Task RunTrayChecks(string path) {
            poll.Stop();
            var results=new List<string>();
            Action<bool,string> check=(ok,name)=>results.Add((ok?"PASS ":"FAIL ")+name);
            check(tray!=null&&tray.Visible&&!Window.IsVisible,"Tray startup creates an icon without showing the window");
            if(tray!=null) {
                check(!startup.AppStart&&!startup.WindowsLogon&&!startup.HasProfile,"Tray inspection does not load or enable user startup settings");
                tray.OpenItem.PerformClick(); await Task.Delay(80);
                check(Window.IsVisible&&Window.WindowState==WindowState.Normal,"Tray Open restores the control panel");
                startupPending=true; startupCancelled=false; Update();
                check(tray.CancelItem.Enabled,"Tray cancellation is available during the countdown");
                tray.CancelItem.PerformClick();
                check(startupCancelled&&!startupPending&&!tray.CancelItem.Enabled,"Tray cancellation stops the pending apply and updates the menu");
                applying=true; Update();
                check(!tray.ExitItem.Enabled,"Tray Exit is disabled while a write is in progress");
                applying=false; Update();
                startupPending=true; startupCancelled=false; Update(); Window.Close();
                check(!Window.IsVisible&&!closing&&tray.Visible&&startupCancelled,"Closing a tray window cancels the countdown and keeps the icon alive");
                tray.OpenItem.PerformClick(); Window.WindowState=WindowState.Minimized;
                check(!Window.IsVisible&&tray.Visible,"Minimizing a tray window hides it from the taskbar");
                startupMessage="화면 검사: 자동 적용을 확인하지 못했습니다.";
                ShowStartupIssue();
                check(Window.IsVisible&&Window.WindowState==WindowState.Normal,"A startup problem restores the window for recovery");
                var old=tray; DisposeTray();
                check(!old.Visible,"Disposing the tray removes the notification icon");
                tray=new TrayHost(ShowWindow,CancelStartup,ExitApplication);
                check(tray.Visible&&!old.Visible,"A replacement tray does not retain the old icon");
                startupMessage="현재 적용값을 저장한 뒤 켜 주세요."; Update();
            }
            results.Add("Hardware writes performed: 0; startup settings and tasks unchanged.");
            File.WriteAllLines(Path.GetFullPath(path),results);
        }
        async Task RunMinimizeChecks(string path) {
            poll.Stop();
            var results=new List<string>();
            Action<bool,string> check=(ok,name)=>results.Add((ok?"PASS ":"FAIL ")+name);
            check(Window.IsVisible&&tray==null,"Manual launch initially shows a window without a tray icon");
            Window.WindowState=WindowState.Minimized;
            check(!Window.IsVisible&&tray!=null&&tray.Visible,"Minimizing a manual launch creates a tray icon and hides the window");
            if(tray!=null) {
                tray.OpenItem.PerformClick(); await Task.Delay(80);
                check(Window.IsVisible&&Window.WindowState==WindowState.Normal,"The minimized manual window can be restored from its tray menu");
                Window.WindowState=WindowState.Minimized; tray.OpenItem.PerformClick();
                check(Window.IsVisible&&tray.Visible&&!startup.AppStart&&!startup.WindowsLogon,"Repeated minimize and restore preserves startup preferences");
            }
            File.WriteAllLines(Path.GetFullPath(path),results);
        }
    }
}
