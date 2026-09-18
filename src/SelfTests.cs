using System;
using System.IO;
using System.Collections.Generic;
using System.Xml;

namespace XbarControl {
    public static class SelfTests {
        sealed class Fake : IGpuTransport {
            public byte[] Buffer; public byte[] Written; public int Writes; public int Code; public bool Mismatch; public bool ChangeVoltage;
            public byte[] Read(){return (byte[])Buffer.Clone();}
            public int Write(byte[] payload){Writes++; Written=(byte[])payload.Clone(); if(Code!=0)return Code;
                if(!Mismatch) ClockPayload.Put(Buffer,ClockPayload.Frequency,ClockPayload.Get(payload,ClockPayload.Frequency));
                if(ChangeVoltage) ClockPayload.Put(Buffer,ClockPayload.Voltage,1000);
                return 0;
            }
        }
        static byte[] Fixture() {
            var b=new byte[ClockPayload.Size]; ClockPayload.Put(b,0,ClockPayload.Version); ClockPayload.Put(b,8,255);
            for(int i=0;i<8;i++){int at=ClockPayload.Base+i*ClockPayload.Stride; ClockPayload.Put(b,at,15); ClockPayload.Put(b,at+0x114,(i==1?0:123000)); ClockPayload.Put(b,at+0x11c,5000);}
            return b;
        }
        static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        static void Throws(Action action){bool threw=false;try{action();}catch{threw=true;}Require(threw,"Expected refusal");}
        static StartupSettings Profile() { return new StartupSettings { HasProfile=true, GpuName="NVIDIA GeForce RTX 5090", Bus=1, Driver="616.92", ImplementationHash=new string('a',64), OffsetMhz=30 }; }
        public static int Run(string report) {
            var rows=new List<string>(); int errors=0;
            Action<string,Action> test=(name,action)=>{try{action();rows.Add("PASS "+name);}catch(Exception ex){errors++;rows.Add("FAIL "+name+": "+ex.Message);}};
            test("Payload changes only XBAR frequency and domain mask",()=>{
                var b=Fixture(); var before=(byte[])b.Clone(); var p=ClockPayload.ForOffset(b,150);
                Require(ClockPayload.Get(p,8)==2,"Mask must be XBAR only");
                for(int i=0;i<b.Length;i++){Require(b[i]==before[i],"Source mutation"); if((i>=8&&i<12)||(i>=ClockPayload.Frequency&&i<ClockPayload.Frequency+4))continue; Require(p[i]==b[i],"Unrelated byte changed at "+i);}
                Require(ClockPayload.Get(p,ClockPayload.Frequency)==150000,"MHz to kHz conversion");
            });
            test("Negative offset remains signed and preserves voltage",()=>{var p=ClockPayload.ForOffset(Fixture(),-150);Require(ClockPayload.Get(p,ClockPayload.Frequency)==-150000,"Sign");Require(ClockPayload.Get(p,ClockPayload.Voltage)==5000,"Voltage");});
            test("Bounds reject without writes",()=>{var f=new Fake{Buffer=Fixture()};Throws(()=>ClockPayload.Apply(f,1001,0));Throws(()=>ClockPayload.Apply(f,-1001,0));Require(f.Writes==0,"Write happened");});
            test("Unexpected version refuses",()=>{var b=Fixture();ClockPayload.Put(b,0,0);Throws(()=>ClockPayload.ForOffset(b,15));});
            test("Unexpected XBAR record refuses",()=>{var b=Fixture();ClockPayload.Put(b,ClockPayload.Base+ClockPayload.Stride,9);Throws(()=>ClockPayload.ForOffset(b,15));});
            test("Missing XBAR mask refuses",()=>{var b=Fixture();ClockPayload.Put(b,8,1);Throws(()=>ClockPayload.ForOffset(b,15));});
            test("Concurrent offset change refuses before write",()=>{var f=new Fake{Buffer=Fixture()};ClockPayload.Put(f.Buffer,ClockPayload.Frequency,30000);Throws(()=>ClockPayload.Apply(f,15,0));Require(f.Writes==0,"Write happened");});
            test("No-op does not call SET",()=>{var f=new Fake{Buffer=Fixture()};ClockPayload.Apply(f,0,0);Require(f.Writes==0,"Write happened");});
            test("Successful apply uses readback",()=>{var f=new Fake{Buffer=Fixture()};var b=ClockPayload.Apply(f,15,0);Require(f.Writes==1,"Write count");Require(ClockPayload.Get(b,ClockPayload.Frequency)==15000,"Readback");});
            test("Failed SET is not presented as success",()=>{var f=new Fake{Buffer=Fixture(),Code=-5};Throws(()=>ClockPayload.Apply(f,15,0));Require(f.Writes==1,"No automatic retries");});
            test("Clamped or ignored SET is detected",()=>{var f=new Fake{Buffer=Fixture(),Mismatch=true};Throws(()=>ClockPayload.Apply(f,15,0));Require(f.Writes==1,"No automatic rollback");});
            test("Unexpected voltage change is detected",()=>{var f=new Fake{Buffer=Fixture(),ChangeVoltage=true};Throws(()=>ClockPayload.Apply(f,15,0));});
            test("Unrelated domain readback change is detected",()=>{var before=Fixture();var after=(byte[])before.Clone();ClockPayload.Put(after,ClockPayload.Frequency,15000);ClockPayload.Put(after,ClockPayload.Base+0x114,999000);Throws(()=>ClockPayload.VerifyReadback(before,after,15));});
            test("Both startup modes default off",()=>{var s=new StartupSettings();Require(!s.Requested(false,false,false)&&!s.Requested(true,false,false),"Unexpected automatic apply");});
            test("Tray startup defaults off",()=>{var s=Profile();s.WindowsLogon=true;Require(!s.TrayRequested(true,false,false),"Unexpected hidden startup");});
            test("Tray startup is limited to enabled Windows logon",()=>{var s=Profile();s.StartInTray=true;s.AppStart=true;Require(!s.TrayRequested(false,false,false)&&!s.TrayRequested(true,false,false),"Normal launch hidden");s.WindowsLogon=true;Require(s.TrayRequested(true,false,false)&&!s.TrayRequested(false,false,false),"Windows launch policy");});
            test("Skip and inspection prevent hidden startup",()=>{var s=Profile();s.WindowsLogon=true;s.StartInTray=true;Require(!s.TrayRequested(true,true,false)&&!s.TrayRequested(true,false,true),"Recovery or inspection hidden");});
            test("App and Windows startup preferences are independent",()=>{var s=Profile();s.AppStart=true;Require(s.Requested(false,false,false)&&!s.Requested(true,false,false),"App-only");s.AppStart=false;s.WindowsLogon=true;Require(!s.Requested(false,false,false)&&s.Requested(true,false,false),"Windows-only");});
            test("Skip and inspection override enabled startup modes",()=>{var s=Profile();s.AppStart=true;s.WindowsLogon=true;Require(!s.Requested(false,true,false)&&!s.Requested(true,true,false)&&!s.Requested(false,false,true)&&!s.Requested(true,false,true),"Override ignored");});
            test("Enabling automatic apply requires a saved profile",()=>{var s=new StartupSettings{AppStart=true};Throws(()=>s.Validate());s.AppStart=false;s.WindowsLogon=true;Throws(()=>s.Validate());});
            test("GPU identity and driver hash must all match",()=>{var s=Profile();var gpu=new GpuDevice{Name=s.GpuName,Bus=s.Bus};Require(s.Matches(gpu,s.Driver,s.ImplementationHash),"Matching profile");gpu.Bus=2;Require(!s.Matches(gpu,s.Driver,s.ImplementationHash),"Wrong bus accepted");gpu.Bus=1;gpu.Name="Another GPU";Require(!s.Matches(gpu,s.Driver,s.ImplementationHash),"Wrong GPU accepted");gpu.Name=s.GpuName;Require(!s.Matches(gpu,"610.88",s.ImplementationHash)&&!s.Matches(gpu,s.Driver,new string('b',64)),"Driver change accepted");});
            test("Invalid startup offsets and hashes are rejected",()=>{var s=Profile();s.OffsetMhz=1001;Throws(()=>s.Validate());s.OffsetMhz=-1001;Throws(()=>s.Validate());s.OffsetMhz=0;s.ImplementationHash=new string('z',64);Throws(()=>s.Validate());});
            test("Unknown startup schema is rejected",()=>{var s=Profile();s.Version=2;Throws(()=>s.Validate());});
            string fixtureRoot=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)),"startup-tests-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            var store=new StartupStore(fixtureRoot);
            try {
                test("Missing startup settings stay disabled",()=>{Require(!store.Load().HasProfile,"Unexpected profile");});
                test("Existing settings without a tray field preserve visible startup",()=>{store.Save(Profile());string json=File.ReadAllText(store.SettingsPath);Require(json.Contains("\"StartInTray\":false"),"Missing fixture field");json=json.Replace(",\"StartInTray\":false","").Replace("\"StartInTray\":false,","");File.WriteAllText(store.SettingsPath,json);var loaded=store.Load();Require(!loaded.StartInTray&&loaded.HasProfile&&loaded.OffsetMhz==30,"Legacy profile changed");});
                test("Saved profile and all startup preferences survive restart",()=>{var s=Profile();s.AppStart=true;s.WindowsLogon=true;s.StartInTray=true;store.Save(s);var loaded=store.Load();Require(loaded.AppStart&&loaded.WindowsLogon&&loaded.StartInTray&&loaded.OffsetMhz==30&&loaded.ImplementationHash==s.ImplementationHash,"Roundtrip");});
                test("Invalid replacement preserves the last valid settings",()=>{var s=Profile();s.OffsetMhz=2000;Throws(()=>store.Save(s));Require(store.Load().OffsetMhz==30,"Valid profile lost");});
                test("Settings replacement can disable both modes",()=>{var s=store.Load();s.AppStart=false;s.WindowsLogon=false;store.Save(s);var loaded=store.Load();Require(!loaded.AppStart&&!loaded.WindowsLogon&&loaded.OffsetMhz==30,"Disable not persisted");});
                test("Pending marker blocks a second automatic attempt",()=>{store.BeginAttempt();Require(store.Interrupted,"Missing pending marker");Throws(()=>store.BeginAttempt());});
                test("Completed or explicitly saved profile clears pending marker",()=>{store.ClearAttempt();Require(!store.Interrupted,"Marker not cleared");});
                test("Corrupt settings fail closed",()=>{File.WriteAllText(store.SettingsPath,"{broken");Throws(()=>store.Load());});
                test("Oversized settings are rejected",()=>{File.WriteAllText(store.SettingsPath,new string(' ',17000));Throws(()=>store.Load());});
            } finally { foreach(string file in Directory.GetFiles(fixtureRoot)) File.Delete(file); Directory.Delete(fixtureRoot); }
            test("Logon task targets current user without password or repeated execution",()=>{
                string exe="C:\\Program Files\\Xbar & Control\\app.exe",sid="S-1-5-21-111-222-333-1001";
                var doc=new XmlDocument();doc.LoadXml(StartupTask.BuildXml(exe,sid));var ns=new XmlNamespaceManager(doc.NameTable);ns.AddNamespace("t","http://schemas.microsoft.com/windows/2004/02/mit/task");
                Func<string,string> value=p=>doc.SelectSingleNode(p,ns).InnerText;
                Require(value("//t:Exec/t:Command")==exe&&value("//t:Exec/t:Arguments")=="--startup","Command escaped incorrectly");
                Require(value("//t:Principal/t:UserId")==sid&&value("//t:LogonTrigger/t:UserId")==sid,"Wrong user");
                Require(value("//t:LogonType")=="InteractiveToken"&&value("//t:RunLevel")=="HighestAvailable","Logon mode");
                Require(value("//t:Delay")=="PT30S"&&doc.SelectNodes("//t:Repetition|//t:RestartOnFailure|//t:RegistrationTrigger",ns).Count==0,"Unexpected retry or immediate trigger");
            });
            rows.Add("Hardware API calls: 0. "+(rows.Count-errors)+" passed, "+errors+" failed.");
            File.WriteAllLines(report,rows); return errors==0?0:1;
        }
    }
}
