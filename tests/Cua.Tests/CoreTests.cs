using System.Text.Json;
using Cua.Core.Artifacts;
using Cua.Core.Policy;
using Cua.Core.Redaction;
using Cua.Engine.Discovery;
using Xunit;

namespace Cua.Tests;

public sealed class RedactorTests
{
    [Fact]
    public void MasksSecretLiterals_AndBuiltInPatterns()
    {
        var r = Redactor.CreateDefault();
        r.AddLiteral("hunter2");
        var input = "logged in with password=hunter2 key sk-abcdef1234567890XYZjk and ssn 123-45-6789";
        var output = r.Apply(input);
        Assert.DoesNotContain("hunter2", output);
        Assert.DoesNotContain("sk-abcdef1234567890XYZjk", output);
        Assert.DoesNotContain("123-45-6789", output);
    }

    [Fact]
    public void MasksAddedPatterns_LeavesOtherTextIntact()
    {
        var r = Redactor.CreateDefault();
        r.AddPattern(@"\b12345\b");
        var output = r.Apply("account 12345 balance 1,284.09");
        Assert.DoesNotContain("12345", output);
        Assert.Contains("1,284.09", output);
    }
}

public sealed class RedactorDocumentTests
{
    [Fact]
    public void DocumentTier_MasksBystanderDigitRuns_LogTierDoesNot()
    {
        var r = Redactor.CreateDefault();
        var screen = "ACCT 40219 | S J BRENNEMAN | FEE-88220 | 30.00";
        // log lines keep run ids and timestamps legible
        Assert.Contains("40219", r.Apply(screen));
        // captured screen content masks account-shaped digit runs
        var doc = r.ApplyToDocument(screen);
        Assert.DoesNotContain("40219", doc);
        Assert.DoesNotContain("88220", doc);
        // short numbers (amounts, dates) survive
        Assert.Contains("30.00", doc);
    }
}

public sealed class PolicyGateTests
{
    private static PolicyGate Gate(RiskyActionMode mode = RiskyActionMode.Flag) =>
        new(new PolicyConfig { AllowedHosts = ["127.0.0.1:8080"], RiskyMode = mode });

    [Fact]
    public void Navigation_OutsideAllowlist_Blocked()
    {
        Assert.True(Gate().CheckNavigation("https://evil.example.com/x").IsBlocked);
        Assert.False(Gate().CheckNavigation("http://127.0.0.1:8080/portal").IsBlocked);
    }

    [Fact]
    public void ActionOnPageOutsideAllowlist_Blocked()
    {
        var d = Gate().CheckAction(StepAction.Click, "https://phish.example.com/", RiskLevel.Safe, "OK");
        Assert.True(d.IsBlocked);
    }

    [Fact]
    public void RiskyText_UpgradesRisk_EvenWhenModelUnderDeclares()
    {
        var d = Gate().CheckAction(StepAction.Click, "http://127.0.0.1:8080/", RiskLevel.Safe, "Waive Fee");
        Assert.Equal(RiskLevel.Irreversible, d.EffectiveRisk);
        Assert.Equal(PolicyVerdict.AllowedWithFlag, d.Verdict);
    }

    [Fact]
    public void RiskyModes_BlockAndConfirm()
    {
        Assert.Equal(PolicyVerdict.Blocked,
            Gate(RiskyActionMode.Block).CheckAction(StepAction.Click, "http://127.0.0.1:8080/", RiskLevel.Irreversible, "Commit Changes").Verdict);
        Assert.Equal(PolicyVerdict.NeedsConfirmation,
            Gate(RiskyActionMode.Confirm).CheckAction(StepAction.Click, "http://127.0.0.1:8080/", RiskLevel.Irreversible, "Commit Changes").Verdict);
    }

    [Fact]
    public void RouteScopedAllowlist_LimitsPaths_BareHostAllowsAll()
    {
        var gate = new PolicyGate(new PolicyConfig { AllowedHosts = ["127.0.0.1:8080/portal"] });
        Assert.False(gate.CheckNavigation("http://127.0.0.1:8080/portal/Main.do").IsBlocked);
        Assert.True(gate.CheckNavigation("http://127.0.0.1:8080/admin/console").IsBlocked);
        Assert.True(gate.CheckAction(StepAction.Click, "http://127.0.0.1:8080/admin/x", RiskLevel.Safe, "OK").IsBlocked);

        var bare = new PolicyGate(new PolicyConfig { AllowedHosts = ["127.0.0.1:8080"] });
        Assert.False(bare.CheckNavigation("http://127.0.0.1:8080/anything/at/all").IsBlocked);
    }

