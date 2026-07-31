using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeFeatureTests
    {
        private const string LatestFileName = "RimWorld-DevBridge-FeatureTests-Latest.txt";

        internal static string RootPath =>
            Path.Combine(RimWorldDevBridgeMod.RootDir, "DevTools", "FeatureTests");
        internal static string PendingPath => Path.Combine(RootPath, "Pending");
        internal static string CompletedPath => Path.Combine(RootPath, "Completed");
        internal static string LatestPath =>
            Path.Combine(GenFilePaths.SaveDataFolderPath, LatestFileName);

        [DebugAction("RimWorld Dev Bridge", "Test Features", actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void TestFeatures()
        {
            FeatureTestRun run = Run();
            Find.WindowStack.Add(new Window_FeatureTestResults(run));
        }

        internal static List<string> Status()
        {
            EnsureDirectories();
            string[] files = Directory.GetFiles(PendingPath, "*.xml");
            int tests = 0;
            int retries = 0;
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    XElement root = XDocument.Load(files[i]).Root;
                    tests += root?.Elements("Test").Count() ?? 0;
                    retries += int.TryParse((string)root?.Attribute("attempts"),
                        out int attempts) ? attempts : 0;
                }
                catch { tests++; }
            }
            return new List<string>
            {
                "featureTests=pending suites:" + files.Length + " tests:" + tests +
                    " priorAttempts:" + retries,
                "queue=" + PendingPath,
                "latest=" + LatestPath
            };
        }

        internal static List<string> RunForBridge()
        {
            FeatureTestRun run = Run();
            return new List<string>
            {
                "featureTests=" + (run.Failed == 0 ? "PASS" : "FAIL") +
                    " suites:" + run.Suites + " tests:" + run.Results.Count +
                    " pass:" + run.Passed + " fail:" + run.Failed +
                    " requeuedSuites:" + run.RequeuedSuites,
                "latest=" + LatestPath
            }.Concat(run.Results.Where(value => !value.passed).Take(12)
                .Select(value => "FAIL=" + Clean(value.mod) + "/" + Clean(value.feature) +
                    "/" + Clean(value.test) + " " + Clean(value.detail))).ToList();
        }

        private static FeatureTestRun Run()
        {
            EnsureDirectories();
            FeatureTestRun run = new FeatureTestRun { runUtc = DateTime.UtcNow };
            string[] files = Directory.GetFiles(PendingPath, "*.xml")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            run.Suites = files.Length;
            for (int i = 0; i < files.Length; i++)
            {
                int firstResult = run.Results.Count;
                bool passed = ExecuteSuite(files[i], run);
                if (passed)
                    Archive(files[i]);
                else
                {
                    run.RequeuedSuites++;
                    MarkForRetry(files[i], run.Results.Skip(firstResult)
                        .Where(value => !value.passed).ToList());
                }
            }
            WriteLatest(run);
            return run;
        }

        private static bool ExecuteSuite(string path, FeatureTestRun run)
        {
            int firstResult = run.Results.Count;
            string mod = "Unknown mod";
            string feature = Path.GetFileNameWithoutExtension(path);
            try
            {
                XElement root = XDocument.Load(path).Root ??
                    throw new InvalidDataException("Missing FeatureTestSuite root.");
                mod = Attribute(root, "mod", mod);
                feature = Attribute(root, "feature", feature);
                List<XElement> tests = root.Elements("Test").ToList();
                if (tests.Count == 0)
                    throw new InvalidDataException("Suite contains no tests.");
                foreach (XElement test in tests)
                    ExecuteTest(mod, feature, test, run);
            }
            catch (Exception exception)
            {
                run.Results.Add(new FeatureTestResult
                {
                    mod = mod,
                    feature = feature,
                    test = "Queue file",
                    passed = false,
                    detail = exception.GetBaseException().Message
                });
            }
            return run.Results.Count > firstResult &&
                run.Results.Skip(firstResult).All(value => value.passed);
        }

        private static void ExecuteTest(string mod, string feature, XElement test, FeatureTestRun run)
        {
            string name = Attribute(test, "name", "Unnamed test");
            string command = Attribute(test, "command", "").Trim().ToUpperInvariant();
            string argument = Attribute(test, "argument", "");
            FeatureTestResult result = new FeatureTestResult
            {
                mod = mod,
                feature = feature,
                test = name
            };
            try
            {
                if (command.NullOrEmpty())
                    throw new InvalidDataException("Missing command.");
                if (command == "RUN_FEATURE_TESTS")
                    throw new InvalidDataException("Feature-test commands cannot invoke themselves.");
                List<string> response = BridgeHost.ExecuteRegistered(command, argument);
                if (response == null)
                    throw new InvalidOperationException("Unsupported command: " + command);
                string output = string.Join("\n", response);
                foreach (XElement expectation in test.Elements("Expect"))
                {
                    string contains = Attribute(expectation, "contains", "");
                    if (!contains.NullOrEmpty() && output.IndexOf(contains,
                        StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidOperationException("Expected: " + contains);
                }
                foreach (XElement rejection in test.Elements("Reject"))
                {
                    string contains = Attribute(rejection, "contains", "");
                    if (!contains.NullOrEmpty() && output.IndexOf(contains,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("Unexpected: " + contains);
                }
                result.passed = true;
                result.detail = response.FirstOrDefault() ?? "Completed";
            }
            catch (Exception exception)
            {
                result.passed = false;
                result.detail = exception.GetBaseException().Message;
            }
            run.Results.Add(result);
        }

        private static void WriteLatest(FeatureTestRun run)
        {
            List<string> lines = new List<string>
            {
                "feature-tests=v1",
                "runUtc=" + run.runUtc.ToString("s") + "Z",
                "summary=" + (run.Failed == 0 ? "PASS" : "FAIL") +
                    " suites:" + run.Suites + " tests:" + run.Results.Count +
                    " pass:" + run.Passed + " fail:" + run.Failed +
                    " requeuedSuites:" + run.RequeuedSuites,
                "retry=" + (run.RequeuedSuites == 0
                    ? "none"
                    : "failed suites remain pending for the next Test Features run")
            };
            lines.AddRange(run.Results.Take(200).Select(value =>
                (value.passed ? "PASS" : "FAIL") + "|" + Clean(value.mod) + "|" +
                Clean(value.feature) + "|" + Clean(value.test) +
                (value.passed ? "" : "|" + Clean(value.detail))));
            AtomicWrite(LatestPath, lines);
        }

        private static void MarkForRetry(string path, List<FeatureTestResult> failures)
        {
            try
            {
                XDocument document = XDocument.Load(path);
                XElement root = document.Root;
                if (root == null) return;
                int attempts = int.TryParse((string)root.Attribute("attempts"),
                    out int parsed) ? parsed : 0;
                root.SetAttributeValue("attempts", attempts + 1);
                root.SetAttributeValue("lastAttemptUtc", DateTime.UtcNow.ToString("s") + "Z");
                root.SetAttributeValue("lastFailure", Clean(string.Join("; ",
                    failures.Take(3).Select(value => value.test + ": " + value.detail))));
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                document.Save(temp);
                File.Replace(temp, path, null);
            }
            catch (Exception exception)
            {
                Log.Warning("[RimWorld Dev Bridge] Failed suite remains queued, but retry metadata could not be updated: " +
                    exception.GetBaseException().Message);
            }
        }

        private static void Archive(string path)
        {
            try
            {
                string destination = Path.Combine(CompletedPath,
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Path.GetFileName(path));
                File.Move(path, destination);
            }
            catch (Exception exception)
            {
                Log.Warning("[RimWorld Dev Bridge] Could not archive feature test: " +
                    exception.GetBaseException().Message);
            }
        }

        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(PendingPath);
            Directory.CreateDirectory(CompletedPath);
        }

        private static void AtomicWrite(string path, IEnumerable<string> lines)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllLines(temp, lines);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static string Attribute(XElement element, string name, string fallback) =>
            ((string)element.Attribute(name) ?? fallback).Trim();

        private static string Clean(string value) =>
            (value ?? "none").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
    }

    internal sealed class FeatureTestRun
    {
        internal DateTime runUtc;
        internal int Suites;
        internal int RequeuedSuites;
        internal readonly List<FeatureTestResult> Results = new List<FeatureTestResult>();
        internal int Passed => Results.Count(value => value.passed);
        internal int Failed => Results.Count - Passed;
    }

    internal sealed class FeatureTestResult
    {
        internal string mod;
        internal string feature;
        internal string test;
        internal bool passed;
        internal string detail;
    }

    internal sealed class Window_FeatureTestResults : Window
    {
        private readonly FeatureTestRun run;
        private Vector2 scroll;

        internal Window_FeatureTestResults(FeatureTestRun run)
        {
            this.run = run;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(760f, 620f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), "Feature Test Results");
            Text.Font = GameFont.Small;
            string summary = run.Results.Count == 0
                ? "No feature tests were queued."
                : (run.Failed == 0 ? "All tests passed." :
                    run.Failed + " test(s) failed and remain queued for retry.") +
                  "  " + run.Passed + " passed / " + run.Results.Count + " total";
            Color previous = GUI.color;
            GUI.color = run.Failed == 0 ? Color.green : Color.red;
            Widgets.Label(new Rect(inRect.x, inRect.y + 38f, inRect.width, 28f), summary);
            GUI.color = previous;

            Rect viewport = new Rect(0f, 0f, inRect.width - 20f,
                Math.Max(1, run.Results.Count) * 58f);
            Rect outer = new Rect(inRect.x, inRect.y + 72f, inRect.width,
                inRect.height - 118f);
            Widgets.BeginScrollView(outer, ref scroll, viewport);
            for (int i = 0; i < run.Results.Count; i++)
            {
                FeatureTestResult result = run.Results[i];
                Rect row = new Rect(0f, i * 58f, viewport.width, 54f);
                Widgets.DrawBoxSolid(row, i % 2 == 0
                    ? new Color(0.12f, 0.14f, 0.13f, 0.75f)
                    : new Color(0.16f, 0.18f, 0.17f, 0.75f));
                GUI.color = result.passed ? Color.green : Color.red;
                Widgets.Label(new Rect(row.x + 8f, row.y + 5f, 54f, 24f),
                    result.passed ? "PASS" : "FAIL");
                GUI.color = previous;
                Widgets.Label(new Rect(row.x + 68f, row.y + 5f, row.width - 76f, 24f),
                    result.mod + " — " + result.feature);
                GUI.color = Color.gray;
                Widgets.Label(new Rect(row.x + 68f, row.y + 28f, row.width - 76f, 22f),
                    result.test + (result.passed ? "" : " — " + result.detail));
                GUI.color = previous;
                TooltipHandler.TipRegion(row, result.detail ?? "");
            }
            Widgets.EndScrollView();
            if (Widgets.ButtonText(new Rect(inRect.xMax - 120f, inRect.yMax - 38f, 120f, 35f), "Close"))
                Close();
        }
    }
}
