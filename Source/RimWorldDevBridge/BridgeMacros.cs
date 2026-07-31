using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeMacros
    {
        internal const string ModuleFileName = "RimWorld-DevBridge-HotCommands.xml";
        private static readonly Dictionary<string, Macro> commands =
            new Dictionary<string, Macro>(StringComparer.OrdinalIgnoreCase);
        private static bool initialized;
        private static int generation;
        private static string error = "";

        internal static string ModulePath =>
            Path.Combine(GenFilePaths.SaveDataFolderPath, ModuleFileName);
        internal static int Generation => generation;
        internal static IEnumerable<string> CommandNames => commands.Keys.OrderBy(value => value);
        internal static string FingerprintSource => string.Join("|",
            commands.Values.OrderBy(value => value.name).Select(value =>
                value.name + ":" + value.description + ":" + value.mutating + ":" +
                string.Join(",", value.calls.Select(call =>
                    call.command + "(" + call.argument + ")"))));

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            EnsureTemplate();
            Reload();
        }

        internal static List<string> Reload()
        {
            try
            {
                XDocument document = XDocument.Load(ModulePath);
                Dictionary<string, Macro> replacement =
                    new Dictionary<string, Macro>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement element in document.Root?.Elements("Command") ??
                    Enumerable.Empty<XElement>())
                {
                    string name = ((string)element.Attribute("name") ?? "").Trim().ToUpperInvariant();
                    if (name.Length == 0 || name.Any(character =>
                        !char.IsLetterOrDigit(character) && character != '_'))
                        throw new InvalidDataException("Invalid macro name: " + name);
                    Macro macro = new Macro
                    {
                        name = name,
                        description = (string)element.Attribute("description") ?? "",
                        mutating = string.Equals((string)element.Attribute("mutation"), "true",
                            StringComparison.OrdinalIgnoreCase)
                    };
                    foreach (XElement call in element.Elements("Call"))
                        macro.calls.Add(new MacroCall
                        {
                            command = ((string)call.Attribute("command") ?? "").Trim().ToUpperInvariant(),
                            argument = (string)call.Attribute("argument") ?? ""
                        });
                    replacement[name] = macro;
                }
                commands.Clear();
                foreach (KeyValuePair<string, Macro> pair in replacement) commands[pair.Key] = pair.Value;
                error = "";
                generation++;
                return Status("reloaded");
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return Status("failed");
            }
        }

        internal static bool TryExecute(string name, string argument,
            Func<string, string, List<string>> executor, out List<string> lines)
        {
            lines = null;
            if (!commands.TryGetValue(name ?? "", out Macro macro)) return false;
            lines = new List<string>
            {
                "hotCommand=" + macro.name,
                "generation=" + generation
            };
            foreach (MacroCall call in macro.calls.Take(12))
            {
                string expanded = (call.argument ?? "").Replace("$arg", argument ?? "");
                lines.Add("section=" + call.command);
                lines.AddRange((executor(call.command, expanded) ??
                    new List<string> { "unsupported=" + call.command }).Take(24));
            }
            return true;
        }

        internal static List<string> Status() => Status("status");

        private static List<string> Status(string state) => new List<string>
        {
            "hot=" + state,
            "generation=" + generation,
            "commands=" + commands.Count,
            "names=" + string.Join(",", CommandNames),
            "module=" + ModulePath,
            "error=" + (error.Length == 0 ? "none" : Clean(error))
        };

        private static void EnsureTemplate()
        {
            if (File.Exists(ModulePath)) return;
            new XDocument(
                new XElement("BridgeCommands",
                    new XAttribute("version", "1"),
                    new XElement("Command",
                        new XAttribute("name", "QUICK_STATE"),
                        new XAttribute("description", "One-call compact game and UI state"),
                        new XAttribute("mutation", "false"),
                        new XElement("Call", new XAttribute("command", "SNAPSHOT")),
                        new XElement("Call", new XAttribute("command", "SELECTED")),
                        new XElement("Call", new XAttribute("command", "UI_STATE")))))
                .Save(ModulePath);
        }

        private static string Clean(string value) =>
            (value ?? "none").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

        private sealed class Macro
        {
            public string name;
            public string description;
            public bool mutating;
            public readonly List<MacroCall> calls = new List<MacroCall>();
        }

        private sealed class MacroCall
        {
            public string command;
            public string argument;
        }
    }
}
