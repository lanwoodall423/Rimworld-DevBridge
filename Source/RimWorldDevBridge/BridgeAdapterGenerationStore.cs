using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RimWorldDevBridge
{
    // Resolves immutable generations and publishes the command/fingerprint projections consumed by the catalog.
    internal static class BridgeAdapterGenerationStore
    {
        internal static void ResolveDuplicates(List<BridgeAdapterCatalog.AdapterGeneration> indexed,
            List<string> errors)
        {
            foreach (IGrouping<string, BridgeAdapterCatalog.AdapterGeneration> group in indexed.GroupBy(item =>
                item.Manifest.adapterId + "\n" + item.Manifest.generation, StringComparer.OrdinalIgnoreCase))
            {
                List<BridgeAdapterCatalog.AdapterGeneration> ordered = group.OrderBy(item => item.Source.SourceKind)
                    .ThenBy(item => item.Source.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ManifestPath, StringComparer.OrdinalIgnoreCase).ToList();
                List<string> bindings = ordered.Select(BindingFingerprint).Distinct(StringComparer.Ordinal).ToList();
                if (bindings.Count > 1)
                {
                    foreach (BridgeAdapterCatalog.AdapterGeneration item in ordered)
                    {
                        item.Compatible = false;
                        item.Selected = false;
                        item.State = "quarantined-conflict";
                        item.Reason = "conflicting immutable generation binding";
                    }
                    errors.Add(group.First().Manifest.adapterId + ": generation " +
                        group.First().Manifest.generation + " has conflicting immutable bindings");
                    continue;
                }
                BridgeAdapterCatalog.AdapterGeneration preferred = ordered.FirstOrDefault(item =>
                    item.Source.SourceKind == BridgeAdapterSourceKind.OwnerMod) ?? ordered.FirstOrDefault();
                foreach (BridgeAdapterCatalog.AdapterGeneration duplicate in ordered.Where(item => item != preferred))
                {
                    duplicate.Compatible = false;
                    duplicate.Selected = false;
                    duplicate.State = preferred.Source.SourceKind == BridgeAdapterSourceKind.OwnerMod &&
                        duplicate.Source.SourceKind == BridgeAdapterSourceKind.LegacyDevelopment
                        ? "migration-duplicate" : "duplicate";
                    duplicate.Reason = preferred.Source.SourceKind == BridgeAdapterSourceKind.OwnerMod &&
                        duplicate.Source.SourceKind == BridgeAdapterSourceKind.LegacyDevelopment
                        ? "owner copy preferred over legacy copy" : "identical generation duplicate";
                }
            }
        }

        internal static void SelectActive(List<BridgeAdapterCatalog.AdapterGeneration> indexed, List<string> errors)
        {
            foreach (IGrouping<string, BridgeAdapterCatalog.AdapterGeneration> group in indexed.GroupBy(item =>
                item.Manifest.adapterId, StringComparer.OrdinalIgnoreCase))
            {
                BridgeAdapterCatalog.AdapterGeneration selected = group.Where(item => item.Compatible)
                    .OrderByDescending(item => item.BuildUtc).ThenByDescending(item => item.Manifest.generation,
                        StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                foreach (BridgeAdapterCatalog.AdapterGeneration item in group)
                {
                    item.Selected = item == selected;
                    if (item == selected) item.State = "available";
                    else if (item.Compatible) { item.State = "superseded"; item.Reason = "newer generation selected"; }
                }
                if (selected == null) errors.Add(group.Key + ": no compatible generation");
            }
        }

        internal static void MergeLoadedGenerations(
            IEnumerable<BridgeAdapterCatalog.AdapterGeneration> previousGenerations,
            List<BridgeAdapterCatalog.AdapterGeneration> indexed, List<string> errors)
        {
            List<BridgeAdapterCatalog.AdapterGeneration> previous = previousGenerations.ToList();
            foreach (BridgeAdapterCatalog.AdapterGeneration retained in previous.Where(item => item.RetainedOnly))
                indexed.Add(retained);
            foreach (BridgeAdapterCatalog.AdapterGeneration oldGeneration in previous.Where(item =>
                item.Assembly != null && !item.RetainedOnly))
            {
                BridgeAdapterCatalog.AdapterGeneration current = indexed.FirstOrDefault(item =>
                    item.Manifest.adapterId.Equals(oldGeneration.Manifest.adapterId, StringComparison.OrdinalIgnoreCase) &&
                    item.Manifest.generation.Equals(oldGeneration.Manifest.generation,
                        StringComparison.OrdinalIgnoreCase));
                if (current != null && SameManifestBinding(current.Manifest, oldGeneration.Manifest))
                {
                    current.CopyRuntime(oldGeneration);
                    if (!current.Selected) current.State = "retained-superseded";
                }
                else
                {
                    oldGeneration.Selected = false;
                    oldGeneration.RetainedOnly = true;
                    oldGeneration.State = "retained-superseded";
                    oldGeneration.Reason = current == null ? "manifest generation no longer published" :
                        "published manifest changed without a new generation";
                    indexed.Add(oldGeneration);
                    if (current != null)
                    {
                        current.Selected = false;
                        current.Compatible = false;
                        current.State = "incompatible";
                        current.Reason = "generation identity was reused with a changed manifest";
                        errors.Add(current.Manifest.adapterId + ": generation " + current.Manifest.generation +
                            " changed after load; publish a new generation");
                    }
                }
            }
        }

        internal static void RebuildCommandIndex(List<BridgeAdapterCatalog.AdapterGeneration> all,
            IDictionary<string, BridgeAdapterCatalog.AdapterGeneration> active,
            IDictionary<string, BridgeAdapterCatalog.AdapterGeneration> commandsByName, List<string> errors)
        {
            active.Clear();
            commandsByName.Clear();
            foreach (BridgeAdapterCatalog.AdapterGeneration generation in all.Where(item => item.Selected &&
                item.Compatible && item.State != "failed" && item.State != "quarantined")
                .OrderBy(item => item.Manifest.adapterId, StringComparer.OrdinalIgnoreCase))
            {
                active[generation.Manifest.adapterId] = generation;
                bool collision = false;
                foreach (AdapterCommandManifest command in generation.Manifest.commands)
                {
                    if (BridgeCommands.Describe(command.name) != null || commandsByName.ContainsKey(command.name))
                    {
                        collision = true;
                        errors.Add(generation.Manifest.adapterId + ": command collision " + command.name);
                        break;
                    }
                }
                if (collision)
                {
                    generation.State = "quarantined";
                    generation.Reason = "command collision";
                    active.Remove(generation.Manifest.adapterId);
                    continue;
                }
                foreach (AdapterCommandManifest command in generation.Manifest.commands)
                    commandsByName[command.name] = generation;
            }
        }

        internal static string FingerprintFor(AdapterManifest manifest)
        {
            using (SHA256 algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(ManifestFingerprint(manifest)))
                    .Take(12).Select(value => value.ToString("x2")));
        }

        internal static string ComputeFingerprint(IEnumerable<BridgeAdapterCatalog.AdapterGeneration> generations)
        {
            string source = BridgeProtocol.CoreSchema + "|" + string.Join("|", generations
                .Where(item => item.State == "available" || item.State == "loaded" || item.State == "prepared")
                .OrderBy(item => item.Manifest.adapterId).Select(item => item.Manifest.adapterId + ":" +
                    item.Manifest.version + ":" + item.Manifest.generation + ":" + item.Manifest.contentHash));
            using (SHA256 algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(source)).Take(6)
                    .Select(value => value.ToString("x2")));
        }

        private static string BindingFingerprint(BridgeAdapterCatalog.AdapterGeneration item) =>
            ManifestFingerprint(item.Manifest);

        private static bool SameManifestBinding(AdapterManifest current, AdapterManifest previous)
        {
            return string.Equals(ManifestFingerprint(current), ManifestFingerprint(previous), StringComparison.Ordinal);
        }

        private static string ManifestFingerprint(AdapterManifest manifest)
        {
            StringBuilder value = new StringBuilder();
            value.Append(manifest.manifestVersion).Append('|').Append(manifest.adapterId).Append('|')
                .Append(manifest.displayName).Append('|').Append(manifest.version).Append('|')
                .Append(manifest.generation).Append('|').Append(manifest.buildUtc).Append('|')
                .Append(manifest.protocolMin).Append('|').Append(manifest.protocolMax).Append('|')
                .Append(manifest.assemblySource).Append('|').Append(manifest.assemblyFile).Append('|')
                .Append(manifest.assemblyIdentity).Append('|').Append(manifest.modulePackageId).Append('|')
                .Append(manifest.moduleRelativePath).Append('|').Append(manifest.moduleMvid).Append('|')
                .Append(manifest.assemblyBytes).Append('|').Append(manifest.contentHash).Append('|')
                .Append(manifest.providerType).Append('|').Append(manifest.executionContract).Append('|')
                .Append(manifest.changeSummary);
            foreach (string package in (manifest.requiredPackageIds ?? new List<string>())
                .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)) value.Append("|required:").Append(package);
            foreach (string package in (manifest.optionalPackageIds ?? new List<string>())
                .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)) value.Append("|optional:").Append(package);
            foreach (AdapterCommandManifest command in (manifest.commands ?? new List<AdapterCommandManifest>())
                .OrderBy(command => command.name, StringComparer.OrdinalIgnoreCase))
                value.Append('|').Append(command.name).Append('|').Append(command.description).Append('|')
                    .Append(command.providerCommand).Append('|').Append(command.mode).Append('|')
                    .Append(command.cost).Append('|').Append(command.requiresMap).Append('|')
                    .Append(command.argumentSchema).Append('|').Append(command.resultSchema).Append('|')
                    .Append(command.schemaVersion).Append('|').Append(command.minimumExecutionBudgetMs);
            return value.ToString();
        }
    }
}
