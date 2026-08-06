using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Verse;

namespace RimWorldDevBridge
{
    // Lazy loading is isolated from indexing so a prepared generation is the only load boundary.
    internal static class BridgeAdapterLoader
    {
        internal static void EnsureLoaded(BridgeAdapterCatalog.AdapterGeneration generation)
        {
            if (generation.Assembly != null) return;
            lock (generation.LoadGate)
            {
                if (generation.Assembly != null) return;
                byte[] bytes = generation.PreparedBytes;
                Assembly preparedAssembly = generation.PreparedAssembly;
                if (bytes == null && preparedAssembly == null)
                    throw new InvalidOperationException("Adapter was not prepared off-thread.");
                long loadStart = Stopwatch.GetTimestamp();
                Assembly assembly = preparedAssembly ?? Assembly.Load(bytes);
                if (!string.IsNullOrWhiteSpace(generation.Manifest.assemblyIdentity) &&
                    !assembly.FullName.Equals(generation.Manifest.assemblyIdentity, StringComparison.Ordinal))
                    throw new InvalidDataException("Adapter assembly identity does not match its manifest.");
                Type providerType = assembly.GetType(generation.Manifest.providerType, true, false);
                if (typeof(IBridgeAdapterProvider).IsAssignableFrom(providerType))
                {
                    generation.TypedProvider = (IBridgeAdapterProvider)Activator.CreateInstance(providerType);
                    ValidateTypedProvider(generation);
                }
                else
                {
                    generation.LegacyExecute = providerType.GetMethod("ExecuteBridgeCommand",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string), typeof(string), typeof(Map) }, null);
                    if (generation.LegacyExecute == null)
                        throw new MissingMethodException(providerType.FullName, "ExecuteBridgeCommand");
                }
                generation.Assembly = assembly;
                generation.PreparedBytes = null;
                generation.PreparedAssembly = null;
                generation.LoadMs = BridgeTiming.Milliseconds(loadStart);
                generation.State = "loaded";
            }
        }

        private static void ValidateTypedProvider(BridgeAdapterCatalog.AdapterGeneration generation)
        {
            BridgeAdapterMetadata metadata = generation.TypedProvider.Metadata ??
                throw new InvalidDataException("Typed adapter metadata is missing.");
            if (!string.Equals(metadata.Id, generation.Manifest.adapterId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metadata.Version, generation.Manifest.version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Typed adapter metadata does not match its manifest.");
            if (string.Equals(generation.Manifest.executionContract, "cooperative-v1",
                    StringComparison.OrdinalIgnoreCase))
            {
                IBridgeCooperativeAdapterProvider cooperative = generation.TypedProvider as
                    IBridgeCooperativeAdapterProvider;
                if (cooperative == null || !string.Equals(cooperative.ExecutionContract, "cooperative-v1",
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Manifest requests cooperative-v1 but provider does not implement it.");
            }
            Dictionary<string, BridgeCommandDescriptor> declared = (generation.TypedProvider.Commands ??
                Enumerable.Empty<BridgeCommandDescriptor>()).Where(item => item != null)
                .ToDictionary(item => BridgeText.NormalizeCommand(item.Name), StringComparer.OrdinalIgnoreCase);
            foreach (AdapterCommandManifest command in generation.Manifest.commands)
            {
                if (!declared.TryGetValue(command.name, out BridgeCommandDescriptor descriptor))
                    throw new InvalidDataException("Typed provider did not declare " + command.name + ".");
                Enum.TryParse(command.mode, true, out BridgeCommandMode mode);
                if (descriptor.Mode != mode)
                    throw new InvalidDataException("Typed provider mode differs for " + command.name + ".");
            }
        }
    }
}
