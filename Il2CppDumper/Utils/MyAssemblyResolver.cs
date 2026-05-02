using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;
using System.Collections.Generic;

namespace Il2CppDumper
{
    public class MyAssemblyResolver : AssemblyResolverBase
    {
        private readonly Dictionary<string, AssemblyDefinition> _registered = new();

        public MyAssemblyResolver()
        : base(new ModuleReaderParameters())
        {
        }


        public void Register(AssemblyDefinition assembly)
        {
            _registered[assembly.Name!] = assembly;
            if (!HasCached(assembly))
                AddToCache(assembly, assembly);
        }

        protected override AssemblyDefinition? ResolveImpl(AssemblyDescriptor assembly)
        {
            _registered.TryGetValue(assembly.Name!, out var result);
            return result;
        }

        protected override string ProbeRuntimeDirectories(AssemblyDescriptor assembly)
        {
            return null;
        }
    }
}
