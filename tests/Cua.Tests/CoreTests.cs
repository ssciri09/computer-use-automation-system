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
    public void SafeClick_Allowed()
    {
        var d = Gate().CheckAction(StepAction.Click, "http://127.0.0.1:8080/", RiskLevel.Safe, "Execute Inquiry");
        Assert.Equal(PolicyVerdict.Allowed, d.Verdict);
        Assert.Equal(RiskLevel.Safe, d.EffectiveRisk);
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
}
