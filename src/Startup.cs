using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;

namespace XbarControl {
    [DataContract] public sealed class StartupSettings {
        [DataMember] public int Version = 1;
        [DataMember] public bool HasProfile;
        [DataMember] public bool AppStart;
        [DataMember] public bool WindowsLogon;
        [DataMember] public bool StartInTray;
        [DataMember] public string GpuName;
        [DataMember] public uint Bus;
        [DataMember] public string Driver;
        [DataMember] public string ImplementationHash;
        [DataMember] public int OffsetMhz;
        public StartupSettings Copy() { return (StartupSettings)MemberwiseClone(); }
        public void Validate() {
            if(Version!=1) throw new InvalidDataException("자동 적용 설정 버전이 다릅니다.");
            if((AppStart || WindowsLogon) && !HasProfile) throw new InvalidDataException("저장된 자동 적용 값이 없습니다.");
            if(HasProfile && (OffsetMhz < -1000 || OffsetMhz > 1000 || string.IsNullOrWhiteSpace(GpuName) ||
                string.IsNullOrWhiteSpace(Driver) || ImplementationHash==null || ImplementationHash.Length!=64 ||
                !ImplementationHash.All(c=>Uri.IsHexDigit(c)))) throw new InvalidDataException("자동 적용 설정을 읽을 수 없습니다. 현재 값을 다시 저장해 주세요.");
        }
        public bool Matches(GpuDevice gpu,string driver,string hash) {
            return HasProfile && gpu!=null && GpuName==gpu.Name && Bus==gpu.Bus && Driver==driver &&
                string.Equals(ImplementationHash,hash,StringComparison.OrdinalIgnoreCase);
        }
        public bool Requested(bool logon,bool skip,bool inspect) {
            Validate(); return !skip && !inspect && HasProfile && (logon ? WindowsLogon : AppStart);
        }
        public bool TrayRequested(bool logon,bool skip,bool inspect) {
            return StartInTray && Requested(logon,skip,inspect) && logon;
        }
    }
    public sealed class StartupStore {
        public static readonly string DefaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XbarControl");
        readonly string directory;
        public StartupStore(string path) { directory=Path.GetFullPath(path); }
        public string SettingsPath { get { return Path.Combine(directory,"startup.json"); } }
        public string MarkerPath { get { return Path.Combine(directory,"auto-apply.pending"); } }
        public bool Interrupted { get { return File.Exists(MarkerPath); } }
        public StartupSettings Load() {
            if(!File.Exists(SettingsPath)) return new StartupSettings();
            using(var f=File.OpenRead(SettingsPath)) {
                if(f.Length>16384) throw new InvalidDataException("자동 적용 설정 파일이 너무 큽니다.");
                var s=(StartupSettings)new DataContractJsonSerializer(typeof(StartupSettings)).ReadObject(f);
                if(s==null) throw new InvalidDataException("자동 적용 설정이 비어 있습니다.");
                s.Validate(); return s;
            }
        }
        public void Save(StartupSettings settings) {
            settings.Validate(); Directory.CreateDirectory(directory);
            string temp=Path.Combine(directory,Guid.NewGuid().ToString("N")+".tmp");
            try {
                using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    new DataContractJsonSerializer(typeof(StartupSettings)).WriteObject(f,settings); f.Flush(true);
                }
                if(File.Exists(SettingsPath)) File.Replace(temp,SettingsPath,null); else File.Move(temp,SettingsPath);
            } finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        public void BeginAttempt() {
            Directory.CreateDirectory(directory);
            using(var f=new FileStream(MarkerPath,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                byte[] data=Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("o")); f.Write(data,0,data.Length); f.Flush(true);
            }
        }
        public void ClearAttempt() { if(File.Exists(MarkerPath)) File.Delete(MarkerPath); }
    }
    public static class StartupTask {
        public static string UserSid { get { using(var identity=WindowsIdentity.GetCurrent()) return identity.User.Value; } }
        public static string TaskName { get { return "XBAR Control - "+UserSid; } }
        public static string BuildXml(string executable,string sid) {
            // No password, SYSTEM identity, repetition, registration trigger, or restart-on-failure.
            var text=new StringBuilder();
            using(var w=XmlWriter.Create(text,new XmlWriterSettings{OmitXmlDeclaration=true,Indent=true})) {
                w.WriteStartElement("Task","http://schemas.microsoft.com/windows/2004/02/mit/task"); w.WriteAttributeString("version","1.2");
                w.WriteStartElement("RegistrationInfo"); w.WriteElementString("Description","XBAR Control: apply the explicitly saved XBAR offset at this user's Windows logon."); w.WriteEndElement();
                w.WriteStartElement("Triggers"); w.WriteStartElement("LogonTrigger"); w.WriteElementString("Enabled","true"); w.WriteElementString("UserId",sid); w.WriteElementString("Delay","PT30S"); w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("Principals"); w.WriteStartElement("Principal"); w.WriteAttributeString("id","User"); w.WriteElementString("UserId",sid); w.WriteElementString("LogonType","InteractiveToken"); w.WriteElementString("RunLevel","HighestAvailable"); w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("Settings"); w.WriteElementString("MultipleInstancesPolicy","IgnoreNew"); w.WriteElementString("DisallowStartIfOnBatteries","false"); w.WriteElementString("StopIfGoingOnBatteries","false"); w.WriteElementString("AllowHardTerminate","false"); w.WriteElementString("StartWhenAvailable","false"); w.WriteElementString("ExecutionTimeLimit","PT0S"); w.WriteEndElement();
                w.WriteStartElement("Actions"); w.WriteAttributeString("Context","User"); w.WriteStartElement("Exec"); w.WriteElementString("Command",executable); w.WriteElementString("Arguments","--startup"); w.WriteElementString("WorkingDirectory",Path.GetDirectoryName(executable)); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
            }
            return text.ToString();
        }
        static dynamic Connect() { dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true)); service.Connect(); return service; }
        static bool Missing(System.Runtime.InteropServices.COMException ex) { return ex.ErrorCode==unchecked((int)0x80070002); }
        public static bool IsRegistered() {
            dynamic service=Connect(); dynamic root=service.GetFolder("\\");
            try { dynamic task=root.GetTask(TaskName); return task.Enabled; }
            catch(FileNotFoundException) { return false; }
            catch(DirectoryNotFoundException) { return false; }
            catch(System.Runtime.InteropServices.COMException ex) { if(Missing(ex)) return false; throw; }
        }
        static string Hash(string path) { using(var sha=SHA256.Create()) using(var f=File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant(); }
        static void RejectReparse(string path) {
            for(var d=new DirectoryInfo(path);d!=null;d=d.Parent)
                if(d.Exists && (d.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("자동 실행 설치 경로에 연결 폴더를 사용할 수 없습니다.");
        }
        static void SecureDirectory(string path) {
            RejectReparse(path);
            var acl=new DirectorySecurity();
            acl.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");
            Directory.CreateDirectory(path,acl); Directory.SetAccessControl(path,acl); RejectReparse(path);
        }
        public static string InstallExecutable() {
            if(!NvApi.IsAdmin) throw new UnauthorizedAccessException("Windows 시작 설정은 관리자 권한으로 변경해 주세요.");
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"XbarControl");
            SecureDirectory(root); string folder=Path.Combine(root,UserSid); SecureDirectory(folder);
            string source=Assembly.GetExecutingAssembly().Location, hash=Hash(source);
            string destination=Path.Combine(folder,"XbarControl-"+hash+".exe");
            if(File.Exists(destination)) {
                if((File.GetAttributes(destination)&FileAttributes.ReparsePoint)!=0 || Hash(destination)!=hash) throw new IOException("자동 실행 파일 검증에 실패했습니다.");
            } else { File.Copy(source,destination,false); }
            if(Hash(destination)!=hash) throw new IOException("자동 실행 파일 복사를 확인하지 못했습니다.");
            var acl=new FileSecurity(); acl.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)"); File.SetAccessControl(destination,acl);
            return destination;
        }
        public static void Register() {
            string executable=InstallExecutable(); dynamic service=Connect(); dynamic root=service.GetFolder("\\");
            // CREATE_OR_UPDATE plus IGNORE_REGISTRATION_TRIGGERS; registration must never apply now.
            root.RegisterTask(TaskName,BuildXml(executable,UserSid),6|32,UserSid,null,3,null);
        }
        public static void Remove() {
            if(!NvApi.IsAdmin) throw new UnauthorizedAccessException("Windows 시작 설정은 관리자 권한으로 변경해 주세요.");
            dynamic service=Connect(); dynamic root=service.GetFolder("\\");
            try { root.DeleteTask(TaskName,0); }
            catch(FileNotFoundException) { }
            catch(DirectoryNotFoundException) { }
            catch(System.Runtime.InteropServices.COMException ex) { if(!Missing(ex)) throw; }
        }
        public static void ValidateOnly() {
            dynamic service=Connect(); dynamic root=service.GetFolder("\\");
            root.RegisterTask(TaskName,BuildXml(Assembly.GetExecutingAssembly().Location,UserSid),1,UserSid,null,3,null);
        }
    }
}