    [Fact]
    public void SafeClick_Allowed()
    {
        var d = Gate().CheckAction(StepAction.Click, "http://127.0.0.1:8080/", RiskLevel.Safe, "Execute Inquiry");
        Assert.Equal(PolicyVerdict.Allowed, d.Verdict);
        Assert.Equal(RiskLevel.Safe, d.EffectiveRisk);
    }

    [Fact]
    public void DesktopTarget_Allowlist_UsesProcessIdentity()
    {
        var gate = new PolicyGate(new PolicyConfig
        {
            AllowedHosts = ["FirstCore"],
            SurfaceKind = SurfaceKind.Desktop,
        });
        Assert.False(gate.CheckNavigation(@"C:\Bank\FirstCore.exe").IsBlocked);
        Assert.False(gate.CheckNavigation("app://FirstCore").IsBlocked);
        Assert.True(gate.CheckNavigation(@"C:\Bank\OtherTeller.exe").IsBlocked);
        Assert.False(gate.CheckAction(StepAction.Click, "app://FirstCore", RiskLevel.Safe, "OK").IsBlocked);
        Assert.True(gate.CheckAction(StepAction.Click, "app://OtherTeller", RiskLevel.Safe, "OK").IsBlocked);
    }
}

public sealed class ArtifactJsonTests
{
    [Fact]
    public void RoundTrips_AndUsesSnakeCase()
    {
        var artifact = new CapabilityArtifact
        {
            CapabilityId = "firstcore.fee_waiver",
            CapabilityVersion = 3,
            DisplayName = "Waive a fee",
            Surface = new SurfaceInfo { EntryUrl = "http://x/", Allowlist = ["x"] },
            Inputs = [new InputDef { Name = "account_id", Sensitivity = Sensitivity.Pii, RedactInLogs = true }],
            Steps =
            [
                new StepDef
                {
                    Id = "s1",
                    Action = StepAction.Click,
                    Frame = ["wrkFrm", "modFrm"],
                    Risk = RiskLevel.Irreversible,
                    Locator = new LocatorChain
                    {
                        Candidates =
                        [
                            new Locator { By = LocatorKind.Css, Value = "#ext-gen77" },
                            new Locator { By = LocatorKind.Text, Value = "Waive Fee", Within = "span.x-btn-text" },
                        ],
                    },
                    Assertions =
                    [
                        new AssertionDef
                        {
                            Classify = AssertionClass.BusinessOutcome,
                            When = new ConditionDef { By = ConditionKind.TextContains, Value = "Account is closed" },
                            Emit = new Dictionary<string, string> { ["outcome"] = "not_permitted" },
                            Terminal = true,
                        },
                    ],
                },
            ],
        };

        var json = CuaJson.Serialize(artifact);
        Assert.Contains("\"capability_id\"", json);
        Assert.Contains("\"business_outcome\"", json);
        Assert.Contains("\"text_contains\"", json);
        Assert.Contains("\"irreversible\"", json);

        var back = CuaJson.Deserialize<CapabilityArtifact>(json);
        Assert.Equal("firstcore.fee_waiver", back.CapabilityId);
        Assert.Equal(["wrkFrm", "modFrm"], back.Steps[0].Frame);
        Assert.Equal(AssertionClass.BusinessOutcome, back.Steps[0].Assertions[0].Classify);
        Assert.Equal("not_permitted", back.Steps[0].Assertions[0].Emit!["outcome"]);
        Assert.Equal(LocatorKind.Text, back.Steps[0].Locator!.Candidates[1].By);
        Assert.Equal(SurfaceKind.Web, back.Surface.Kind);
    }

    [Fact]
    public void LegacyWebKind_RoundTripsAsSnakeCase()
    {
        var artifact = new CapabilityArtifact
        {
            CapabilityId = "x",
            CapabilityVersion = 1,
            DisplayName = "x",
            Surface = new SurfaceInfo { Kind = SurfaceKind.LegacyWeb, EntryUrl = "http://x/", Allowlist = ["x"] },
            Steps = [new StepDef { Id = "s1", Action = StepAction.Click }],
        };
        var json = CuaJson.Serialize(artifact);
        Assert.Contains("\"legacy_web\"", json);
        Assert.Equal(SurfaceKind.LegacyWeb, CuaJson.Deserialize<CapabilityArtifact>(json).Surface.Kind);
    }
}

public sealed class ArtifactCompilerTests
{
    private static DiscoveryConfig Config => new()
    {
        Goal = "waive the fee on the account",
        EntryUrl = "http://127.0.0.1:8080/",
        CapabilityId = "firstcore.fee_waiver",
        Parameters = new Dictionary<string, string> { ["account_id"] = "12345" },
    };

