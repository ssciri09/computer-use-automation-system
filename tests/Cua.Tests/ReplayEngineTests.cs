using Cua.Core.Artifacts;
using Cua.Core.Contracts;
using Cua.Core.Evidence;
using Cua.Core.Hitl;
using Cua.Core.Redaction;
using Cua.Core.Surface;
using Cua.Engine.Replay;
using Xunit;

namespace Cua.Tests;

public sealed class ReplayEngineTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "cua-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static CapabilityArtifact Artifact(IReadOnlyList<StepDef> steps, IReadOnlyList<GuardDef>? guards = null) => new()
    {
        CapabilityId = "test.capability",
        CapabilityVersion = 1,
        DisplayName = "test",
        Surface = new SurfaceInfo { EntryUrl = "http://127.0.0.1:8080/", Allowlist = ["127.0.0.1:8080"] },
        Steps = steps,
        Guards = guards ?? [],
        Provenance = new ProvenanceInfo { Approval = "approved" },
    };

    private (ReplayEngine Engine, RunLogger Log) Engine(FakeSurface surface, IOperatorChannel? channel = null)
    {
        var redactor = Redactor.CreateDefault();
        var log = new RunLogger(_tmp, "test", redactor) { Quiet = true };
        return (new ReplayEngine(surface,
            channel ?? new FakeOperatorChannel(_ => new InterventionResolution { Resolved = false }),
            log, redactor), log);
    }

    private static StepDef ClickStep(string id, string css, IReadOnlyList<AssertionDef> assertions, int timeoutMs = 2000) => new()
    {
        Id = id,
        Action = StepAction.Click,
        Locator = new LocatorChain { Candidates = [new Locator { By = LocatorKind.Css, Value = css }] },
        Assertions = assertions,
        TimeoutMs = timeoutMs,
    };

    [Fact]
    public async Task RecoverableCondition_RetriesAndSucceeds()
    {
        var surface = new FakeSurface();
        surface.Present.Add(FakeSurface.Key([], "#go"));
        surface.ClickHandlers["#go"] = (s, n) =>
        {
            s.Present.Remove(FakeSurface.Key([], ".busy"));
            s.Present.Remove(FakeSurface.Key([], ".result"));
            // transient: first two attempts hit the busy banner, third succeeds
            s.Present.Add(FakeSurface.Key([], n < 3 ? ".busy" : ".result"));
        };

        var artifact = Artifact([
            ClickStep("s1", "#go",
            [
                new AssertionDef
                {
                    Classify = AssertionClass.Recoverable,
                    When = new ConditionDef { By = ConditionKind.Css, Value = ".busy" },
                    Retry = new RetrySpec { MaxAttempts = 3, BackoffMs = [10, 10, 10] },
                },
                new AssertionDef
                {
                    Classify = AssertionClass.Success,
                    When = new ConditionDef { By = ConditionKind.Css, Value = ".result" },
                },
            ]),
        ]);

        var (engine, log) = Engine(surface);
        using (log)
        {
            var result = await engine.RunAsync(artifact, new Dictionary<string, string>(), new ReplayOptions(), CancellationToken.None);
            Assert.Equal(RunStatus.Success, result.Status);
            Assert.Equal(3, surface.ClickCounts["#go"]);
        }
    }

    [Fact]
    public async Task RecoverableCondition_Persisting_IsHardFailure_NotSilentLoop()
    {
        var surface = new FakeSurface();
        surface.Present.Add(FakeSurface.Key([], "#go"));
        surface.ClickHandlers["#go"] = (s, _) => s.Present.Add(FakeSurface.Key([], ".busy"));

        var artifact = Artifact([
            ClickStep("s1", "#go",
            [
                new AssertionDef
                {
                    Classify = AssertionClass.Recoverable,
                    When = new ConditionDef { By = ConditionKind.Css, Value = ".busy" },
                    Retry = new RetrySpec { MaxAttempts = 2, BackoffMs = [10] },
                },
            ]),
        ]);

        var (engine, log) = Engine(surface);
        using (log)
        {
            var result = await engine.RunAsync(artifact, new Dictionary<string, string>(), new ReplayOptions(), CancellationToken.None);
            Assert.Equal(RunStatus.HardFailure, result.Status);
            Assert.Equal("s1", result.Failure!.StepId);
            Assert.Contains("transient condition persisted", result.Failure.Observed);
        }
    }

    [Fact]
    public async Task BusinessOutcome_IsTerminal_AndCarriesOutputs_NotAFailure()
    {
        var surface = new FakeSurface();
        surface.Present.Add(FakeSurface.Key([], "#waive"));
        surface.ClickHandlers["#waive"] = (s, _) => s.FrameTexts[""] = "Error: Account is closed. Fee waiver not permitted.";

        var artifact = Artifact([
            ClickStep("s1", "#waive",
            [
                new AssertionDef
                {
                    Classify = AssertionClass.BusinessOutcome,
                    When = new ConditionDef { By = ConditionKind.TextContains, Value = "Account is closed" },
                    Emit = new Dictionary<string, string> { ["outcome"] = "not_permitted" },
                    Terminal = true,
                },
                new AssertionDef
                {
                    Classify = AssertionClass.Success,
                    When = new ConditionDef { By = ConditionKind.Css, Value = ".conf" },
                },
            ]),
            new StepDef { Id = "never", Action = StepAction.Checkpoint },
        ]);

        var (engine, log) = Engine(surface);
        using (log)
        {
            var result = await engine.RunAsync(artifact, new Dictionary<string, string>(), new ReplayOptions(), CancellationToken.None);
            Assert.Equal(RunStatus.BusinessOutcome, result.Status);
            Assert.Equal("not_permitted", result.Outcome);
            Assert.Null(result.Failure);
        }
    }

    [Fact]
    public async Task Escalation_HumanResolves_ResumesAtCheckpoint_AndRecordsIntervention()
    {
        var surface = new FakeSurface();
        surface.Present.Add(FakeSurface.Key([], "#commit"));
        surface.ClickHandlers["#commit"] = (s, _) => s.Present.Add(FakeSurface.Key([], "#secOvl"));
        surface.PendingHumanActions.Add(new HumanAction { Kind = "click", Id = "ext-gen101", At = DateTimeOffset.UtcNow });

        var channel = new FakeOperatorChannel(
            _ => new InterventionResolution { Resolved = true, OperatorNotes = "entered override PIN" },
            beforeResolve: () =>
            {
                surface.Present.Remove(FakeSurface.Key([], "#secOvl"));
                surface.Present.Add(FakeSurface.Key([], "#conf"));
                surface.FrameTexts[""] = "Fee reversed. Confirmation RVSL-ABCD1234";
            });

        var artifact = Artifact([
            ClickStep("s1", "#commit",
            [
                new AssertionDef
                {
                    Classify = AssertionClass.Escalate,
                    When = new ConditionDef { By = ConditionKind.Css, Value = "#secOvl", Frame = [] },
                    Escalate = new EscalationSpec { Reason = "security interception", ResumeAt = "checkpoint" },
                },
                new AssertionDef
                {
                    Classify = AssertionClass.Success,
                    When = new ConditionDef { By = ConditionKind.Css, Value = "#conf" },
                },
            ]),
            new StepDef
            {
                Id = "checkpoint",
                Action = StepAction.Checkpoint,
                Assertions =
                [
                    new AssertionDef
                    {
                        Classify = AssertionClass.Success,
                        When = new ConditionDef { By = ConditionKind.Css, Value = "#conf" },
                        Emit = new Dictionary<string, string> { ["outcome"] = "waived" },
                    },
                ],
                Extracts = [new ExtractDef { Name = "confirmation_id", Regex = @"RVSL-[A-F0-9]{8}" }],
                TimeoutMs = 2000,
            },
        ]);

        var (engine, log) = Engine(surface, channel);
        using (log)
        {
            var result = await engine.RunAsync(artifact, new Dictionary<string, string>(), new ReplayOptions(), CancellationToken.None);
            Assert.Equal(RunStatus.Success, result.Status);
            Assert.True(result.HumanAssisted);
            Assert.Equal("waived", result.Outcome);
            Assert.Equal("RVSL-ABCD1234", result.Outputs["confirmation_id"]);
            Assert.Equal("resolved", result.Intervention!.Resolution);
            Assert.Equal("entered override PIN", result.Intervention.OperatorNotes);
            Assert.Single(channel.Requests);
            Assert.Equal("s1", channel.Requests[0].StepId);
        }
    }

    [Fact]
    public async Task Escalation_Unattended_QueuesAndReportsPending()
    {
        var surface = new FakeSurface();
        surface.Present.Add(FakeSurface.Key([], "#commit"));
        surface.ClickHandlers["#commit"] = (s, _) => s.Present.Add(FakeSurface.Key([], "#secOvl"));

        var queueDir = Path.Combine(_tmp, "queue");
        var artifact = Artifact([
            ClickStep("s1", "#commit",
            [
                new AssertionDef
                {
                    Classify = AssertionClass.Escalate,
                    When = new ConditionDef { By = ConditionKind.Css, Value = "#secOvl", Frame = [] },
                    Escalate = new EscalationSpec { Reason = "security interception" },
                },
            ]),
        ]);

        var redactor = Redactor.CreateDefault();
        using var log = new RunLogger(_tmp, "test", redactor) { Quiet = true };
        var engine = new ReplayEngine(surface, new QueueOperatorChannel(queueDir), log, redactor);
        var result = await engine.RunAsync(artifact, new Dictionary<string, string>(), new ReplayOptions(), CancellationToken.None);

        Assert.Equal(RunStatus.EscalationPending, result.Status);
        Assert.Equal("pending", result.Intervention!.Resolution);
        Assert.Single(Directory.GetFiles(queueDir));
    }

    [Fact]
    public async Task NothingMatches_IsHardFailure_WithExpectedVsObserved()
    {
        var surface = new FakeSurface();
        surface.Present.Add(FakeSurface.Key([], "#go"));

        var artifact = Artifact([
            ClickStep("s1", "#go",
            [
                new AssertionDef
                {
                    Classify = AssertionClass.Success,
                    When = new ConditionDef { By = ConditionKind.Css, Value = ".result" },
                },
            ], timeoutMs: 600),
        ]);

        var (engine, log) = Engine(surface);
        using (log)
        {
            var result = await engine.RunAsync(artifact, new Dictionary<string, string>(), new ReplayOptions(), CancellationToken.None);
            Assert.Equal(RunStatus.HardFailure, result.Status);
            Assert.Equal("s1", result.Failure!.StepId);
            Assert.Contains("success(css:.result)", result.Failure.Expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SessionExpiryGuard_RerunsAuthPhase_ThenResumes()
    {
        var surface = new FakeSurface();
        surface.Present.Add(FakeSurface.Key([], "#signon"));
        surface.Present.Add(FakeSurface.Key([], "#main"));
        surface.ClickHandlers["#signon"] = (s, n) =>
        {
            if (n == 1) s.Present.Add(FakeSurface.Key([], "#login")); // session drops right after initial sign-on
            else s.Present.Remove(FakeSurface.Key([], "#login"));     // guard-driven re-auth restores it
        };
        surface.ClickHandlers["#main"] = (s, _) => s.Present.Add(FakeSurface.Key([], "#done"));

        var artifact = Artifact(
            steps:
            [
                new StepDef
                {
                    Id = "s1", Action = StepAction.Click, Phase = "auth",
                    Locator = new LocatorChain { Candidates = [new Locator { By = LocatorKind.Css, Value = "#signon" }] },
                },
                ClickStep("s2", "#main",
                [
                    new AssertionDef
                    {
                        Classify = AssertionClass.Success,
                        When = new ConditionDef { By = ConditionKind.Css, Value = "#done" },
                    },
                ]),
            ],
            guards:
            [
                new GuardDef
                {
                    Id = "session-expired",
                    When = new ConditionDef { By = ConditionKind.Css, Value = "#login", Frame = [] },
                    Response = GuardResponse.RecoverAuth,
                },
            ]);

        var (engine, log) = Engine(surface);
        using (log)
        {
            var result = await engine.RunAsync(artifact, new Dictionary<string, string>(), new ReplayOptions(), CancellationToken.None);
            Assert.Equal(RunStatus.Success, result.Status);
            // auth step ran twice: once in sequence, once via guard recovery
            Assert.Equal(2, surface.ClickCounts["#signon"]);
        }
    }

    [Fact]
    public async Task DraftArtifact_RefusedWithoutAllowDraft()
    {
        var surface = new FakeSurface();
        var artifact = Artifact([new StepDef { Id = "s1", Action = StepAction.Checkpoint }]) with
        {
            Provenance = new ProvenanceInfo { Approval = "draft" },
        };
        var (engine, log) = Engine(surface);
        using (log)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.RunAsync(artifact, new Dictionary<string, string>(), new ReplayOptions(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task IrreversibleStep_WithoutAckRisk_RaisesConfirmationIntervention()
    {
        var surface = new FakeSurface();
        surface.Present.Add(FakeSurface.Key([], "#waive"));
        surface.ClickHandlers["#waive"] = (s, _) => s.Present.Add(FakeSurface.Key([], "#conf"));

        var channel = new FakeOperatorChannel(_ => new InterventionResolution { Resolved = true, OperatorNotes = "authorized" });
        var artifact = Artifact([
            new StepDef
            {
                Id = "s1", Action = StepAction.Click, Risk = RiskLevel.Irreversible,
                Locator = new LocatorChain { Candidates = [new Locator { By = LocatorKind.Css, Value = "#waive" }] },
                Assertions =
                [
                    new AssertionDef
                    {
                        Classify = AssertionClass.Success,
                        When = new ConditionDef { By = ConditionKind.Css, Value = "#conf" },
                    },
                ],
                TimeoutMs = 2000,
            },
        ]);

        var (engine, log) = Engine(surface, channel);
        using (log)
        {
            var result = await engine.RunAsync(artifact, new Dictionary<string, string>(),
                new ReplayOptions { AckRisk = false }, CancellationToken.None);
            Assert.Equal(RunStatus.Success, result.Status);
            Assert.Single(channel.Requests);
            Assert.Contains("irreversible", channel.Requests[0].Reason);
        }
    }

    [Fact]
    public async Task InputValidation_PatternMismatch_Throws()
    {
        var surface = new FakeSurface();
        var artifact = Artifact([new StepDef { Id = "s1", Action = StepAction.Checkpoint }]) with
        {
            Inputs = [new InputDef { Name = "account_id", Pattern = "^[0-9]{5}$" }],
        };
        var (engine, log) = Engine(surface);
        using (log)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => engine.RunAsync(artifact,
                new Dictionary<string, string> { ["account_id"] = "abc" },
                new ReplayOptions(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task LocatorFallback_UsedWhenPrimaryMissing()
    {
        var surface = new FakeSurface();
        surface.Present.Add(FakeSurface.Key([], "input[name='acctNbr']")); // only the fallback exists
        surface.ClickHandlers["input[name='acctNbr']"] = (s, _) => s.Present.Add(FakeSurface.Key([], "#ok"));

        var artifact = Artifact([
            new StepDef
            {
                Id = "s1", Action = StepAction.Click,
                Locator = new LocatorChain
                {
                    Candidates =
                    [
                        new Locator { By = LocatorKind.Css, Value = "#ext-gen42_input" },
                        new Locator { By = LocatorKind.Css, Value = "input[name='acctNbr']" },
                    ],
                },
                Assertions =
                [
                    new AssertionDef
                    {
                        Classify = AssertionClass.Success,
                        When = new ConditionDef { By = ConditionKind.Css, Value = "#ok" },
                    },
                ],
                TimeoutMs = 2000,
            },
        ]);

        var (engine, log) = Engine(surface);
        using (log)
        {
            var result = await engine.RunAsync(artifact, new Dictionary<string, string>(), new ReplayOptions(), CancellationToken.None);
            Assert.Equal(RunStatus.Success, result.Status);
        }
    }
}
