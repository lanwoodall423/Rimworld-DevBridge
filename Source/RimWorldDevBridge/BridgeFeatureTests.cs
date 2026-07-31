using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeFeatureTests
    {
        private const int CompletedHistoryLimit = 100;
        private const int MaximumSuites = 100;
        private const int MaximumTestsPerSuite = 100;
        private const int MaximumStepsPerTest = 100;
        private const int MaximumAssertionsPerTest = 100;
        private const int MaximumTotalTests = 1000;
        private const int MaximumTotalSteps = 5000;
        private const long MaximumSuiteBytes = 1024 * 1024;
        private const string LatestFileName = "Latest.txt";

        internal static IEnumerable<BridgeCommandDescriptor> Commands => new[]
        {
            Descriptor("FEATURE_TESTS", BridgeCommandMode.PureRead, BridgeCostClass.Normal,
                "Feature-test queue and bounded history status."),
            Descriptor("RUN_FEATURE_TESTS", BridgeCommandMode.PersistentMutation, BridgeCostClass.Simulation,
                "Run queued phased feature tests with typed assertions and cleanup."),
            Descriptor("FEATURE_TEST_DRY_RUN", BridgeCommandMode.PureRead, BridgeCostClass.Normal,
                "Validate requirements, phases, commands, modes, and assertions."),
            Descriptor("FEATURE_TEST_RETRY", BridgeCommandMode.PersistentMutation, BridgeCostClass.Trivial,
                "Reset retry metadata for one queued suite."),
            Descriptor("FEATURE_TEST_DISABLE", BridgeCommandMode.PersistentMutation, BridgeCostClass.Trivial,
                "Disable one queued suite."),
            Descriptor("FEATURE_TEST_REMOVE", BridgeCommandMode.PersistentMutation, BridgeCostClass.Trivial,
                "Remove one queued suite.")
        };

        internal static BridgeCommandDescriptor Describe(BridgeRequest request)
        {
            string name = BridgeText.NormalizeCommand(request?.Command);
            if (name == "FEATURE_TESTS") return Descriptor(name, BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, "Feature-test queue and bounded history status.");
            if (name == "RUN_FEATURE_TESTS")
            {
                if (request.NestingDepth > 0)
                    return Descriptor(name, BridgeCommandMode.PotentiallyDestructive, BridgeCostClass.Simulation,
                        "Run queued phased feature tests with typed assertions and cleanup.");
                BridgeResult failure = LoadPlan(null, out TestRunPlan plan);
                return Descriptor(name, failure == null ? plan.Mode : BridgeCommandMode.PotentiallyDestructive,
                    failure == null ? plan.Cost : BridgeCostClass.Simulation,
                    "Run queued phased feature tests with typed assertions and cleanup.");
            }
            if (name == "FEATURE_TEST_DRY_RUN") return Descriptor(name, BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, "Validate requirements, phases, commands, modes, and assertions.");
            if (name == "FEATURE_TEST_RETRY" || name == "FEATURE_TEST_DISABLE" || name == "FEATURE_TEST_REMOVE")
                return Descriptor(name, BridgeCommandMode.PersistentMutation, BridgeCostClass.Trivial,
                    "Manage one queued suite by ID.");
            return null;
        }

        internal static BridgeResult Prepare(BridgeRequest request)
        {
            if (request.Command != "RUN_FEATURE_TESTS") return null;
            if (request.NestingDepth > 0)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "nested_feature_test_run_forbidden");
            BridgeResult failure = LoadPlan(request, out TestRunPlan plan);
            if (failure != null) return failure;
            request.PreparedPayload = plan;
            return null;
        }

        internal static BridgeResult Execute(BridgeExecutionContext context)
        {
            switch (context.Request.Command)
            {
                case "FEATURE_TESTS": return Status();
                case "FEATURE_TEST_DRY_RUN": return DryRun();
                case "FEATURE_TEST_RETRY": return Manage(context.Request.Argument, "retry");
                case "FEATURE_TEST_DISABLE": return Manage(context.Request.Argument, "disable");
                case "FEATURE_TEST_REMOVE": return Manage(context.Request.Argument, "remove");
                case "RUN_FEATURE_TESTS": return Run(context);
                default: return null;
            }
        }

        [DebugAction("RimWorld Dev Bridge", "Test Features", actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void TestFeatures()
        {
            Messages.Message("Use RUN_FEATURE_TESTS through the bridge so write leases, deadlines, and evidence are enforced.",
                MessageTypeDefOf.CautionInput, false);
        }

        private static BridgeResult Status()
        {
            List<string> pending = PendingSuiteFiles();
            int legacyPending = pending.Count(IsLegacyPath);
            List<string> disabled = SuiteFiles(DisabledPath);
            List<string> completed = SuiteFiles(CompletedPath);
            int tests = 0;
            int attempts = 0;
            foreach (string path in pending)
            {
                try
                {
                    XElement root = LoadDocument(path).Root;
                    tests += root?.Elements("Test").Count() ?? 0;
                    attempts += IntAttribute(root, "attempts", 0);
                }
                catch { tests++; }
            }
            return BridgeResult.Ok("core.featureTestStatus").Add("pendingSuites", pending.Count)
                .Add("pendingTests", tests).Add("priorAttempts", attempts).Add("disabledSuites", disabled.Count)
                .Add("completedHistory", completed.Count).Add("legacyPending", legacyPending).Add("queue", PendingPath)
                .Add("latest", LatestPath).Add("schemaVersion", 2);
        }

        private static BridgeResult DryRun()
        {
            BridgeResult failure = LoadPlan(null, out TestRunPlan plan);
            if (failure != null) return failure;
            BridgeResult result = BridgeResult.Ok("core.featureTestDryRun").Add("suites", plan.Suites.Count)
                .Add("tests", plan.Suites.Sum(suite => suite.Tests.Count)).Add("mode", plan.Mode)
                .Add("cost", plan.Cost).Add("schemaVersion", 2);
            foreach (SuitePlan suite in plan.Suites)
            {
                result.AddLine("suite=id:" + suite.Id + " mod:" + BridgeText.Clean(suite.Mod) +
                    " feature:" + BridgeText.Clean(suite.Feature) + " tests:" + suite.Tests.Count +
                    " mode:" + suite.Mode + " blocked:" + BridgeText.Clean(suite.BlockedReason));
                foreach (TestPlan test in suite.Tests)
                    result.AddLine("test=suite:" + suite.Id + " name:" + BridgeText.Clean(test.Name) +
                        " mode:" + test.Mode + " cost:" + test.Cost + " steps:" + test.Steps.Count +
                        " assertions:" + test.Assertions.Count);
            }
            return result;
        }

        private static BridgeResult Run(BridgeExecutionContext context)
        {
            TestRunPlan plan = context.Request.PreparedPayload as TestRunPlan;
            if (plan == null) return BridgeResult.Fail(BridgeStatus.ERROR, "feature_test_plan_missing");
            context.ThrowIfCancellationRequested();
            EnsureDirectories();
            MigratePlannedSuites(plan);
            TestRunEvidence evidence = new TestRunEvidence { RunUtc = DateTime.UtcNow };
            foreach (SuitePlan suite in plan.Suites)
            {
                SuiteEvidence suiteEvidence = RunSuite(context, suite);
                evidence.Suites.Add(suiteEvidence);
                if (suiteEvidence.Status == BridgeStatus.OK) Archive(suite.Path);
                else if (suiteEvidence.Status != BridgeStatus.BLOCKED) MarkAttempt(suite.Path, suiteEvidence);
            }
            WriteLatest(evidence);
            TrimHistory();
            int passed = evidence.Suites.Sum(suite => suite.Tests.Count(test => test.Status == BridgeStatus.OK));
            int blocked = evidence.Suites.Sum(suite => suite.Tests.Count(test => test.Status == BridgeStatus.BLOCKED));
            int failed = evidence.Suites.Sum(suite => suite.Tests.Count(test =>
                test.Status != BridgeStatus.OK && test.Status != BridgeStatus.BLOCKED));
            BridgeResult result = BridgeResult.Ok("core.featureTestRun")
                .Add("suites", evidence.Suites.Count).Add("tests", passed + blocked + failed)
                .Add("passed", passed).Add("blocked", blocked).Add("failed", failed).Add("latest", LatestPath);
            foreach (SuiteEvidence suite in evidence.Suites)
                foreach (TestEvidence test in suite.Tests)
                    result.AddLine("test=suite:" + suite.Id + " name:" + BridgeText.Clean(test.Name) +
                        " status:" + test.Status + " category:" + BridgeText.Clean(test.Category) +
                        " detail:" + BridgeText.Clean(test.Detail));
            if (failed > 0) result.Status = BridgeStatus.PARTIAL;
            else if (blocked > 0) result.Status = BridgeStatus.BLOCKED;
            if (context.Request.Mode != BridgeCommandMode.PureRead)
                result.MutationSummary = "feature tests ran with cleanup; inspect per-test evidence";
            return result;
        }

        private static SuiteEvidence RunSuite(BridgeExecutionContext context, SuitePlan suite)
        {
            SuiteEvidence evidence = new SuiteEvidence { Id = suite.Id, Mod = suite.Mod, Feature = suite.Feature };
            string blockedReason = suite.BlockedReason ?? EvaluateRequirements(suite.Requirements);
            if (!string.IsNullOrEmpty(blockedReason))
            {
                foreach (TestPlan test in suite.Tests) evidence.Tests.Add(new TestEvidence
                {
                    Name = test.Name, Status = BridgeStatus.BLOCKED, Category = "prerequisite",
                    Detail = blockedReason
                });
                evidence.Status = BridgeStatus.BLOCKED;
                return evidence;
            }
            foreach (TestPlan test in suite.Tests)
                evidence.Tests.Add(RunTest(context, test));
            evidence.Status = evidence.Tests.All(test => test.Status == BridgeStatus.OK) ? BridgeStatus.OK :
                evidence.Tests.Any(test => test.Status != BridgeStatus.BLOCKED) ? BridgeStatus.ERROR : BridgeStatus.BLOCKED;
            return evidence;
        }

        private static TestEvidence RunTest(BridgeExecutionContext context, TestPlan test)
        {
            TestEvidence evidence = new TestEvidence { Name = test.Name, Status = BridgeStatus.OK, Category = "none" };
            string blockedReason = EvaluateRequirements(test.Requirements);
            if (!string.IsNullOrEmpty(blockedReason))
            {
                evidence.Status = BridgeStatus.BLOCKED;
                evidence.Category = "prerequisite";
                evidence.Detail = blockedReason;
                return evidence;
            }
            Dictionary<string, BridgeResult> results = new Dictionary<string, BridgeResult>(StringComparer.OrdinalIgnoreCase);
            int tickStart = context.Tick;
            bool pushedRandom = false;
            bool started = false;
            try
            {
                if (test.RandomSeed.HasValue) { Rand.PushState(test.RandomSeed.Value); pushedRandom = true; }
                foreach (PreparedTestStep step in test.Steps.Where(item => item.Phase != "cleanup"))
                {
                    context.ThrowIfCancellationRequested();
                    started = true;
                    BridgeResult result = BridgeDispatch.ExecuteChild(context, step.Call);
                    results[step.Id] = result;
                    evidence.Steps.Add(step.Phase + ":" + step.Id + ":" + result.Status);
                    bool statusIsAsserted = test.Assertions.Any(assertion =>
                        string.Equals(assertion.Step, step.Id, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(assertion.Kind, "Status", StringComparison.OrdinalIgnoreCase));
                    if (result.Status != BridgeStatus.OK && step.StopOnError && !statusIsAsserted)
                        throw new FeatureAssertionException("command_status", step.Id + " returned " + result.Status);
                    if (test.TickBudget > 0 && context.Tick - tickStart > test.TickBudget)
                        throw new FeatureAssertionException("tick_budget", "tick budget exceeded");
                    context.ThrowIfCancellationRequested();
                }
                foreach (TestAssertion assertion in test.Assertions)
                    assertion.Assert(results);
            }
            catch (FeatureAssertionException exception)
            {
                evidence.Status = BridgeStatus.ERROR;
                evidence.Category = exception.Category;
                evidence.Detail = exception.Message;
            }
            catch (OperationCanceledException)
            {
                evidence.Status = context.Request.Expired ? BridgeStatus.TIMEOUT : BridgeStatus.CANCELLED;
                evidence.Category = context.Request.Expired ? "timeout" : "cancelled";
                evidence.Detail = "request cancelled during feature test";
            }
            catch (Exception exception)
            {
                evidence.Status = BridgeStatus.ERROR;
                evidence.Category = "exception";
                evidence.Detail = exception.GetBaseException().GetType().Name + ": " +
                    exception.GetBaseException().Message;
            }
            finally
            {
                foreach (PreparedTestStep cleanup in test.Steps.Where(item => started && item.Phase == "cleanup"))
                {
                    try
                    {
                        BridgeResult cleanupResult = BridgeDispatch.ExecuteCleanupChild(context, cleanup.Call);
                        evidence.Steps.Add("cleanup:" + cleanup.Id + ":" + cleanupResult.Status);
                        if (!cleanupResult.IsSuccess && evidence.Status == BridgeStatus.OK)
                        {
                            evidence.Status = BridgeStatus.ERROR;
                            evidence.Category = "cleanup";
                            evidence.Detail = cleanup.Id + " returned " + cleanupResult.Status;
                        }
                    }
                    catch (Exception exception)
                    {
                        evidence.Steps.Add("cleanup:" + cleanup.Id + ":ERROR");
                        if (evidence.Status == BridgeStatus.OK)
                        {
                            evidence.Status = BridgeStatus.ERROR;
                            evidence.Category = "cleanup";
                            evidence.Detail = exception.GetBaseException().Message;
                        }
                    }
                }
                if (pushedRandom) Rand.PopState();
            }
            if (evidence.Status == BridgeStatus.OK) evidence.Detail = "all typed assertions and cleanup passed";
            return evidence;
        }

        private static BridgeResult LoadPlan(BridgeRequest parent, out TestRunPlan plan)
        {
            plan = new TestRunPlan();
            List<string> paths = PendingSuiteFiles();
            if (paths.Count > MaximumSuites)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "too_many_feature_test_suites");
            foreach (string path in paths)
            {
                try
                {
                    SuitePlan suite = ParseSuite(path, parent);
                    suite.Legacy = IsLegacyPath(path);
                    plan.Suites.Add(suite);
                }
                catch (Exception exception)
                {
                    return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_feature_test_suite",
                        Path.GetFileName(path) + ": " + exception.GetBaseException().Message);
                }
            }
            if (plan.Suites.Sum(suite => suite.Tests.Count) > MaximumTotalTests)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "too_many_feature_tests");
            if (plan.Suites.Sum(suite => suite.Tests.Sum(test => test.Steps.Count)) > MaximumTotalSteps)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "too_many_feature_test_steps");
            plan.Mode = plan.Suites.Select(suite => suite.Mode)
                .DefaultIfEmpty(BridgeCommandMode.PersistentMutation)
                .Concat(new[] { BridgeCommandMode.PersistentMutation }).Max();
            plan.Cost = plan.Suites.Select(suite => suite.Cost)
                .DefaultIfEmpty(BridgeCostClass.Normal).Max();
            return null;
        }

        private static SuitePlan ParseSuite(string path, BridgeRequest parent)
        {
            XElement root = LoadDocument(path).Root ??
                throw new InvalidDataException("Missing FeatureTestSuite root.");
            if (root.Name != "FeatureTestSuite") throw new InvalidDataException("Expected FeatureTestSuite root.");
            SuitePlan suite = new SuitePlan
            {
                Path = path,
                Id = Attribute(root, "id", Path.GetFileNameWithoutExtension(path)),
                Mod = Attribute(root, "mod", "Unknown"),
                Feature = Attribute(root, "feature", Path.GetFileNameWithoutExtension(path)),
                MaximumAttempts = IntAttribute(root, "maxAttempts", 0),
                Requirements = ParseRequirements(root)
            };
            int attempts = IntAttribute(root, "attempts", 0);
            if (suite.MaximumAttempts > 0 && attempts >= suite.MaximumAttempts)
                suite.BlockedReason = "maximum retry policy reached";
            foreach (XElement testElement in root.Elements("Test"))
                suite.Tests.Add(ParseTest(testElement, parent));
            if (suite.Tests.Count == 0) throw new InvalidDataException("Suite has no tests.");
            if (suite.Tests.Count > MaximumTestsPerSuite) throw new InvalidDataException("Suite has too many tests.");
            suite.Mode = suite.Tests.Select(test => test.Mode).Max();
            suite.Cost = suite.Tests.Select(test => test.Cost).Max();
            return suite;
        }

        private static TestPlan ParseTest(XElement element, BridgeRequest parent)
        {
            TestPlan test = new TestPlan
            {
                Name = Attribute(element, "name", "unnamed"),
                TickBudget = IntAttribute(element, "tickBudget", 0),
                RandomSeed = NullableIntAttribute(element, "randomSeed")
            };
            IEnumerable<XElement> phaseElements = element.Elements().Where(child =>
                child.Name == "Requirements" || child.Name == "Setup" || child.Name == "Action" ||
                child.Name == "Assertions" || child.Name == "Cleanup");
            if (!phaseElements.Any() && element.Attribute("command") != null)
            {
                XElement action = new XElement("Action", new XElement("Call",
                    new XAttribute("id", "action"), new XAttribute("command", (string)element.Attribute("command")),
                    new XAttribute("argument", (string)element.Attribute("argument") ?? string.Empty)));
                phaseElements = new[] { action };
            }
            foreach (XElement phase in phaseElements)
            {
                string phaseName = phase.Name.LocalName.ToLowerInvariant();
                if (phaseName == "requirements")
                {
                    test.Requirements = ParseRequirements(phase);
                    continue;
                }
                if (phaseName == "assertions")
                {
                    foreach (XElement assertion in phase.Elements()) test.Assertions.Add(ParseAssertion(assertion));
                    continue;
                }
                foreach (XElement callElement in phase.Elements("Call"))
                {
                    BridgeRequest owner = parent ?? SyntheticParent();
                    string command = Attribute(callElement, "command", null);
                    BridgeResult failure = BridgeDispatch.PrepareChild(owner, command,
                        Attribute(callElement, "argument", string.Empty), out PreparedCall call);
                    if (failure != null) throw new InvalidDataException(test.Name + ": " + Field(failure, "detail",
                        Field(failure, "error", failure.Status.ToString())));
                    test.Steps.Add(new PreparedTestStep
                    {
                        Id = Attribute(callElement, "id", phaseName + test.Steps.Count),
                        Phase = phaseName,
                        Call = call,
                        StopOnError = !string.Equals(Attribute(callElement, "onError", "stop"), "continue",
                            StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            foreach (XElement legacy in element.Elements("Expect"))
                test.Assertions.Add(new TestAssertion { Step = "action", Kind = "contains",
                    Value = Attribute(legacy, "contains", string.Empty) });
            foreach (XElement legacy in element.Elements("Reject"))
                test.Assertions.Add(new TestAssertion { Step = "action", Kind = "notContains",
                    Value = Attribute(legacy, "contains", string.Empty) });
            if (test.Steps.Count == 0) throw new InvalidDataException(test.Name + ": no executable calls.");
            if (test.Steps.Count > MaximumStepsPerTest) throw new InvalidDataException(test.Name + ": too many calls.");
            if (test.Assertions.Count > MaximumAssertionsPerTest)
                throw new InvalidDataException(test.Name + ": too many assertions.");
            test.Mode = test.Steps.Select(step => step.Call.Descriptor.Mode).Max();
            test.Cost = test.Steps.Select(step => step.Call.Descriptor.Cost).Max();
            return test;
        }

        private static TestAssertion ParseAssertion(XElement element)
        {
            return new TestAssertion
            {
                Step = Attribute(element, "step", "action"),
                Kind = element.Name.LocalName,
                Field = Attribute(element, "field", null),
                Operator = Attribute(element, "op", "eq"),
                Value = Attribute(element, "value", Attribute(element, "contains", string.Empty)),
                Minimum = NullableDoubleAttribute(element, "min"),
                Maximum = NullableDoubleAttribute(element, "max")
            };
        }

        private static BridgeResult Manage(string id, string operation)
        {
            string safeId = SafeId(id);
            if (safeId == null) return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_suite_id");
            EnsureDirectories();
            List<string> matches = PendingSuiteFiles().Where(path => SuiteMatches(path, safeId)).ToList();
            if (matches.Count > 1) return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "ambiguous_suite_id");
            string source = matches.SingleOrDefault();
            if (source == null) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "suite_not_found");
            if (IsLegacyPath(source))
            {
                string target = Path.Combine(PendingPath, Path.GetFileName(source));
                if (!File.Exists(target)) File.Copy(source, target, false);
                source = target;
            }
            if (operation == "remove") File.Delete(source);
            else if (operation == "disable") File.Move(source, Path.Combine(DisabledPath, Path.GetFileName(source)));
            else
            {
                XDocument document = LoadDocument(source);
                document.Root?.SetAttributeValue("attempts", 0);
                document.Root?.SetAttributeValue("lastFailure", null);
                AtomicSave(document, source);
            }
            return BridgeResult.Ok("core.featureTestManage").Add("suite", safeId).Add("operation", operation)
                .WithMutation(operation + " feature-test suite " + safeId);
        }

        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(PendingPath);
            Directory.CreateDirectory(CompletedPath);
            Directory.CreateDirectory(DisabledPath);
        }

        private static void MigratePlannedSuites(TestRunPlan plan)
        {
            foreach (SuitePlan suite in plan.Suites.Where(item => item.Legacy))
            {
                string target = Path.Combine(PendingPath, Path.GetFileName(suite.Path));
                if (!File.Exists(target)) File.Copy(suite.Path, target, false);
                suite.Path = target;
                suite.Legacy = false;
            }
        }

        private static void MarkAttempt(string path, SuiteEvidence evidence)
        {
            try
            {
                XDocument document = LoadDocument(path);
                XElement root = document.Root;
                int attempts = IntAttribute(root, "attempts", 0) + 1;
                root?.SetAttributeValue("attempts", attempts);
                root?.SetAttributeValue("lastAttemptUtc", DateTime.UtcNow.ToString("o"));
                root?.SetAttributeValue("lastStatus", evidence.Status);
                root?.SetAttributeValue("lastFailure", string.Join("; ", evidence.Tests
                    .Where(test => test.Status != BridgeStatus.OK).Take(3)
                    .Select(test => test.Name + ": " + test.Detail)));
                AtomicSave(document, path);
            }
            catch (Exception exception) { Log.Warning("[RimWorld Dev Bridge] Feature retry metadata: " + exception.Message); }
        }

        private static void Archive(string path)
        {
            string target = Path.Combine(CompletedPath, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" +
                Path.GetFileName(path));
            File.Move(path, target);
        }

        private static void TrimHistory()
        {
            foreach (string path in SuiteFiles(CompletedPath).OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(CompletedHistoryLimit))
                File.Delete(path);
        }

        private static void WriteLatest(TestRunEvidence evidence)
        {
            List<string> lines = new List<string>
            {
                "feature-tests=v2", "runUtc=" + evidence.RunUtc.ToString("o"),
                "suites=" + evidence.Suites.Count
            };
            foreach (SuiteEvidence suite in evidence.Suites)
                foreach (TestEvidence test in suite.Tests)
                    lines.Add(test.Status + "|" + BridgeText.Clean(suite.Id) + "|" + BridgeText.Clean(test.Name) +
                        "|" + BridgeText.Clean(test.Category) + "|" + BridgeText.Clean(test.Detail));
            string temp = LatestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllLines(temp, lines);
            if (File.Exists(LatestPath)) File.Delete(LatestPath);
            File.Move(temp, LatestPath);
        }

        private static void AtomicSave(XDocument document, string path)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            document.Save(temp);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static XDocument LoadDocument(string path)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("Feature-test suite was not found.", path);
            if (file.Length > MaximumSuiteBytes) throw new InvalidDataException("Feature-test suite exceeds 1 MiB.");
            XmlReaderSettings settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumSuiteBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using (XmlReader reader = XmlReader.Create(path, settings))
                return XDocument.Load(reader, LoadOptions.None);
        }

        private static RequirementPlan ParseRequirements(XElement element)
        {
            RequirementPlan plan = new RequirementPlan
            {
                RequiresMap = string.Equals(Attribute(element, "requiredMap", null), "true",
                    StringComparison.OrdinalIgnoreCase),
                RequiredSave = Attribute(element, "requiredSave", null)
            };
            plan.RequiredMods.AddRange(Split(Attribute(element, "requiredMods", null)));
            plan.RequiredAdapters.AddRange(Split(Attribute(element, "requiredAdapters", null)));
            foreach (XElement child in element?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (child.Name == "Mod")
                    plan.RequiredMods.Add(Attribute(child, "packageId", Attribute(child, "id", null)));
                else if (child.Name == "Adapter")
                    plan.RequiredAdapters.Add(Attribute(child, "id", null));
                else if (child.Name == "Map") plan.RequiresMap = true;
            }
            plan.RequiredMods.RemoveAll(string.IsNullOrWhiteSpace);
            plan.RequiredAdapters.RemoveAll(string.IsNullOrWhiteSpace);
            return plan;
        }

        private static string EvaluateRequirements(RequirementPlan plan)
        {
            if (plan == null) return null;
            if (plan.RequiresMap && Find.CurrentMap == null) return "current map required";
            if (!string.IsNullOrEmpty(plan.RequiredSave) &&
                !File.Exists(GenFilePaths.FilePathForSavedGame(plan.RequiredSave)))
                return "required save missing: " + plan.RequiredSave;
            foreach (string package in plan.RequiredMods)
                if (!LoadedModManager.RunningModsListForReading.Any(mod => string.Equals(
                    mod.PackageIdPlayerFacing, package, StringComparison.OrdinalIgnoreCase)))
                    return "required mod missing: " + package;
            foreach (string adapter in plan.RequiredAdapters)
                if (!BridgeAdapterCatalog.IsAvailable(adapter)) return "required adapter missing: " + adapter;
            return null;
        }

        private static string Field(BridgeResult result, string name, string fallback) =>
            result?.Data.FirstOrDefault(item => string.Equals(item.Name, name,
                StringComparison.OrdinalIgnoreCase))?.Value ?? fallback;
        private static string Attribute(XElement element, string name, string fallback) =>
            ((string)element?.Attribute(name) ?? fallback)?.Trim();
        private static int IntAttribute(XElement element, string name, int fallback) =>
            int.TryParse(Attribute(element, name, null), out int value) ? value : fallback;
        private static int? NullableIntAttribute(XElement element, string name) =>
            int.TryParse(Attribute(element, name, null), out int value) ? value : (int?)null;
        private static double? NullableDoubleAttribute(XElement element, string name) =>
            double.TryParse(Attribute(element, name, null), NumberStyles.Float, CultureInfo.InvariantCulture,
                out double value) ? value : (double?)null;
        private static IEnumerable<string> Split(string value) => (value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim());
        private static List<string> SuiteFiles(string path) => Directory.Exists(path)
            ? Directory.GetFiles(path, "*.xml").OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        private static List<string> PendingSuiteFiles() => SuiteFiles(PendingPath)
            .Concat(EligibleLegacyFiles()).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        private static List<string> EligibleLegacyFiles()
        {
            HashSet<string> handled = new HashSet<string>(SuiteFiles(PendingPath)
                .Concat(SuiteFiles(DisabledPath)).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            List<string> completed = SuiteFiles(CompletedPath).Select(Path.GetFileName).ToList();
            return SuiteFiles(LegacyPendingPath).Where(path =>
            {
                string name = Path.GetFileName(path);
                return !handled.Contains(name) && !completed.Any(value => value.EndsWith("-" + name,
                    StringComparison.OrdinalIgnoreCase));
            }).ToList();
        }
        private static bool IsLegacyPath(string path) => Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(LegacyPendingPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static bool SuiteMatches(string path, string id)
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.OrdinalIgnoreCase)) return true;
            try { return string.Equals(Attribute(LoadDocument(path).Root, "id", null), id,
                StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        private static string SafeId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 96 &&
            value.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_')
            ? value : null;
        private static BridgeRequest SyntheticParent() => new BridgeRequest
        {
            RequestId = "feature-describe", SessionId = BridgeRuntime.SessionId, EnqueuedUtc = DateTime.UtcNow,
            DeadlineUtc = DateTime.UtcNow.AddSeconds(30), AllowExpensive = true
        };

        private static BridgeCommandDescriptor Descriptor(string name, BridgeCommandMode mode, BridgeCostClass cost,
            string description) => new BridgeCommandDescriptor
        {
            Name = name, Description = description, Provider = "feature-tests", ProviderVersion = "2",
            Mode = mode, Cost = cost, RequiresMap = false, ArgumentSchema = "suite XML v2",
            ResultSchema = "core.featureTests", SchemaVersion = 2, MinimumExecutionBudgetMs = 100
        };

        private static string PendingPath => Path.Combine(BridgePaths.FeatureTestPath, "Pending");
        private static string CompletedPath => Path.Combine(BridgePaths.FeatureTestPath, "Completed");
        private static string DisabledPath => Path.Combine(BridgePaths.FeatureTestPath, "Disabled");
        private static string LatestPath => Path.Combine(BridgePaths.FeatureTestPath, LatestFileName);
        private static string LegacyPendingPath => Path.Combine(BridgePaths.ModRoot, "DevTools", "FeatureTests", "Pending");

        private sealed class TestRunPlan
        {
            internal List<SuitePlan> Suites = new List<SuitePlan>();
            internal BridgeCommandMode Mode;
            internal BridgeCostClass Cost;
        }

        private sealed class SuitePlan
        {
            internal string Path;
            internal string Id;
            internal string Mod;
            internal string Feature;
            internal int MaximumAttempts;
            internal string BlockedReason;
            internal RequirementPlan Requirements;
            internal bool Legacy;
            internal List<TestPlan> Tests = new List<TestPlan>();
            internal BridgeCommandMode Mode;
            internal BridgeCostClass Cost;
        }

        private sealed class TestPlan
        {
            internal string Name;
            internal int TickBudget;
            internal int? RandomSeed;
            internal RequirementPlan Requirements;
            internal List<PreparedTestStep> Steps = new List<PreparedTestStep>();
            internal List<TestAssertion> Assertions = new List<TestAssertion>();
            internal BridgeCommandMode Mode;
            internal BridgeCostClass Cost;
        }

        private sealed class RequirementPlan
        {
            internal bool RequiresMap;
            internal string RequiredSave;
            internal List<string> RequiredMods = new List<string>();
            internal List<string> RequiredAdapters = new List<string>();
        }

        private sealed class PreparedTestStep
        {
            internal string Id;
            internal string Phase;
            internal PreparedCall Call;
            internal bool StopOnError;
        }

        private sealed class TestAssertion
        {
            internal string Step;
            internal string Kind;
            internal string Field;
            internal string Operator;
            internal string Value;
            internal double? Minimum;
            internal double? Maximum;

            internal void Assert(IDictionary<string, BridgeResult> results)
            {
                if (!results.TryGetValue(Step, out BridgeResult result))
                    throw new FeatureAssertionException("assertion_target", "missing step result: " + Step);
                string kind = (Kind ?? string.Empty).ToLowerInvariant();
                if (kind == "status") Compare(result.Status.ToString(), Value, "status");
                else if (kind == "schema") Compare(result.Schema, Value, "schema");
                else if (kind == "noexception")
                {
                    if (result.Status == BridgeStatus.ERROR) throw new FeatureAssertionException("exception", Step + " errored");
                }
                else if (kind == "contains" || kind == "notcontains")
                {
                    string all = string.Join("\n", result.Data.Select(item => item.Name + "=" + item.Value)
                        .Concat(result.Lines));
                    bool found = all.IndexOf(Value ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
                    if ((kind == "contains" && !found) || (kind == "notcontains" && found))
                        throw new FeatureAssertionException("text_assertion", kind + " failed for " + Value);
                }
                else
                {
                    string actual = result.Data.FirstOrDefault(item => string.Equals(item.Name, Field,
                        StringComparison.OrdinalIgnoreCase))?.Value;
                    if (actual == null) throw new FeatureAssertionException("field_missing", Field + " missing");
                    if (kind == "boolean")
                    {
                        if (!bool.TryParse(actual, out bool parsed) || !bool.TryParse(Value, out bool expected) || parsed != expected)
                            throw new FeatureAssertionException("boolean_assertion", Field + " expected " + Value + " got " + actual);
                    }
                    else if (kind == "number" || kind == "range" || kind == "count")
                    {
                        if (!double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                            throw new FeatureAssertionException("numeric_assertion", Field + " is not numeric: " + actual);
                        if (Minimum.HasValue && number < Minimum.Value || Maximum.HasValue && number > Maximum.Value)
                            throw new FeatureAssertionException("range_assertion", Field + " out of range: " + number);
                        if (!string.IsNullOrEmpty(Value) && !CompareNumber(number, Value, Operator))
                            throw new FeatureAssertionException("numeric_assertion", Field + " comparison failed");
                    }
                    else if (kind == "member")
                    {
                        if (!(actual ?? string.Empty).Split(',').Any(item => string.Equals(item.Trim(), Value,
                            StringComparison.OrdinalIgnoreCase)))
                            throw new FeatureAssertionException("membership_assertion", Value + " not in " + actual);
                    }
                    else Compare(actual, Value, Field);
                }
            }

            private static void Compare(string actual, string expected, string label)
            {
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new FeatureAssertionException("exact_assertion", label + " expected " + expected + " got " + actual);
            }

            private static bool CompareNumber(double actual, string expectedText, string op)
            {
                if (!double.TryParse(expectedText, NumberStyles.Float, CultureInfo.InvariantCulture, out double expected)) return false;
                switch ((op ?? "eq").ToLowerInvariant())
                {
                    case "gt": return actual > expected;
                    case "gte": return actual >= expected;
                    case "lt": return actual < expected;
                    case "lte": return actual <= expected;
                    case "ne": return Math.Abs(actual - expected) > 0.0000001d;
                    default: return Math.Abs(actual - expected) <= 0.0000001d;
                }
            }
        }

        private sealed class FeatureAssertionException : Exception
        {
            internal string Category { get; }
            internal FeatureAssertionException(string category, string message) : base(message) { Category = category; }
        }

        private sealed class TestRunEvidence { internal DateTime RunUtc; internal List<SuiteEvidence> Suites = new List<SuiteEvidence>(); }
        private sealed class SuiteEvidence
        {
            internal string Id; internal string Mod; internal string Feature; internal BridgeStatus Status;
            internal List<TestEvidence> Tests = new List<TestEvidence>();
        }
        private sealed class TestEvidence
        {
            internal string Name; internal BridgeStatus Status; internal string Category; internal string Detail;
            internal List<string> Steps = new List<string>();
        }
    }
}
