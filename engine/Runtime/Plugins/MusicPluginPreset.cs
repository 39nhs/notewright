using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Graze.Presentation.Audio
{
    /// <summary>Bounded preset reader. Files bind to an application-selected VST3 class, never choose/approve a plugin.</summary>
    public sealed class MusicPluginPreset
    {
        public const int MaxFileBytes = 16 * 1024 * 1024;
        readonly MusicPluginState state;
        public MusicPluginIdentity Plugin { get; }
        public string Format { get; }
        public string Name { get; }
        public uint? LegacyFxId { get; }
        public MusicPluginState State => new MusicPluginState(state.Component,state.Controller);
        MusicPluginPreset(MusicPluginIdentity plugin,string format,string name,MusicPluginState state,uint? legacy=null)
        { Plugin=plugin;Format=format;Name=name;this.state=state;LegacyFxId=legacy; }
        public static MusicPluginPreset Load(string path,MusicPluginIdentity expectedPlugin,uint? expectedLegacyFxId=null)
        {
            if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("Select a preset file.");
            string extension=Path.GetExtension(path);
            if(!string.Equals(extension,".vstpreset",StringComparison.OrdinalIgnoreCase)&&!string.Equals(extension,".fxp",StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Preset file must be .vstpreset or a compatible chunk .fxp.");
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(file.Length<4||file.Length>MaxFileBytes)throw new InvalidDataException("Preset file must contain 4 bytes..16 MiB.");
                var bytes=new byte[(int)file.Length];int at=0;
                while(at<bytes.Length){int read=file.Read(bytes,at,bytes.Length-at);if(read==0)throw new EndOfStreamException("Preset changed while reading.");at+=read;}
                var preset=Parse(bytes,expectedPlugin,expectedLegacyFxId);
                if((extension.Equals(".fxp",StringComparison.OrdinalIgnoreCase))!=(preset.Format=="fxp"))throw new InvalidDataException("Preset extension and file signature disagree.");
                return preset;
            }
        }
        public static MusicPluginPreset Parse(byte[] data,MusicPluginIdentity expectedPlugin,uint? expectedLegacyFxId=null)
        {
            if(expectedPlugin==null)throw new ArgumentNullException(nameof(expectedPlugin));
            if(data==null||data.Length<4||data.Length>MaxFileBytes)throw new InvalidDataException("Invalid preset size.");
            if(Tag(data,0)=="VST3")return VstPreset(data,expectedPlugin);
            if(Tag(data,0)=="CcnK")return Fxp(data,expectedPlugin,expectedLegacyFxId);
            throw new InvalidDataException("Unknown preset signature (expected VST3 or CcnK).");
        }
        static MusicPluginPreset VstPreset(byte[] data,MusicPluginIdentity expected)
        {
            if(data.Length<56||U32(data,4,false)!=1)throw new InvalidDataException("Unsupported/truncated VST3 preset header.");
            string id=Encoding.ASCII.GetString(data,8,32);
            try{if(new MusicPluginIdentity(id).ClassId!=expected.ClassId)throw new InvalidDataException("Preset belongs to a different VST3 class.");}
            catch(ArgumentException e){throw new InvalidDataException("Invalid preset class ID.",e);}
            ulong list64=U64(data,40);
            if(list64<48||list64>(ulong)data.Length-8)throw new InvalidDataException("Invalid VST3 chunk-list offset.");
            int list=(int)list64;
            if(Tag(data,list)!="List")throw new InvalidDataException("Missing VST3 List marker.");
            uint count=U32(data,list+4,false);
            if(count==0||count>128||list+8+(long)count*20!=data.Length)throw new InvalidDataException("Invalid VST3 chunk-list length.");
            var seen=new HashSet<string>();var ranges=new List<Tuple<int,int>>();byte[] component=null,controller=null;
            for(int i=0;i<count;i++)
            {
                int entry=list+8+i*20;string tag=Tag(data,entry);ulong start64=U64(data,entry+4),size64=U64(data,entry+12);
                if(!seen.Add(tag)||start64<48||start64>(ulong)list||size64>(ulong)list-start64)
                    throw new InvalidDataException("Duplicate or out-of-range VST3 chunk.");
                int start=(int)start64,size=(int)size64;
                if(size>0){foreach(var range in ranges)if(start<range.Item2&&start+size>range.Item1)throw new InvalidDataException("Overlapping VST3 chunks.");ranges.Add(Tuple.Create(start,start+size));}
                if(tag=="Comp")component=Slice(data,start,size);
                else if(tag=="Cont")controller=Slice(data,start,size);
            }
            if(component==null||component.Length==0)throw new NotSupportedException("VST3 preset requires a nonempty Comp state; program/unit-only presets are unsupported.");
            return new MusicPluginPreset(expected,"vstpreset","",new MusicPluginState(component,controller));
        }
        static MusicPluginPreset Fxp(byte[] data,MusicPluginIdentity expected,uint? legacy)
        {
            if(data.Length<60)throw new InvalidDataException("Truncated FXP header.");
            if(Tag(data,8)!="FPCh")throw new NotSupportedException("Only single-program FPCh chunk FXP is supported; parameter-only FxCk and banks need plugin-specific conversion.");
            if(U32(data,12,true)!=1||U32(data,24,true)!=1)throw new InvalidDataException("Unsupported FXP version/program count.");
            uint id=U32(data,16,true),declared=U32(data,4,true),size=U32(data,56,true);
            if(!legacy.HasValue)throw new NotSupportedException("FXP needs an application-owned legacy fxID mapping for the selected VST3 class.");
            if(legacy.Value!=id)throw new InvalidDataException("FXP belongs to a different legacy plugin ID.");
            // Surge's factory FXPs historically leave byteSize=0; chunkSize still bounds the complete payload.
            if(declared!=data.Length-8&&!(declared==0&&id==0x636a7333))throw new InvalidDataException("FXP declared size disagrees with the file.");
            if(size==0||size>12*1024*1024||60+(long)size!=data.Length)throw new InvalidDataException("Invalid/truncated FXP chunk.");
            string name=Encoding.GetEncoding(28591).GetString(data,28,28).TrimEnd('\0',' ');
            return new MusicPluginPreset(expected,"fxp",name,new MusicPluginState(Slice(data,60,(int)size)),id);
        }
        static string Tag(byte[] b,int at)=>Encoding.ASCII.GetString(b,at,4);
        static uint U32(byte[] b,int at,bool big)
            =>big?((uint)b[at]<<24)|((uint)b[at+1]<<16)|((uint)b[at+2]<<8)|b[at+3]
                  :(uint)b[at]|((uint)b[at+1]<<8)|((uint)b[at+2]<<16)|((uint)b[at+3]<<24);
        static ulong U64(byte[] b,int at)=>U32(b,at,false)|((ulong)U32(b,at+4,false)<<32);
        static byte[] Slice(byte[] data,int at,int size){var result=new byte[size];Array.Copy(data,at,result,0,size);return result;}
    }
}
