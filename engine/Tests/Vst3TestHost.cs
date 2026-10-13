using System;
using NUnit.Framework;

namespace Graze.Tests
{
    /// <summary>
    /// Tests with real VST3 plugins: Windows x64 only and opt-in. NOTEWRIGHT_TEST_VST3_ROOTS lists ';'-separated folders of installed
    /// plugins; NOTEWRIGHT_PLUGIN_HOST is the worker (engine/run-tests.sh sets it to engine/Native/PluginHost/graze-plugin-host.exe).
    /// </summary>
    static class Vst3TestHost
    {
        public static string Executable => Environment.GetEnvironmentVariable("NOTEWRIGHT_PLUGIN_HOST") ?? "graze-plugin-host.exe";

        public static string[] Roots(string what)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8) Assert.Ignore("VST3 needs Windows x64.");
            string roots = Environment.GetEnvironmentVariable("NOTEWRIGHT_TEST_VST3_ROOTS");
            if (string.IsNullOrEmpty(roots)) Assert.Ignore("Set NOTEWRIGHT_TEST_VST3_ROOTS to " + what + ".");
            return roots.Split(';');
        }
    }
}
