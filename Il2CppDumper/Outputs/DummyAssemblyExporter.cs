using System;
using System.IO;

namespace Il2CppDumper
{
    public static class DummyAssemblyExporter
    {
        public static void Export(Il2CppExecutor il2CppExecutor, string outputDir, bool addToken)
        {
            var dummyDllPath = Path.Combine(outputDir, "DummyDll");
            if (Directory.Exists(dummyDllPath))
            {
                Directory.Delete(dummyDllPath, true);
            }
            Directory.CreateDirectory("DummyDll");

            var dummy = new DummyAssemblyGenerator(il2CppExecutor, addToken);
            foreach (var assembly in dummy.Assemblies)
            {
                var manifest = assembly.ManifestModule;
                if (manifest == null) continue;

                string fileName = manifest.Name?.Value ?? (assembly.Name + ".dll");
                string outputPath = Path.Combine(dummyDllPath, fileName);
                try
                {
                    assembly.Write(outputPath);
                }
                catch (Exception ex)
                {
                    ExtensionMethods.logger.LogError($"Failed to write {fileName}: {ex.Message}");
                }
            }
        }
    }
}