    private static JsonElement Declaration(object o) => JsonSerializer.SerializeToElement(o);

    private static readonly object BaseDeclaration = new
    {
        display_name = "Waive fee",
        auth_steps = new[] { "s1", "s2" },
        outputs = new object[]
        {
            new { name = "outcome", type = "enum", enum_values = new[] { "waived", "not_found" } },
        },
        checkpoint = new
        {
            frame_path = new[] { "wrkFrm", "modFrm" },
            by = "css",
            value = "#ext-gen110",
            emit = new { outcome = "waived" },
            extracts = new object[] { new { name = "confirmation_id", regex = "RVSL-[A-F0-9]{8}" } },
        },
        guarded_steps = new object[]
        {
            new
            {
                after_step = "s4",
                business_outcomes = new object[]
                {
                    new
                    {
                        when = new { by = "text_contains", value = "No records found" },
                        emit = new { outcome = "not_found" },
                    },
                },
                recoverable = new object[]
                {
                    new { when = new { by = "text_contains", value = "HOST-0521" }, max_attempts = 4 },
                },
            },
        },
    };

    private static List<TraceStep> Trace() =>
    [
        new TraceStep
        {
            Id = "s1", Action = StepAction.Type, Frame = [],
            ModelLocator = new Locator { By = LocatorKind.Css, Value = "#user" },
            Meta = new Cua.Core.Surface.ElementMeta { Id = "ext-gen17_u", Name = "j_username", Tag = "input" },
            ValueRef = "credentials.username",
        },
        new TraceStep
        {
            Id = "s2", Action = StepAction.Click, Frame = [],
            ModelLocator = new Locator { By = LocatorKind.Css, Value = "#ext-gen21" },
            Meta = new Cua.Core.Surface.ElementMeta { Id = "ext-gen21", Tag = "span", Text = "Sign On", Classes = "x-btn-text", X = 100, Y = 20 },
        },
        new TraceStep
        {
            Id = "s3", Action = StepAction.Type, Frame = ["wrkFrm", "modFrm"],
            ModelLocator = new Locator { By = LocatorKind.Css, Value = "#ext-gen42_input" },
            Meta = new Cua.Core.Surface.ElementMeta { Id = "ext-gen42_input", Name = "acctNbr", Tag = "input" },
            ValueRef = "inputs.account_id",
        },
        new TraceStep
        {
            Id = "s4", Action = StepAction.Click, Frame = ["wrkFrm", "modFrm"],
            ModelLocator = new Locator { By = LocatorKind.Css, Value = "#ext-gen44" },
            Meta = new Cua.Core.Surface.ElementMeta { Id = "ext-gen44", Tag = "div", Text = "Execute Inquiry", Classes = "x-btn x-btn-noicon", X = 300, Y = 25 },
            WaitAfter = new WaitSpec
            {
                Locator = new Locator { By = LocatorKind.Css, Value = "#loading-spinner" },
                State = WaitState.Absent,
            },
        },
        new TraceStep
        {
            Id = "s5", Action = StepAction.Click, Frame = ["wrkFrm", "modFrm"],
            ModelLocator = new Locator { By = LocatorKind.Css, Value = "#ext-gen77" },
            Meta = new Cua.Core.Surface.ElementMeta { Id = "ext-gen77", Tag = "span", Text = "Waive Fee", Classes = "x-btn-text" },
            Risk = RiskLevel.Irreversible,
        },
    ];

    private static CapabilityArtifact Compile() => ArtifactCompiler.Compile(new ArtifactCompiler.Input
    {
        Config = Config,
        Trace = Trace(),
        Declaration = Declaration(BaseDeclaration),
        AllowedHosts = ["127.0.0.1:8080"],
        RunId = "discovery-test",
        NextVersion = 1,
    });

    [Fact]
    public void ParameterizedValues_BecomeValueRefs_NotLiterals()
    {
        var artifact = Compile();
        var typeStep = artifact.Steps.Single(s => s.Id == "s3");
        Assert.Equal("inputs.account_id", typeStep.ValueRef);
        Assert.Null(typeStep.Value);
        Assert.Single(artifact.Inputs);
        Assert.True(artifact.Inputs[0].RedactInLogs);
    }

