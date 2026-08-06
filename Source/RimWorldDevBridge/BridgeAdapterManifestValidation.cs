using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;

namespace RimWorldDevBridge
{
    // Manifest parsing and binding validation produce immutable generation candidates for the indexer.
    internal static class BridgeAdapterManifestValidation
    {
        internal static AdapterManifest Read(string path)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 1024 * 1024)
                throw new InvalidDataException("Manifest size is invalid.");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return (AdapterManifest)new DataContractJsonSerializer(typeof(AdapterManifest)).ReadObject(stream);
        }

        internal static BridgeAdapterCatalog.AdapterGeneration Validate(AdapterManifest manifest, string manifestPath,
            BridgeAdapterSourceRecord source, IndexContext context)
        {
            if (manifest == null) throw new InvalidDataException("Manifest is empty.");
            if (!BridgeAdapterAssemblyVerification.IsSafeFile(manifestPath, source.DirectoryPath))
                throw new InvalidDataException("manifest is outside its declared source or is a reparse point");
            if (manifest.manifestVersion != 1 && manifest.manifestVersion != 2)
                throw new InvalidDataException("Unsupported manifestVersion.");
            RequireName(manifest.adapterId, "adapterId");
            if (string.IsNullOrWhiteSpace(manifest.displayName)) throw new InvalidDataException("displayName is required.");
            if (string.IsNullOrWhiteSpace(manifest.version)) throw new InvalidDataException("version is required.");
            if (string.IsNullOrWhiteSpace(manifest.generation)) throw new InvalidDataException("generation is required.");
            if (!DateTime.TryParse(manifest.buildUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime buildUtc))
                throw new InvalidDataException("buildUtc is invalid.");
            if (manifest.protocolMin > BridgeProtocol.ProtocolVersion || manifest.protocolMax < BridgeProtocol.ProtocolVersion)
                return NewGeneration(manifest, manifestPath, source, buildUtc, false, "protocol incompatible");
            if (string.IsNullOrWhiteSpace(manifest.providerType)) throw new InvalidDataException("providerType is required.");
            bool loadedAssembly = string.Equals(manifest.assemblySource, "loaded", StringComparison.OrdinalIgnoreCase);
            if (!loadedAssembly && !string.IsNullOrEmpty(manifest.assemblySource) &&
                !string.Equals(manifest.assemblySource, "file", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("assemblySource must be file or loaded.");
            manifest.assemblySource = loadedAssembly ? "loaded" : "file";
            if (!loadedAssembly && string.IsNullOrWhiteSpace(manifest.assemblyFile))
                throw new InvalidDataException("assemblyFile is required.");
            if (loadedAssembly && string.IsNullOrWhiteSpace(manifest.assemblyIdentity))
                throw new InvalidDataException("assemblyIdentity is required for a loaded adapter.");
            if (source.SourceKind == BridgeAdapterSourceKind.OwnerMod &&
                !(manifest.requiredPackageIds ?? new List<string>()).Any(package =>
                    string.Equals(package, source.PackageId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("owner package must be required by the manifest");
            if (loadedAssembly && source.SourceKind != BridgeAdapterSourceKind.OwnerMod)
                throw new InvalidDataException("loaded adapters must be discovered from their owner mod");
            if (loadedAssembly && !string.Equals(manifest.modulePackageId, source.PackageId,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("loaded adapter module package does not match its owner");
            if (loadedAssembly && !BridgeAdapterAssemblyVerification.IsSafeRelativePath(manifest.moduleRelativePath))
                throw new InvalidDataException("Loaded adapter module path is invalid.");
            if (loadedAssembly && manifest.manifestVersion >= 2 && !Guid.TryParse(manifest.moduleMvid, out _))
                throw new InvalidDataException("Loaded adapter moduleMvid is invalid.");
            if (loadedAssembly && !(manifest.requiredPackageIds ?? new List<string>()).Any(package =>
                string.Equals(package, manifest.modulePackageId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Loaded adapter module package must be required.");
            string directory = Path.GetDirectoryName(manifestPath);
            string assemblyPath;
            if (!loadedAssembly)
            {
                if (!BridgeAdapterAssemblyVerification.IsSafeAssemblyFileName(manifest.assemblyFile))
                    throw new InvalidDataException("assemblyFile is unsafe.");
                assemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.assemblyFile));
                if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(source.DirectoryPath),
                    StringComparison.OrdinalIgnoreCase) ||
                    !BridgeAdapterAssemblyVerification.IsSafeFile(assemblyPath, source.DirectoryPath) ||
                    !File.Exists(assemblyPath))
                    throw new InvalidDataException("assemblyFile is missing or partial.");
                long actualBytes = new FileInfo(assemblyPath).Length;
                if (manifest.assemblyBytes <= 0 || manifest.assemblyBytes != actualBytes)
                    throw new InvalidDataException("assemblyBytes does not match the published file.");
                if (!string.Equals(BridgeAdapterAssemblyVerification.HashFile(assemblyPath, actualBytes),
                    manifest.contentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("adapter hash does not match its published file.");
                AssemblyName assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                if (!string.Equals(assemblyName.FullName, manifest.assemblyIdentity, StringComparison.Ordinal))
                    throw new InvalidDataException("adapter assembly identity does not match its published file.");
            }
            else
            {
                string relative = manifest.moduleRelativePath.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                assemblyPath = Path.GetFullPath(Path.Combine(source.OwnerRootPath, relative));
                if (!BridgeAdapterAssemblyVerification.IsWithin(assemblyPath, source.OwnerRootPath) ||
                    !BridgeAdapterAssemblyVerification.IsSafeFile(assemblyPath, source.OwnerRootPath) ||
                    !File.Exists(assemblyPath))
                    throw new InvalidDataException("loaded adapter module is missing or escaped its owner.");
                BridgeLoadedModuleRecord loaded = source.LoadedModules.FirstOrDefault(item =>
                    string.Equals(item.RelativePath.Replace('/', Path.DirectorySeparatorChar), relative,
                        StringComparison.OrdinalIgnoreCase));
                if (loaded == null) throw new InvalidDataException("loaded adapter module is not loaded by its owner.");
                if (!string.Equals(Path.GetFullPath(loaded.FullPath), assemblyPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("loaded adapter origin does not match its owner module.");
                if (!string.Equals(loaded.AssemblyIdentity, manifest.assemblyIdentity, StringComparison.Ordinal))
                    throw new InvalidDataException("loaded adapter assembly identity mismatch.");
                if (manifest.manifestVersion >= 2 && loaded.ModuleMvid != Guid.Parse(manifest.moduleMvid))
                    throw new InvalidDataException("loaded adapter MVID mismatch.");
                long actualBytes = new FileInfo(assemblyPath).Length;
                if (manifest.assemblyBytes <= 0 || actualBytes != manifest.assemblyBytes || actualBytes != loaded.Length)
                    throw new InvalidDataException("loaded adapter byte length mismatch.");
                if (!string.Equals(BridgeAdapterAssemblyVerification.HashFile(assemblyPath, actualBytes),
                    manifest.contentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("loaded adapter hash mismatch.");
            }
            if (string.IsNullOrWhiteSpace(manifest.contentHash) || manifest.contentHash.Length != 64)
                throw new InvalidDataException("contentHash must be SHA-256 hex.");
            if (manifest.commands == null || manifest.commands.Count == 0)
                throw new InvalidDataException("commands are required.");
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AdapterCommandManifest command in manifest.commands)
            {
                RequireName(command.name, "command name");
                command.name = BridgeText.NormalizeCommand(command.name);
                command.providerCommand = BridgeText.NormalizeCommand(command.providerCommand ?? command.name);
                RequireName(command.providerCommand, "provider command name");
                if (!names.Add(command.name)) throw new InvalidDataException("Duplicate command " + command.name + ".");
                if (string.Equals(command.mode, "R", StringComparison.OrdinalIgnoreCase))
                    command.mode = BridgeCommandMode.PureRead.ToString();
                else if (string.Equals(command.mode, "W", StringComparison.OrdinalIgnoreCase))
                    command.mode = BridgeCommandMode.PersistentMutation.ToString();
                else if (!Enum.TryParse(command.mode, true, out BridgeCommandMode parsedMode))
                    throw new InvalidDataException("Unknown command mode for " + command.name + ".");
                else command.mode = parsedMode.ToString();
                if (!Enum.TryParse(command.cost, true, out BridgeCostClass parsedCost))
                    throw new InvalidDataException("Unknown command cost for " + command.name + ".");
                command.cost = parsedCost.ToString();
            }
            string missing = (manifest.requiredPackageIds ?? new List<string>())
                .FirstOrDefault(package => !context.LoadedPackages.Contains(package));
            return NewGeneration(manifest, manifestPath, source, buildUtc, missing == null,
                missing == null ? null : "missing package " + missing);
        }

        private static void RequireName(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(character =>
                !char.IsLetterOrDigit(character) && character != '_' && character != '-' && character != '.'))
                throw new InvalidDataException(label + " is invalid.");
        }

        private static BridgeAdapterCatalog.AdapterGeneration NewGeneration(AdapterManifest manifest,
            string manifestPath, BridgeAdapterSourceRecord source, DateTime buildUtc, bool compatible, string reason)
        {
            return new BridgeAdapterCatalog.AdapterGeneration
            {
                Manifest = manifest,
                ManifestPath = manifestPath,
                Source = source,
                AssemblyPath = string.Equals(manifest.assemblySource, "loaded", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFullPath(Path.Combine(source.OwnerRootPath,
                        manifest.moduleRelativePath.Replace('/', Path.DirectorySeparatorChar)))
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath), manifest.assemblyFile)),
                BuildUtc = buildUtc,
                Compatible = compatible,
                Reason = reason,
                State = compatible ? "available" : "incompatible"
            };
        }
    }
}
