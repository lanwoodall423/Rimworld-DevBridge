using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace RimWorldDevBridge
{
    internal static class BridgeOrchestration
    {
        private const int MaximumDepth = 8;
        private const long MaximumMacroBytes = 1024 * 1024;
        private static readonly object Gate = new object();
        private static Dictionary<string, MacroDefinition> macros =
            new Dictionary<string, MacroDefinition>(StringComparer.OrdinalIgnoreCase);
        private static List<string> errors = new List<string>();
        private static bool initialized;
        private static DateTime loadedWriteUtc;

        internal static IEnumerable<BridgeCommandDescriptor> Commands
        {
            get
            {
                EnsureLoaded();
                lock (Gate) return macros.Values.Select(item => item.Descriptor.Clone()).OrderBy(item => item.Name).ToList();
            }
        }

        internal static BridgeCommandDescriptor Describe(string command, string argument)
        {
            string name = BridgeText.NormalizeCommand(command);
            if (name == "BATCH") return DescribeBatch(argument);
            if (name == "MACRO_DRY_RUN") return new BridgeCommandDescriptor
            {
                Name = name, Description = "Validate and expand one declarative macro without execution.",
                Provider = "macro", ProviderVersion = "2", Mode = BridgeCommandMode.PureRead,
                Cost = BridgeCostClass.Trivial, RequiresMap = false, ArgumentSchema = "name&parameter=value",
                ResultSchema = "core.macroDryRun", SchemaVersion = 2
            };
            if (name == "MACRO_STATUS" || name == "RELOAD_MACROS") return new BridgeCommandDescriptor
            {
                Name = name, Description = name == "MACRO_STATUS" ? "Report lazy declarative macro state." :
                    "Reload and validate declarative macros.", Provider = "macro", ProviderVersion = "2",
                Mode = BridgeCommandMode.PureRead, Cost = BridgeCostClass.Normal, RequiresMap = false,
                ArgumentSchema = "none", ResultSchema = "core.macroStatus", SchemaVersion = 2
            };
            EnsureLoaded();
            lock (Gate) return macros.TryGetValue(name, out MacroDefinition value) ? value.Descriptor.Clone() : null;
        }

        internal static BridgeResult Prepare(BridgeRequest request)
        {
            if (request.PreparedPayload != null) return null;
            string name = request.Command;
            if (name == "MACRO_STATUS" || name == "RELOAD_MACROS" || name == "MACRO_DRY_RUN") return null;
            if (name == "BATCH")
            {
                BridgeResult failure = ParseBatch(request, out PreparedPlan plan);
                if (failure != null) return failure;
                request.PreparedPayload = plan;
                return null;
            }
            EnsureLoaded();
            MacroDefinition macro;
            lock (Gate) macros.TryGetValue(name, out macro);
            if (macro == null) return null;
            BridgeResult prepare = PrepareMacro(request, macro, ParseParameters(request.Argument), out PreparedPlan prepared);
            if (prepare != null) return prepare;
            request.PreparedPayload = prepared;
            return null;
        }

        internal static BridgeResult Execute(BridgeExecutionContext context)
        {
            if (context.Request.Command == "MACRO_STATUS") return Status();
            if (context.Request.Command == "RELOAD_MACROS") { Reload(); return Status(); }
            if (context.Request.Command == "MACRO_DRY_RUN") return DryRun(context.Request.Argument);
            PreparedPlan plan = context.Request.PreparedPayload as PreparedPlan;
            if (plan == null) return null;
            BridgeResult result = BridgeResult.Ok(plan.Schema).Add("steps", plan.Calls.Count)
                .Add("mode", context.Request.Mode).Add("cost", context.Request.Cost);
            int failures = 0;
            List<BridgeResult> childResults = new List<BridgeResult>();
            for (int i = 0; i < plan.Calls.Count; i++)
            {
                PreparedCall call = plan.Calls[i];
                BridgeResult child = BridgeDispatch.ExecuteChild(context, call);
                childResults.Add(child);
                result.AddLine("step=index:" + i + " command:" + call.Request.Command + " status:" + child.Status +
                    " schema:" + BridgeText.Clean(child.Schema) + " provider:" + BridgeText.Clean(child.Provider));
                foreach (BridgeField field in child.Data.Take(12))
                    result.AddLine("stepData=index:" + i + " name:" + BridgeText.Clean(field.Name) +
                        " value:" + BridgeText.Clean(field.Value));
                foreach (string line in child.Lines.Take(12))
                    result.AddLine("stepLine=index:" + i + " value:" + BridgeText.Clean(line));
                bool statusExpected = plan.Assertions.Any(assertion => assertion.Step == i &&
                    !string.IsNullOrEmpty(assertion.Status) && string.Equals(assertion.Status,
                        child.Status.ToString(), StringComparison.OrdinalIgnoreCase));
                if (!child.IsSuccess && !statusExpected)
                {
                    failures++;
                    if (plan.StopOnError) break;
                }
            }
            foreach (MacroAssertion assertion in plan.Assertions)
            {
                string failure = assertion.Validate(childResults);
                result.AddLine("assertion=step:" + assertion.Step + " status:" +
                    (failure == null ? "OK" : "ERROR") + " detail:" + BridgeText.Clean(failure));
                if (failure != null) failures++;
            }
            result.Add("failures", failures);
            if (failures > 0) result.Status = BridgeStatus.PARTIAL;
            if (context.Request.Mode != BridgeCommandMode.PureRead)
                result.MutationSummary = "orchestration executed " + plan.Calls.Count + " bounded steps";
            return result;
        }

        internal static void Reload()
        {
            lock (Gate) initialized = false;
            EnsureLoaded();
        }

        private static BridgeCommandDescriptor DescribeBatch(string argument)
        {
            BridgeRequest request = Synthetic("BATCH", argument);
            BridgeResult failure = ParseBatch(request, out PreparedPlan plan);
            if (failure != null) return new BridgeCommandDescriptor
            {
                Name = "BATCH", Description = "Bounded multi-command batch; invalid until arguments are supplied.",
                Provider = "macro", ProviderVersion = "2", Mode = BridgeCommandMode.PotentiallyDestructive,
                Cost = BridgeCostClass.Expensive, RequiresMap = false, ArgumentSchema = "COMMAND[:argument];...",
                ResultSchema = "core.batch", SchemaVersion = 2
            };
            return Descriptor("BATCH", "Bounded multi-command batch with transitively derived mode.", plan);
        }

        private static BridgeResult ParseBatch(BridgeRequest request, out PreparedPlan plan)
        {
            plan = new PreparedPlan { Schema = "core.batch", StopOnError = false };
            string[] entries = (request.Argument ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length == 0 || entries.Length > BridgeProtocol.MaxBatchSections)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_batch_size")
                    .Add("maximum", BridgeProtocol.MaxBatchSections);
            foreach (string entry in entries)
            {
                string[] pair = entry.Split(new[] { ':' }, 2);
                string command = BridgeText.NormalizeCommand(pair[0]);
                if (command == "BATCH") return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "nested_batch_forbidden");
                BridgeResult failure = BridgeDispatch.PrepareChild(request, command,
                    pair.Length > 1 ? pair[1] : string.Empty, out PreparedCall call);
                if (failure != null) return failure;
                plan.Calls.Add(call);
            }
            return null;
        }

        private static BridgeResult PrepareMacro(BridgeRequest request, MacroDefinition macro,
            Dictionary<string, string> parameters, out PreparedPlan plan)
        {
            plan = new PreparedPlan { Schema = "core.macro", StopOnError = macro.StopOnError };
            foreach (MacroCall source in macro.ExpandedCalls)
            {
                string argument = Substitute(source.Argument, parameters);
                BridgeResult failure = BridgeDispatch.PrepareChild(request, source.Command, argument, out PreparedCall call);
                if (failure != null) return failure;
                plan.Calls.Add(call);
            }
            plan.Assertions.AddRange(macro.ExpandedAssertions);
            return null;
        }

        private static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (initialized && FileWriteUtc(BridgePaths.MacroPath) == loadedWriteUtc) return;
                initialized = true;
                loadedWriteUtc = FileWriteUtc(BridgePaths.MacroPath);
                macros = new Dictionary<string, MacroDefinition>(StringComparer.OrdinalIgnoreCase);
                errors = new List<string>();
                MigrateLegacyFile();
                if (!File.Exists(BridgePaths.MacroPath)) return;
                try
                {
                    FileInfo info = new FileInfo(BridgePaths.MacroPath);
                    if (info.Length <= 0 || info.Length > MaximumMacroBytes)
                        throw new InvalidDataException("Macro file size is invalid.");
                    XmlReaderSettings settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = MaximumMacroBytes,
                        IgnoreComments = true,
                        IgnoreProcessingInstructions = true
                    };
                    XDocument document;
                    using (XmlReader reader = XmlReader.Create(BridgePaths.MacroPath, settings))
                        document = XDocument.Load(reader, LoadOptions.None);
                    XElement root = document.Root;
                    if (root == null || root.Name != "BridgeCommands")
                        throw new InvalidDataException("Expected BridgeCommands root.");
                    if (!string.Equals((string)root.Attribute("version") ?? "2", "2",
                        StringComparison.Ordinal)) throw new InvalidDataException("Unsupported macro schema version.");
                    foreach (XElement element in root.Elements("Command"))
                    {
                        string name = BridgeText.NormalizeCommand((string)element.Attribute("name"));
                        if (!ValidName(name)) { errors.Add("invalid macro name: " + name); continue; }
                        if (BridgeCommands.Describe(name) != null || BridgeAdapterCatalog.Describe(name) != null ||
                            macros.ContainsKey(name)) { errors.Add("macro command collision: " + name); continue; }
                        MacroDefinition macro = new MacroDefinition
                        {
                            Name = name,
                            Description = (string)element.Attribute("description") ?? string.Empty,
                            StopOnError = !string.Equals((string)element.Attribute("onError"), "continue",
                                StringComparison.OrdinalIgnoreCase)
                        };
                        foreach (XElement call in element.Elements("Call"))
                        {
                            if (macro.Calls.Count >= BridgeProtocol.MaxMacroCalls)
                                throw new InvalidDataException(name + " exceeds maximum calls.");
                            macro.Calls.Add(new MacroCall
                            {
                                Command = BridgeText.NormalizeCommand((string)call.Attribute("command")),
                                Argument = (string)call.Attribute("argument") ?? string.Empty
                            });
                        }
                        foreach (XElement assertion in element.Elements("Assert"))
                        {
                            if (macro.Assertions.Count >= BridgeProtocol.MaxMacroCalls)
                                throw new InvalidDataException(name + " exceeds maximum assertions.");
                            if (!int.TryParse((string)assertion.Attribute("step"), out int step) || step < 0)
                                throw new InvalidDataException(name + ": assertion step is invalid.");
                            macro.Assertions.Add(new MacroAssertion
                            {
                                Step = step,
                                Status = (string)assertion.Attribute("status"),
                                Schema = (string)assertion.Attribute("schema"),
                                Field = (string)assertion.Attribute("field"),
                                Expected = (string)assertion.Attribute("equals")
                            });
                        }
                        if (macro.Calls.Count == 0) errors.Add(name + ": no calls");
                        else macros[name] = macro;
                    }
                    List<string> invalid = new List<string>();
                    foreach (MacroDefinition macro in macros.Values.ToList())
                    {
                        try { ValidateAndExpand(macro, new Stack<string>()); }
                        catch (Exception exception)
                        {
                            invalid.Add(macro.Name);
                            errors.Add(macro.Name + ": " + exception.GetBaseException().Message);
                        }
                    }
                    foreach (string name in invalid) macros.Remove(name);
                }
                catch (Exception exception)
                {
                    macros.Clear();
                    errors.Add(exception.GetBaseException().Message);
                }
            }
        }

        private static void ValidateAndExpand(MacroDefinition macro, Stack<string> stack)
        {
            if (macro.Descriptor != null) return;
            if (stack.Contains(macro.Name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("macro cycle: " + string.Join(" -> ", stack.Reverse()) + " -> " + macro.Name);
            if (stack.Count >= MaximumDepth) throw new InvalidDataException("macro depth exceeds " + MaximumDepth + ".");
            stack.Push(macro.Name);
            List<MacroCall> expanded = new List<MacroCall>();
            List<MacroAssertion> expandedAssertions = new List<MacroAssertion>();
            List<BridgeCommandDescriptor> descriptors = new List<BridgeCommandDescriptor>();
            foreach (MacroCall call in macro.Calls)
            {
                if (macros.TryGetValue(call.Command, out MacroDefinition nested))
                {
                    ValidateAndExpand(nested, stack);
                    int offset = expanded.Count;
                    expanded.AddRange(nested.ExpandedCalls.Select(item => item.Clone()));
                    expandedAssertions.AddRange(nested.ExpandedAssertions.Select(item => item.Clone(offset)));
                    descriptors.AddRange(nested.ExpandedCalls.Select(item =>
                        BridgeDispatch.Describe(Synthetic(item.Command, item.Argument))));
                }
                else
                {
                    BridgeRequest request = Synthetic(call.Command, call.Argument);
                    BridgeCommandDescriptor descriptor = BridgeDispatch.Describe(request);
                    if (descriptor == null) throw new InvalidDataException(macro.Name + ": unknown command " + call.Command);
                    expanded.Add(call.Clone());
                    descriptors.Add(descriptor);
                }
            }
            stack.Pop();
            if (expanded.Count > BridgeProtocol.MaxMacroCalls)
                throw new InvalidDataException(macro.Name + " expands beyond maximum calls.");
            expandedAssertions.AddRange(macro.Assertions.Select(item => item.Clone(0)));
            if (expandedAssertions.Count > BridgeProtocol.MaxMacroCalls)
                throw new InvalidDataException(macro.Name + " expands beyond maximum assertions.");
            if (expandedAssertions.Any(assertion => assertion.Step >= expanded.Count))
                throw new InvalidDataException(macro.Name + ": assertion step is out of range.");
            macro.ExpandedCalls = expanded;
            macro.ExpandedAssertions = expandedAssertions;
            PreparedPlan plan = new PreparedPlan { Calls = descriptors.Select((descriptor, index) => new PreparedCall
                { Descriptor = descriptor, Request = Synthetic(expanded[index].Command, expanded[index].Argument) }).ToList() };
            macro.Descriptor = Descriptor(macro.Name, macro.Description, plan);
        }

        private static BridgeCommandDescriptor Descriptor(string name, string description, PreparedPlan plan) =>
            new BridgeCommandDescriptor
            {
                Name = name, Description = description, Provider = "macro", ProviderVersion = "2",
                Mode = BridgeDispatch.MaximumMode(plan.Calls), Cost = BridgeDispatch.MaximumCost(plan.Calls),
                RequiresMap = plan.Calls.Any(call => call.Descriptor.RequiresMap), ArgumentSchema = "named parameters",
                ResultSchema = "core.macro", SchemaVersion = 2
            };

        private static BridgeResult DryRun(string argument)
        {
            Dictionary<string, string> parameters = ParseParameters(argument);
            string name = BridgeText.NormalizeCommand(parameters.TryGetValue("name", out string explicitName)
                ? explicitName : (argument ?? string.Empty).Split('&')[0]);
            EnsureLoaded();
            MacroDefinition macro;
            lock (Gate) macros.TryGetValue(name, out macro);
            if (macro == null) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "macro_not_found");
            BridgeResult result = BridgeResult.Ok("core.macroDryRun").Add("name", name)
                .Add("mode", macro.Descriptor.Mode).Add("cost", macro.Descriptor.Cost)
                .Add("steps", macro.ExpandedCalls.Count);
            for (int i = 0; i < macro.ExpandedCalls.Count; i++)
                result.AddLine("step=index:" + i + " command:" + macro.ExpandedCalls[i].Command +
                    " argument:" + BridgeText.Clean(Substitute(macro.ExpandedCalls[i].Argument, parameters)));
            foreach (MacroAssertion assertion in macro.ExpandedAssertions)
                result.AddLine("assertion=step:" + assertion.Step + " status:" +
                    BridgeText.Clean(assertion.Status) + " schema:" + BridgeText.Clean(assertion.Schema) +
                    " field:" + BridgeText.Clean(assertion.Field) + " equals:" + BridgeText.Clean(assertion.Expected));
            return result;
        }

        private static BridgeResult Status()
        {
            EnsureLoaded();
            BridgeResult result = BridgeResult.Ok("core.macroStatus");
            lock (Gate)
            {
                result.Add("path", BridgePaths.MacroPath).Add("commands", macros.Count).Add("errors", errors.Count)
                    .Add("schemaVersion", 2);
                foreach (MacroDefinition macro in macros.Values.OrderBy(item => item.Name))
                    result.AddLine("macro=name:" + macro.Name + " mode:" + macro.Descriptor.Mode +
                        " cost:" + macro.Descriptor.Cost + " steps:" + macro.ExpandedCalls.Count);
                foreach (string error in errors.Take(20)) result.Warn(error);
            }
            return result;
        }

        private static Dictionary<string, string> ParseParameters(string argument)
        {
            Dictionary<string, string> values;
            try { values = BridgeProtocol.ParseOptions(argument ?? string.Empty); }
            catch { values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
            return values;
        }

        private static string Substitute(string value, IDictionary<string, string> parameters)
        {
            string result = value ?? string.Empty;
            foreach (KeyValuePair<string, string> pair in parameters)
                result = result.Replace("${" + pair.Key + "}", pair.Value ?? string.Empty);
            return result;
        }

        private static void MigrateLegacyFile()
        {
            if (File.Exists(BridgePaths.MacroPath)) return;
            if (BridgePaths.UserRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
            string legacy = Path.Combine(Verse.GenFilePaths.SaveDataFolderPath,
                "RimWorld-DevBridge-HotCommands.xml");
            if (!File.Exists(legacy)) return;
            Directory.CreateDirectory(BridgePaths.UserRoot);
            File.Copy(legacy, BridgePaths.MacroPath, false);
        }

        private static DateTime FileWriteUtc(string path) => File.Exists(path)
            ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        private static bool ValidName(string value) => !string.IsNullOrWhiteSpace(value) && value.All(character =>
            char.IsLetterOrDigit(character) || character == '_');
        private static BridgeRequest Synthetic(string command, string argument) => new BridgeRequest
        {
            RequestId = "describe", SessionId = BridgeRuntime.SessionId, Command = BridgeText.NormalizeCommand(command),
            Argument = argument ?? string.Empty, EnqueuedUtc = DateTime.UtcNow,
            DeadlineUtc = DateTime.UtcNow.AddSeconds(5), AllowExpensive = true
        };

        private sealed class MacroDefinition
        {
            internal string Name;
            internal string Description;
            internal bool StopOnError;
            internal List<MacroCall> Calls = new List<MacroCall>();
            internal List<MacroCall> ExpandedCalls = new List<MacroCall>();
            internal List<MacroAssertion> Assertions = new List<MacroAssertion>();
            internal List<MacroAssertion> ExpandedAssertions = new List<MacroAssertion>();
            internal BridgeCommandDescriptor Descriptor;
        }

        private sealed class MacroCall
        {
            internal string Command;
            internal string Argument;
            internal MacroCall Clone() => new MacroCall { Command = Command, Argument = Argument };
        }

        private sealed class PreparedPlan
        {
            internal string Schema = "core.macro";
            internal bool StopOnError = true;
            internal List<PreparedCall> Calls = new List<PreparedCall>();
            internal List<MacroAssertion> Assertions = new List<MacroAssertion>();
        }

        private sealed class MacroAssertion
        {
            internal int Step;
            internal string Status;
            internal string Schema;
            internal string Field;
            internal string Expected;

            internal MacroAssertion Clone(int stepOffset) => new MacroAssertion
            {
                Step = Step + stepOffset,
                Status = Status,
                Schema = Schema,
                Field = Field,
                Expected = Expected
            };

            internal string Validate(IList<BridgeResult> results)
            {
                if (Step < 0 || Step >= results.Count) return "step did not execute";
                BridgeResult result = results[Step];
                if (!string.IsNullOrEmpty(Status) && !string.Equals(result.Status.ToString(), Status,
                    StringComparison.OrdinalIgnoreCase)) return "expected status " + Status + " got " + result.Status;
                if (!string.IsNullOrEmpty(Schema) && !string.Equals(result.Schema, Schema,
                    StringComparison.OrdinalIgnoreCase)) return "expected schema " + Schema + " got " + result.Schema;
                if (!string.IsNullOrEmpty(Field))
                {
                    string actual = result.Data.FirstOrDefault(item => string.Equals(item.Name, Field,
                        StringComparison.OrdinalIgnoreCase))?.Value;
                    if (actual == null) return "field missing: " + Field;
                    if (!string.Equals(actual, Expected ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        return "field " + Field + " expected " + Expected + " got " + actual;
                }
                if (string.IsNullOrEmpty(Status) && string.IsNullOrEmpty(Schema) && string.IsNullOrEmpty(Field))
                    return "assertion has no condition";
                return null;
            }
        }
    }
}