    [Fact]
    public void ModernWeb_RanksTestIdAndAriaLabelAboveId_LegacyDoesNot()
    {
        var trace = new List<TraceStep>
        {
            new()
            {
                Id = "s1", Action = StepAction.Click,
                ModelLocator = new Locator { By = LocatorKind.Css, Value = "#waive" },
                Meta = new Cua.Core.Surface.ElementMeta
                {
                    Id = "waive", Tag = "button", Text = "Waive fee",
                    TestId = "waive-fee", AriaLabel = "Waive fee", Role = "button", X = 10, Y = 20,
                },
            },
        };

        ArtifactCompiler.Input Build(SurfaceKind kind) => new()
        {
            Config = Config with { SurfaceKind = kind },
            Trace = trace,
            Declaration = Declaration(BaseDeclaration),
            AllowedHosts = ["127.0.0.1:8080"],
            RunId = "r",
            NextVersion = 1,
        };

        var modern = ArtifactCompiler.Compile(Build(SurfaceKind.Web))
            .Steps.Single(s => s.Id == "s1").Locator!.Candidates;
        Assert.Equal("[data-testid='waive-fee']", modern[0].Value);
        Assert.Equal("[aria-label='Waive fee']", modern[1].Value);
        Assert.Equal("#waive", modern[2].Value);

        // legacy web has no published hooks to trust: id leads, text and
        // coordinates carry the fallback weight
        var legacy = ArtifactCompiler.Compile(Build(SurfaceKind.LegacyWeb))
            .Steps.Single(s => s.Id == "s1").Locator!.Candidates;
        Assert.Equal("#waive", legacy[0].Value);
        Assert.DoesNotContain(legacy, c => c.Value.Contains("data-testid"));
        Assert.Equal(LocatorKind.Coords, legacy[^1].By);
    }

    [Fact]
    public void LocatorChains_RankIdThenNameThenTextThenCoords()
    {
        var artifact = Compile();
        var click = artifact.Steps.Single(s => s.Id == "s4").Locator!;
        Assert.Equal("#ext-gen44", click.Candidates[0].Value);
        Assert.Equal(LocatorKind.Text, click.Candidates[1].By);
        Assert.Equal("Execute Inquiry", click.Candidates[1].Value);
        Assert.Equal(LocatorKind.Coords, click.Candidates[^1].By);

        var field = artifact.Steps.Single(s => s.Id == "s3").Locator!;
        Assert.Equal("#ext-gen42_input", field.Candidates[0].Value);
        Assert.Equal("input[name='acctNbr']", field.Candidates[1].Value);
    }

    [Fact]
    public void GuardedStep_GetsDeclaredAssertions_InEvaluationOrder_WithSynthesizedSuccess()
    {
        var artifact = Compile();
        var s4 = artifact.Steps.Single(s => s.Id == "s4");
        Assert.Equal(AssertionClass.Recoverable, s4.Assertions[0].Classify);
        Assert.Equal(4, s4.Assertions[0].Retry!.MaxAttempts);
        Assert.Equal(AssertionClass.BusinessOutcome, s4.Assertions[1].Classify);
        Assert.True(s4.Assertions[1].Terminal);
        // synthesized success: the next step's primary target (#ext-gen77) being present
        var success = s4.Assertions[^1];
        Assert.Equal(AssertionClass.Success, success.Classify);
        Assert.Equal("#ext-gen77", success.When.Value);
    }

    [Fact]
    public void WaitFolding_SurvivesCompilation()
    {
        var artifact = Compile();
        var s4 = artifact.Steps.Single(s => s.Id == "s4");
        Assert.Equal(WaitState.Absent, s4.WaitAfter!.State);
        Assert.Equal("#loading-spinner", s4.WaitAfter.Locator.Value);
    }

    [Fact]
    public void AuthSteps_MarkedAsAuthPhase_AndSessionGuardEmitted()
    {
        var artifact = Compile();
        Assert.Equal("auth", artifact.Steps.Single(s => s.Id == "s1").Phase);
        Assert.Equal("auth", artifact.Steps.Single(s => s.Id == "s2").Phase);
        Assert.Equal("main", artifact.Steps.Single(s => s.Id == "s4").Phase);

        var guard = Assert.Single(artifact.Guards);
        Assert.Equal(GuardResponse.RecoverAuth, guard.Response);
        Assert.Equal("#ext-gen17_u", guard.When.Value); // the recorded username field
    }

    [Fact]
    public void CheckpointStep_Appended_WithEmitAndExtracts()
    {
        var artifact = Compile();
        var checkpoint = artifact.Steps[^1];
        Assert.Equal(StepAction.Checkpoint, checkpoint.Action);
        Assert.Equal(["wrkFrm", "modFrm"], checkpoint.Frame);
        Assert.Equal("waived", checkpoint.Assertions[0].Emit!["outcome"]);
        Assert.Equal("confirmation_id", checkpoint.Extracts[0].Name);
        // extraction regexes double as log-redaction patterns
        Assert.Contains("RVSL-[A-F0-9]{8}", artifact.Redaction.LogPatterns);
    }

