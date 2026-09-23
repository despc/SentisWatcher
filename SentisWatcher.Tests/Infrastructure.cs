using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace SentisWatcher.Tests
{
    /// <summary>The game's assemblies are found in the server install when the tests need them.</summary>
    internal static class Infrastructure
    {
        public static string SeRoot => Environment.GetEnvironmentVariable("SE_ROOT") ?? @"C:\SE";

        [ModuleInitializer]
        internal static void Init()
        {
            var dirs = new[] { AppDomain.CurrentDomain.BaseDirectory, SeRoot, Path.Combine(SeRoot, "DedicatedServer64") };
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                foreach (var dir in dirs)
                {
                    var path = Path.Combine(dir, name + ".dll");
                    if (File.Exists(path))
                        try { return Assembly.LoadFrom(path); } catch { }
                }
                return null;
            };
        }
    }
}
