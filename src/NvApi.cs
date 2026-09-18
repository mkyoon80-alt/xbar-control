using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace XbarControl {
    public sealed class GpuDevice {
        public IntPtr Handle; public string Name; public uint Bus;
        public override string ToString() { return Name + "  ·  PCI " + Bus; }
    }
    public sealed class Reading {
        public int OffsetKhz; public int VoltageUv; public double? ClockMhz;
        public DateTime Time; public bool Compatible; public string Compatibility;
        public byte[] Raw;
    }
    public interface IGpuTransport {
        byte[] Read(); int Write(byte[] payload);
    }
    public static class ClockPayload {
        public const int Size = 0x13000, Version = 0x261a4;
        public const int Base = 0x124, Stride = 0x304, XbarIndex = 1;
        public const int Frequency = Base + Stride * XbarIndex + 0x114;
        public const int Voltage = Base + Stride * XbarIndex + 0x11c;
        public static int Get(byte[] b, int at) { return BitConverter.ToInt32(b, at); }
        public static void Put(byte[] b, int at, int value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, b, at, 4); }
        public static void Validate(byte[] b) {
            if (b == null || b.Length != Size || Get(b, 0) != Version)
                throw new InvalidOperationException("드라이버의 XBAR 데이터 형식이 일치하지 않습니다.");
            if ((Get(b, 8) & 2) == 0) throw new InvalidOperationException("XBAR 도메인이 응답하지 않았습니다.");
            for (int i = 0; i < 8; i++)
                if ((Get(b, 8) & (1 << i)) != 0 && Get(b, Base + i * Stride) != 15)
                    throw new InvalidOperationException("클럭 레코드 구조가 일치하지 않습니다.");
            if (Math.Abs((long)Get(b, Frequency)) > 1000000 || Math.Abs((long)Get(b, Voltage)) > 100000)
                throw new InvalidOperationException("XBAR 응답이 지원 범위를 벗어났습니다.");
        }
        public static byte[] ForOffset(byte[] current, int mhz) {
            Validate(current);
            if (mhz < -1000 || mhz > 1000) throw new ArgumentOutOfRangeException("mhz", "−1000~+1000 MHz 안에서 입력해 주세요.");
            byte[] result = (byte[])current.Clone();
            // The setter iterates the 32-bit mask at +8. Select XBAR only.
            // Preserve the XBAR voltage/rail fields and every other byte.
            Put(result, 8, 2);
            Put(result, Frequency, checked(mhz * 1000));
            return result;
        }
        public static void VerifyReadback(byte[] before, byte[] after, int mhz) {
            Validate(after);
            if (Get(after, Frequency) != mhz * 1000)
                throw new InvalidOperationException("적용 후 읽은 값이 요청과 다릅니다. 현재 값을 다시 확인해 주세요.");
            if (Get(after, Voltage) != Get(before, Voltage))
                throw new InvalidOperationException("XBAR 전압 값의 변경이 감지되었습니다. 다른 튜닝 프로그램을 확인해 주세요.");
            for (int i = 0; i < 8; i++) {
                if (i == XbarIndex) continue;
                int at = Base + i * Stride;
                if (Get(before, at + 0x114) != Get(after, at + 0x114) || Get(before, at + 0x11c) != Get(after, at + 0x11c))
                    throw new InvalidOperationException("다른 클럭 도메인의 변경이 감지되었습니다. 다른 튜닝 프로그램을 확인해 주세요.");
            }
        }
        public static byte[] Apply(IGpuTransport transport, int mhz, int expectedKhz) {
            byte[] before = transport.Read(); Validate(before);
            if (Get(before, Frequency) != expectedKhz)
                throw new InvalidOperationException("다른 프로그램이 XBAR 값을 변경했습니다. 새로고침 후 다시 적용해 주세요.");
            byte[] payload = ForOffset(before, mhz);
            if (Get(before, Frequency) == mhz * 1000) return before;
            int rc = transport.Write(payload);
            if (rc != 0) throw new InvalidOperationException("XBAR 적용 실패 (NVAPI " + rc + "). 관리자 권한과 드라이버 상태를 확인해 주세요.");
            byte[] after = transport.Read(); VerifyReadback(before, after, mhz);
            return after;
        }
    }
    public sealed class NvApi : IGpuTransport, IDisposable {
        [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32", SetLastError=true)] static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
        [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern uint GetModuleFileNameW(IntPtr module, StringBuilder file, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Query(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Init();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumGpu([Out] IntPtr[] handles, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GpuBuffer(IntPtr gpu, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NameFn(IntPtr gpu, StringBuilder name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BusFn(IntPtr gpu, out uint bus);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DriverFn(out uint version, StringBuilder branch);
        readonly IntPtr module; readonly Query query; bool disposed;
        public readonly List<GpuDevice> Devices = new List<GpuDevice>();
        public GpuDevice Selected;
        public string Driver { get; private set; }
        public string ImplementationHash { get; private set; }
        public string ImplementationPath { get; private set; }
        public bool DriverValidated { get; private set; }
        public static bool IsAdmin {
            get { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        }
        T Function<T>(uint id) where T : class {
            IntPtr p = query(id);
            if (p == IntPtr.Zero) throw new InvalidOperationException("필요한 NVIDIA 기능을 찾을 수 없습니다: " + id.ToString("X8"));
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }
        public NvApi() {
            module = LoadLibraryW(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));
            if (module == IntPtr.Zero) throw new InvalidOperationException("NVIDIA 드라이버를 찾을 수 없습니다. 드라이버 설치 상태를 확인해 주세요.");
            IntPtr q = GetProcAddress(module, "nvapi_QueryInterface");
            if (q == IntPtr.Zero) throw new InvalidOperationException("NVIDIA API를 초기화할 수 없습니다.");
            query = (Query)Marshal.GetDelegateForFunctionPointer(q, typeof(Query));
            Check(Function<Init>(0x0150E828)(), "초기화");
            uint version; var branch = new StringBuilder(64);
            Check(Function<DriverFn>(0x2926AAAD)(out version, branch), "드라이버 버전");
            Driver = (version / 100) + "." + (version % 100).ToString("00");
            IntPtr[] handles = new IntPtr[64]; uint count;
            Check(Function<EnumGpu>(0xE5AC921F)(handles, out count), "GPU 검색");
            for (int i = 0; i < Math.Min(count, 64); i++) {
                var name = new StringBuilder(64); uint bus;
                Check(Function<NameFn>(0xCEEE8E9F)(handles[i], name), "GPU 이름");
                Check(Function<BusFn>(0x1BE0B8E5)(handles[i], out bus), "GPU 주소");
                Devices.Add(new GpuDevice { Handle=handles[i], Name=name.ToString(), Bus=bus });
            }
            if (Devices.Count == 0) throw new InvalidOperationException("NVIDIA GPU가 검색되지 않았습니다.");
            Selected = Devices.FirstOrDefault(g => g.Name.Contains("RTX 50")) ?? Devices[0];
            ImplementationHash = ""; ImplementationPath = "";
            IntPtr impl;
            if (query(0xF58938F5) != IntPtr.Zero && GetModuleHandleExW(6, query(0xF58938F5), out impl)) {
                var path = new StringBuilder(32768);
                if (GetModuleFileNameW(impl, path, path.Capacity) > 0) {
                    ImplementationPath = path.ToString();
                    using (var sha = SHA256.Create()) using (var f = File.OpenRead(ImplementationPath))
                        ImplementationHash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                }
            }
            string[] prior = {"572.16","576.02","580.88","581.42","591.86","596.49","610.62","610.88"};
            DriverValidated = prior.Contains(Driver) || (Driver == "616.92" && ImplementationHash == "3f26731d9ff492c2ef3be879b0da68e415ed563dae5caee901eb01f66c32379d");
        }
        static void Check(int rc, string action) {
            if (rc != 0) throw new InvalidOperationException(action + " 실패 (NVAPI " + rc + ")");
        }
        int Call(uint id, byte[] buffer) {
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { return Function<GpuBuffer>(id)(Selected.Handle, pinned.AddrOfPinnedObject()); }
            finally { pinned.Free(); }
        }
        public byte[] Read() {
            if (!Selected.Name.Contains("RTX 50")) throw new InvalidOperationException("이 버전은 RTX 50 시리즈의 XBAR 구조를 지원합니다.");
            byte[] b = new byte[ClockPayload.Size];
            ClockPayload.Put(b, 0, ClockPayload.Version); ClockPayload.Put(b, 8, 0xff);
            Check(Call(0xF58938F5, b), "XBAR 읽기"); ClockPayload.Validate(b); return b;
        }
        public Reading Sample() {
            byte[] b = Read(); double? clock = null;
            try {
                byte[] measure = new byte[0x1000c]; ClockPayload.Put(measure, 0, 0x1000c); ClockPayload.Put(measure, 4, 2);
                if (Call(0x527fc458, measure) == 0) {
                    uint khz = BitConverter.ToUInt32(measure, 8); if (khz <= 10000000) clock = khz / 1000.0;
                }
            } catch { /* Physical measurement is optional; never substitute fabricated data. */ }
            bool compatible = DriverValidated && query(0xD14B69CF) != IntPtr.Zero;
            return new Reading { Raw=b, OffsetKhz=ClockPayload.Get(b, ClockPayload.Frequency), VoltageUv=ClockPayload.Get(b, ClockPayload.Voltage), ClockMhz=clock, Time=DateTime.Now, Compatible=compatible,
                Compatibility=compatible ? "드라이버 구조 · 읽기 검사 통과" : "미검증 드라이버 · 읽기 전용" };
        }
        public int Write(byte[] payload) {
            if (!IsAdmin) throw new InvalidOperationException("적용하려면 관리자 권한으로 앱을 다시 열어 주세요.");
            if (!DriverValidated || !Selected.Name.Contains("RTX 50")) throw new InvalidOperationException("검증되지 않은 드라이버 또는 GPU에서는 적용할 수 없습니다.");
            ClockPayload.Validate(payload);
            if (ClockPayload.Get(payload, 8) != 2) throw new InvalidOperationException("XBAR 이외 도메인 쓰기는 허용되지 않습니다.");
            return Call(0xD14B69CF, payload);
        }
        public void Apply(int mhz, int expectedKhz) { ClockPayload.Apply(this, mhz, expectedKhz); }
        public void Dispose() {
            if (disposed) return; disposed = true;
            try { Function<Init>(0xD22BDD7E)(); } catch { }
        }
    }
}
