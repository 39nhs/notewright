using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicPluginPresetTests
    {
        static readonly MusicPluginIdentity Plugin=new MusicPluginIdentity("1234567890ABCDEF1234567890ABCDEF");
        static byte[] Vst(byte[] comp=null,byte[] ctrl=null,string classId=null)
        {
            comp=comp??new byte[]{1,2,3};ctrl=ctrl??new byte[]{4,5};
            using(var m=new MemoryStream())using(var w=new BinaryWriter(m)){
                w.Write(Encoding.ASCII.GetBytes("VST3"));w.Write(1);w.Write(Encoding.ASCII.GetBytes(classId??Plugin.ClassId));w.Write(48L+comp.Length+ctrl.Length);
                w.Write(comp);w.Write(ctrl);w.Write(Encoding.ASCII.GetBytes("List"));w.Write(2);
                w.Write(Encoding.ASCII.GetBytes("Comp"));w.Write(48L);w.Write((long)comp.Length);
                w.Write(Encoding.ASCII.GetBytes("Cont"));w.Write(48L+comp.Length);w.Write((long)ctrl.Length);return m.ToArray();
            }
        }
        static void Big(byte[] b,int at,uint v){for(int i=0;i<4;i++)b[at+i]=(byte)(v>>(24-i*8));}
        static void Little64(byte[] b,int at,ulong v){for(int i=0;i<8;i++)b[at+i]=(byte)(v>>(i*8));}
        static byte[] Fxp(uint id=0x12345678,bool surgeZeroSize=false)
        {
            var data=new byte[64];Encoding.ASCII.GetBytes("CcnK").CopyTo(data,0);Big(data,4,surgeZeroSize?0u:56u);
            Encoding.ASCII.GetBytes("FPCh").CopyTo(data,8);Big(data,12,1);Big(data,16,id);Big(data,20,7);Big(data,24,1);
            Encoding.ASCII.GetBytes("Preset Name").CopyTo(data,28);Big(data,56,4);new byte[]{8,7,6,5}.CopyTo(data,60);return data;
        }
        [Test]
        public void VstPresetExtractsBoundComponentAndControllerAndCopiesSource()
        {
            var data=Vst();var p=MusicPluginPreset.Parse(data,Plugin);data[48]=99;
            Assert.AreEqual("vstpreset",p.Format);Assert.AreEqual(Plugin.ClassId,p.Plugin.ClassId);
            CollectionAssert.AreEqual(new byte[]{1,2,3},p.State.Component);CollectionAssert.AreEqual(new byte[]{4,5},p.State.Controller);
            var state=p.State;state.Component[0]=22;Assert.AreEqual(1,p.State.Component[0]);
            var restored=MusicPluginState.FromBase64(p.State.ToBase64());CollectionAssert.AreEqual(p.State.Component,restored.Component);
        }
        [Test]
        public void FxpRequiresApplicationLegacyMappingAndReturnsOnlyTheBoundChunk()
        {
            Assert.Throws<NotSupportedException>(()=>MusicPluginPreset.Parse(Fxp(),Plugin));
            Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(Fxp(),Plugin,0x11111111));
            var p=MusicPluginPreset.Parse(Fxp(),Plugin,0x12345678);
            Assert.AreEqual("fxp",p.Format);Assert.AreEqual("Preset Name",p.Name);Assert.AreEqual(0x12345678u,p.LegacyFxId.Value);
            CollectionAssert.AreEqual(new byte[]{8,7,6,5},p.State.Component);Assert.AreEqual(0,p.State.Controller.Length);
            Assert.DoesNotThrow(()=>MusicPluginPreset.Parse(Fxp(0x636a7333,true),Plugin,0x636a7333));
            Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(Fxp(0x12345678,true),Plugin,0x12345678));
        }
        [Test]
        public void WrongClassHeaderVersionAndUnboundedOffsetsFailBeforeRestore()
        {
            Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(Vst(),new MusicPluginIdentity(new string('A',32))));
            var bad=Vst();bad[4]=2;Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(bad,Plugin));
            bad=Vst();bad[8]=(byte)'?';Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(bad,Plugin));
            bad=Vst();Little64(bad,40,ulong.MaxValue);Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(bad,Plugin));
            Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(new byte[3],Plugin));
            Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(new byte[MusicPluginPreset.MaxFileBytes+1],Plugin));
        }
        [Test]
        public void DuplicateOverlappingOutsideAndTruncatedChunksAreRejected()
        {
            const int list=53;var duplicate=Vst();Encoding.ASCII.GetBytes("Comp").CopyTo(duplicate,list+28);
            Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(duplicate,Plugin));
            var overlap=Vst();Little64(overlap,list+32,49);Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(overlap,Plugin));
            var outside=Vst();Little64(outside,list+12,ulong.MaxValue);Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(outside,Plugin));
            var size=Vst();Little64(size,list+20,ulong.MaxValue);Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(size,Plugin));
            Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(Vst().Take(92).ToArray(),Plugin));
            Assert.Throws<NotSupportedException>(()=>MusicPluginPreset.Parse(Vst(Array.Empty<byte>()),Plugin));
        }
        [Test]
        public void FxpParameterBanksBadProgramCountsAndTruncationAreExplicit()
        {
            foreach(string magic in new[]{"FxCk","FBCh","FxBk"}){var b=Fxp();Encoding.ASCII.GetBytes(magic).CopyTo(b,8);Assert.Throws<NotSupportedException>(()=>MusicPluginPreset.Parse(b,Plugin,0x12345678));}
            var multi=Fxp();Big(multi,24,2);Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(multi,Plugin,0x12345678));
            var longChunk=Fxp();Big(longChunk,56,uint.MaxValue);Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(longChunk,Plugin,0x12345678));
            Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Parse(Fxp().Take(40).ToArray(),Plugin,0x12345678));
        }
        [Test]
        public void FileLoadingChecksExtensionSignatureAndByteBound()
        {
            string root=Path.Combine(Path.GetTempPath(),"graze-preset-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{
                string path=Path.Combine(root,"Test.vstpreset");File.WriteAllBytes(path,Vst());
                Assert.AreEqual("vstpreset",MusicPluginPreset.Load(path,Plugin).Format);
                File.WriteAllBytes(path,Fxp());Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Load(path,Plugin,0x12345678));
                string invalid=Path.Combine(root,"Unknown.txt");File.WriteAllBytes(invalid,Vst());Assert.Throws<NotSupportedException>(()=>MusicPluginPreset.Load(invalid,Plugin));
                using(var big=File.Create(path))big.SetLength(MusicPluginPreset.MaxFileBytes+1L);
                Assert.Throws<InvalidDataException>(()=>MusicPluginPreset.Load(path,Plugin));
            }finally{Directory.Delete(root,true);}
        }
        [Test]
        public void InstalledEffectPresetCapturesStateAndRejectsWrongClass()
        {
            var roots=Vst3TestHost.Roots("installed effect folders");
            string executable=Vst3TestHost.Executable;
            var catalog=MusicPluginCatalog.Scan(executable,roots,includeEffects:true);
            var plugin=catalog.Plugins.First(p=>!p.IsInstrument);MusicPluginState captured;
            using(var host=new MusicPluginHost(executable)){
                host.LoadEffect(plugin);host.Configure(new MusicPluginState(null),new System.Collections.Generic.Dictionary<string,double>{{"output_mix",.73}});
                captured=host.CaptureState();var preset=MusicPluginPreset.Parse(Vst(captured.Component,captured.Controller,plugin.Identity.ClassId),plugin.Identity);
                host.Configure(new MusicPluginState(null),new System.Collections.Generic.Dictionary<string,double>{{"output_mix",.2}},warm:false);
                var restored=host.LoadPreset(preset);CollectionAssert.AreEqual(captured.Component,restored.Component);
                var wrong=MusicPluginPreset.Parse(Vst(),Plugin);Assert.Throws<ArgumentException>(()=>host.LoadPreset(wrong));
                CollectionAssert.AreEqual(restored.Component,host.CaptureState().Component);
            }
            var denied=new MusicPluginInsertContext(executable,catalog,Array.Empty<string>());Assert.Throws<UnauthorizedAccessException>(()=>denied.Resolve(plugin.Identity));
        }
    }
}