    [Fact]
    public void ArtifactIsDraft_AndCarriesProvenance()
    {
        var artifact = Compile();
        Assert.Equal("draft", artifact.Provenance.Approval);
        Assert.Equal("discovery-test", artifact.Provenance.DiscoveryRunId);
    }

    [Fact]
    public void StampsSurfaceKind_AndKindSpecificRobustnessNote()
    {
        var web = Compile();
        Assert.Equal(SurfaceKind.Web, web.Surface.Kind);

        var desktopCfg = Config with { SurfaceKind = SurfaceKind.Desktop, EntryUrl = @"C:\Apps\FirstCore.exe" };
        var desktop = ArtifactCompiler.Compile(new ArtifactCompiler.Input
        {
            Config = desktopCfg,
            Trace = Trace(),
            Declaration = Declaration(BaseDeclaration),
            AllowedHosts = ["FirstCore"],
            RunId = "discovery-test",
            NextVersion = 1,
        });
        Assert.Equal(SurfaceKind.Desktop, desktop.Surface.Kind);
        Assert.Contains("AutomationId", desktop.Steps.Single(s => s.Id == "s4").Locator!.Robustness);
    }
}

public sealed class ProbePrefixTests
{
    [Fact]
    public void RecordedFlow_IsContiguousPrefix_UpToFirstProbe()
    {
        var config = new DiscoveryConfig
        {
            Goal = "g", EntryUrl = "http://x/", CapabilityId = "cap",
            Parameters = new Dictionary<string, string>(),
        };
        List<TraceStep> trace =
        [
            new() { Id = "s1", Action = StepAction.Click, ModelLocator = new Locator { By = LocatorKind.Css, Value = "#a" },
                    Meta = new Cua.Core.Surface.ElementMeta { Id = "a", Tag = "div" } },
            new() { Id = "s2", Action = StepAction.Type, Probe = true, ModelLocator = new Locator { By = LocatorKind.Css, Value = "#f" },
                    Meta = new Cua.Core.Surface.ElementMeta { Id = "f", Tag = "input" }, Value = "77777" },
            // unflagged action AFTER a probe: must be treated as probe fallout, not flow
            new() { Id = "s3", Action = StepAction.Click, ModelLocator = new Locator { By = LocatorKind.Css, Value = "#a" },
                    Meta = new Cua.Core.Surface.ElementMeta { Id = "a", Tag = "div" } },
        ];
        var artifact = ArtifactCompiler.Compile(new ArtifactCompiler.Input
        {
            Config = config, Trace = trace,
            Declaration = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                display_name = "x",
                outputs = Array.Empty<object>(),
                checkpoint = new { frame_path = Array.Empty<string>(), by = "css", value = "#done" },
                guarded_steps = Array.Empty<object>(),
            }),
            AllowedHosts = ["x"], RunId = "r", NextVersion = 1,
        });
        Assert.Equal(["s1", "checkpoint"], artifact.Steps.Select(s => s.Id).ToArray());
    }
}

public sealed class ArtifactStoreBindingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cua-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static CapabilityArtifact Make(string id, int version, string? binding) => new()
    {
        CapabilityId = id,
        CapabilityVersion = version,
        DisplayName = id,
        Surface = new SurfaceInfo { EntryUrl = "http://x/", Allowlist = ["x"], AppBinding = binding },
        Steps = [],
    };

    [Fact]
    public void VersionLines_AreIndependent_PerAppBinding()
    {
        var store = new ArtifactStore(_dir);
        store.Save(Make("fee_waiver", store.NextVersion("fee_waiver", "firstcore-web"), "firstcore-web"));
        store.Save(Make("fee_waiver", store.NextVersion("fee_waiver", "firstcore-web"), "firstcore-web"));

        // a desktop recording of the same capability is a sibling, not v3
        Assert.Equal(1, store.NextVersion("fee_waiver", "firstcore-desktop"));
        Assert.Equal(3, store.NextVersion("fee_waiver", "firstcore-web"));
        // and artifacts with no binding (this project's existing files) keep their own line
        Assert.Equal(1, store.NextVersion("fee_waiver"));
    }

    [Fact]
    public void SiblingArtifacts_DoNotCollide_OnDisk()
    {
        var store = new ArtifactStore(_dir);
        var web = store.Save(Make("fee_waiver", 1, "firstcore-web"));
        var desktop = store.Save(Make("fee_waiver", 1, "firstcore-desktop"));
        Assert.NotEqual(web, desktop);
        Assert.Equal(2, store.List().Count);
    }
}
