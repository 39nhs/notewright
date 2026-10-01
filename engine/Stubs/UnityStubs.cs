// CI stand-ins for the UnityEngine types the runtime uses. Add a member here when runtime code needs it (engine/run-tests.sh).
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Field)] public sealed class RangeAttribute : Attribute { public RangeAttribute(float a, float b) { } }
    [AttributeUsage(AttributeTargets.Field)] public sealed class TooltipAttribute : Attribute { public TooltipAttribute(string t) { } }
    [AttributeUsage(AttributeTargets.Field)] public sealed class TextAreaAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class MinAttribute : Attribute { public MinAttribute(float a) { } }
    [AttributeUsage(AttributeTargets.Class)] public sealed class CreateAssetMenuAttribute : Attribute { public string menuName; }
    public class Object
    {
        public string name;
        public static void DestroyImmediate(Object o) { }
        public static void Destroy(Object o) { }
        public static T Instantiate<T>(T o) where T : Object => (T)o.MemberwiseClone();
        public static implicit operator bool(Object o) => !ReferenceEquals(o, null);
    }
    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() => new T();
    }
    public enum AudioClipLoadType { DecompressOnLoad, CompressedInMemory, Streaming }
    public enum AudioDataLoadState { Unloaded, Loading, Loaded, Failed }
    public sealed class AudioClip : Object
    {
        float[] data;
        public int samples, channels, frequency;
        public AudioClipLoadType loadType = AudioClipLoadType.DecompressOnLoad;
        public AudioDataLoadState loadState = AudioDataLoadState.Loaded;
        public static AudioClip Create(string name, int lengthSamples, int channels, int frequency, bool stream)
            => new AudioClip { name = name, samples = lengthSamples, channels = channels, frequency = frequency, data = new float[lengthSamples * channels] };
        public bool SetData(float[] d, int offset) { Array.Copy(d, 0, data, offset, Math.Min(d.Length, data.Length - offset)); return true; }
        public bool GetData(float[] d, int offset) { Array.Copy(data, offset, d, 0, Math.Min(d.Length, data.Length - offset)); return true; }
        public bool LoadAudioData() => true;
    }
    public static class JsonUtility
    {
        public static string ToJson(object o, bool pretty = false) => throw new NotSupportedException();
    }
}
