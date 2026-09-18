using System;
using System.IO;
using System.Collections.Generic;

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
            rows.Add("Hardware API calls: 0. "+(rows.Count-errors)+" passed, "+errors+" failed.");
            File.WriteAllLines(report,rows); return errors==0?0:1;
        }
    }
}
