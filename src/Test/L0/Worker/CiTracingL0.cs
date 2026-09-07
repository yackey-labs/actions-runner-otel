using System;
using System.Collections.Generic;
using System.Diagnostics;
using GitHub.DistributedTask.Pipelines.ContextData;
using GitHub.DistributedTask.WebApi;
using GitHub.Runner.Worker;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker
{
    public sealed class CiTracingL0
    {
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryCreateTracerProvider_ReturnsNull_WhenEndpointNotConfigured()
        {
            var original = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            try
            {
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);

                using var provider = CiTracing.TryCreateTracerProvider();

                Assert.Null(provider);
            }
            finally
            {
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", original);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryCreateTracerProvider_BuildsProvider_WhenEndpointConfigured()
        {
            // Exercises the real OpenTelemetry assembly load and provider construction.
            // Guards against transitive-dependency mismatches (e.g. a DiagnosticSource
            // version the self-contained runtime does not ship) that only surface when the
            // provider is actually built, not at compile time.
            var original = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            try
            {
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4318");

                using var provider = CiTracing.TryCreateTracerProvider();

                Assert.NotNull(provider);
            }
            finally
            {
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", original);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void StartActivity_ReturnsNull_WhenNoListener()
        {
            // With no provider built (and therefore no listener on the source), starting an
            // activity must be a no-op so the runner pays no cost when tracing is disabled.
            using var activity = CiTracing.Source.StartActivity("step");

            Assert.Null(activity);
        }

        [Theory]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        // Tag values follow the OpenTelemetry CICD result vocabulary; only failure/error
        // (a failed step or an abandoned/worker-killed run) sets the span status to Error.
        [InlineData(TaskResult.Succeeded, "success", ActivityStatusCode.Unset)]
        [InlineData(TaskResult.SucceededWithIssues, "success", ActivityStatusCode.Unset)]
        [InlineData(TaskResult.Skipped, "skip", ActivityStatusCode.Unset)]
        [InlineData(TaskResult.Canceled, "cancellation", ActivityStatusCode.Unset)]
        [InlineData(TaskResult.Failed, "failure", ActivityStatusCode.Error)]
        [InlineData(TaskResult.Abandoned, "error", ActivityStatusCode.Error)]
        public void SetResult_MapsResultToTagAndStatus(TaskResult result, string expectedTag, ActivityStatusCode expectedStatus)
        {
            using var traced = new TracedActivity();

            CiTracing.SetResult(traced.Activity, "cicd.pipeline.task.run.result", result);

            Assert.Equal(expectedTag, traced.Activity.GetTagItem("cicd.pipeline.task.run.result"));
            Assert.Equal(expectedStatus, traced.Activity.Status);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void SetResult_DefaultsToSucceeded_WhenResultIsNull()
        {
            using var traced = new TracedActivity();

            CiTracing.SetResult(traced.Activity, "cicd.pipeline.task.run.result", null);

            Assert.Equal("success", traced.Activity.GetTagItem("cicd.pipeline.task.run.result"));
            Assert.Equal(ActivityStatusCode.Unset, traced.Activity.Status);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void SetResult_IsNoOp_WhenActivityIsNull()
        {
            // Mirrors the disabled-tracing path: callers pass the null activity straight through.
            CiTracing.SetResult(null, "github.step.result", TaskResult.Failed);
        }

        // ── TryExtractRemoteParent ──────────────────────────────────────────────

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsDefault_WhenContextDataIsNull()
        {
            var result = CiTracing.TryExtractRemoteParent(null);
            Assert.Equal(default, result);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsDefault_WhenInputsKeyAbsent()
        {
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase);
            var result = CiTracing.TryExtractRemoteParent(contextData);
            Assert.Equal(default, result);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsDefault_WhenTraceparentInputAbsent()
        {
            var inputs = new DictionaryContextData();
            inputs.Add("some_other_input", new StringContextData("value"));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputs"] = inputs,
            };

            var result = CiTracing.TryExtractRemoteParent(contextData);
            Assert.Equal(default, result);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsDefault_WhenTraceparentIsEmpty()
        {
            var inputs = new DictionaryContextData();
            inputs.Add("traceparent", new StringContextData(string.Empty));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputs"] = inputs,
            };

            var result = CiTracing.TryExtractRemoteParent(contextData);
            Assert.Equal(default, result);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsDefault_WhenTraceparentIsInvalid()
        {
            var inputs = new DictionaryContextData();
            inputs.Add("traceparent", new StringContextData("not-a-valid-traceparent"));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputs"] = inputs,
            };

            var result = CiTracing.TryExtractRemoteParent(contextData);
            Assert.Equal(default, result);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsParsedContext_WhenValidTraceparent()
        {
            const string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
            var inputs = new DictionaryContextData();
            inputs.Add("traceparent", new StringContextData(traceparent));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputs"] = inputs,
            };

            var result = CiTracing.TryExtractRemoteParent(contextData);

            Assert.NotEqual(default, result);
            Assert.Equal(ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736"), result.TraceId);
            Assert.Equal(ActivitySpanId.CreateFromString("00f067aa0ba902b7"), result.SpanId);
            Assert.True(result.IsRemote);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsParsedContext_WhenInputsIsCaseSensitiveDict()
        {
            // workflow_dispatch sends inputs as CaseSensitiveDictionaryContextData, not DictionaryContextData
            const string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
            var inputs = new CaseSensitiveDictionaryContextData();
            inputs.Add("traceparent", new StringContextData(traceparent));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputs"] = inputs,
            };

            var result = CiTracing.TryExtractRemoteParent(contextData);

            Assert.NotEqual(default, result);
            Assert.Equal(ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736"), result.TraceId);
            Assert.Equal(ActivitySpanId.CreateFromString("00f067aa0ba902b7"), result.SpanId);
            Assert.True(result.IsRemote);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_PreservesTracestate_WhenPresent()
        {
            const string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
            const string tracestate = "vendor1=abc,vendor2=def";
            var inputs = new DictionaryContextData();
            inputs.Add("traceparent", new StringContextData(traceparent));
            inputs.Add("tracestate", new StringContextData(tracestate));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputs"] = inputs,
            };

            var result = CiTracing.TryExtractRemoteParent(contextData);

            Assert.NotEqual(default, result);
            Assert.Equal(tracestate, result.TraceState);
            Assert.True(result.IsRemote);
        }

        // A started, recording Activity for exercising SetResult. Constructor-created
        // activities always store tags and status, so this needs no ActivitySource listener —
        // keeping the test deterministic regardless of process-global listener state.
        private sealed class TracedActivity : IDisposable
        {
            public Activity Activity { get; }

            public TracedActivity()
            {
                Activity = new Activity("test").Start();
            }

            public void Dispose()
            {
                Activity.Dispose();
            }
        }

        // --- needs.<job>.outputs.traceparent (same-run job chaining) ---

        private static DictionaryContextData NeedsJob(string traceparent)
        {
            var outputs = new DictionaryContextData();
            if (traceparent != null)
            {
                outputs.Add("traceparent", new StringContextData(traceparent));
            }
            var job = new DictionaryContextData();
            job.Add("outputs", outputs);
            job.Add("result", new StringContextData("success"));
            return job;
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsParsedContext_FromNeedsOutputs()
        {
            const string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
            var needs = new DictionaryContextData();
            needs.Add("docker", NeedsJob(traceparent));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["needs"] = needs,
            };

            var result = CiTracing.TryExtractRemoteParent(contextData);

            Assert.NotEqual(default, result);
            Assert.Equal(ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736"), result.TraceId);
            Assert.Equal(ActivitySpanId.CreateFromString("00f067aa0ba902b7"), result.SpanId);
            Assert.True(result.IsRemote);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_InputsTakePrecedence_OverNeeds()
        {
            const string fromInputs = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-aaaaaaaaaaaaaaaa-01";
            const string fromNeeds = "00-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-bbbbbbbbbbbbbbbb-01";

            var inputs = new DictionaryContextData();
            inputs.Add("traceparent", new StringContextData(fromInputs));
            var needs = new DictionaryContextData();
            needs.Add("docker", NeedsJob(fromNeeds));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputs"] = inputs,
                ["needs"] = needs,
            };

            var result = CiTracing.TryExtractRemoteParent(contextData);

            Assert.Equal(ActivityTraceId.CreateFromString("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), result.TraceId);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_NeedsJobsVisitedInSortedOrder_FirstValidWins()
        {
            // "alpha" sorts before "beta": alpha has no traceparent, beta's is used.
            // A third job with an invalid value sorts first and must be skipped.
            const string valid = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
            var needs = new DictionaryContextData();
            needs.Add("beta", NeedsJob(valid));
            needs.Add("alpha", NeedsJob(null));
            needs.Add("aaa-invalid", NeedsJob("not-a-valid-traceparent"));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["needs"] = needs,
            };

            var result = CiTracing.TryExtractRemoteParent(contextData);

            Assert.Equal(ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736"), result.TraceId);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsDefault_WhenNeedsHaveNoTraceparent()
        {
            var needs = new DictionaryContextData();
            needs.Add("web", NeedsJob(null));
            var contextData = new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["needs"] = needs,
            };

            Assert.Equal(default, CiTracing.TryExtractRemoteParent(contextData));
        }

        // ── FromWorkflowRun (derived workflow-run context) ──────────────────────

        private static Dictionary<string, PipelineContextData> GitHubContext(
            string repository = "yackey-labs/demo",
            string runId = "12345",
            string runAttempt = "1")
        {
            var github = new DictionaryContextData();
            if (repository != null) { github.Add("repository", new StringContextData(repository)); }
            if (runId != null) { github.Add("run_id", new StringContextData(runId)); }
            if (runAttempt != null) { github.Add("run_attempt", new StringContextData(runAttempt)); }
            return new Dictionary<string, PipelineContextData>(StringComparer.OrdinalIgnoreCase)
            {
                ["github"] = github,
            };
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_DerivesContext_FromWorkflowRun()
        {
            var result = CiTracing.TryExtractRemoteParent(GitHubContext());

            Assert.NotEqual(default, result);
            Assert.True(result.IsRemote);
            Assert.NotEqual(default, result.TraceId);
            Assert.NotEqual(default, result.SpanId);
            // The span id must not simply be a prefix of the trace id.
            Assert.False(result.TraceId.ToHexString().StartsWith(result.SpanId.ToHexString(), StringComparison.Ordinal));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_DerivationIsDeterministic_AcrossJobsOfSameRun()
        {
            // Two jobs of the same run derive independently, with no coordination.
            var a = CiTracing.TryExtractRemoteParent(GitHubContext());
            var b = CiTracing.TryExtractRemoteParent(GitHubContext());

            Assert.Equal(a.TraceId, b.TraceId);
            Assert.Equal(a.SpanId, b.SpanId);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_DerivationDiffers_PerRunPerAttemptPerRepo()
        {
            var baseline = CiTracing.TryExtractRemoteParent(GitHubContext());

            Assert.NotEqual(baseline.TraceId, CiTracing.TryExtractRemoteParent(GitHubContext(runId: "99999")).TraceId);
            Assert.NotEqual(baseline.TraceId, CiTracing.TryExtractRemoteParent(GitHubContext(runAttempt: "2")).TraceId);
            Assert.NotEqual(baseline.TraceId, CiTracing.TryExtractRemoteParent(GitHubContext(repository: "yackey-labs/other")).TraceId);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_TreatsMissingRunAttemptAsFirstAttempt()
        {
            // Older servers omit run_attempt; the run should still group rather than fall back
            // to every job starting its own trace.
            var absent = CiTracing.TryExtractRemoteParent(GitHubContext(runAttempt: null));
            var explicitOne = CiTracing.TryExtractRemoteParent(GitHubContext(runAttempt: "1"));

            Assert.NotEqual(default, absent);
            Assert.Equal(explicitOne.TraceId, absent.TraceId);
            Assert.Equal(explicitOne.SpanId, absent.SpanId);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_ReturnsDefault_WhenRunIdentityIncomplete()
        {
            Assert.Equal(default, CiTracing.TryExtractRemoteParent(GitHubContext(repository: null)));
            Assert.Equal(default, CiTracing.TryExtractRemoteParent(GitHubContext(runId: null)));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_PrefersDispatchInputs_OverDerivedWorkflowRun()
        {
            var contextData = GitHubContext();
            var inputs = new DictionaryContextData();
            inputs.Add("traceparent", new StringContextData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"));
            contextData["inputs"] = inputs;

            var result = CiTracing.TryExtractRemoteParent(contextData);

            Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", result.TraceId.ToHexString());
            Assert.Equal("00f067aa0ba902b7", result.SpanId.ToHexString());
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryExtractRemoteParent_PrefersNeedsOutputs_OverDerivedWorkflowRun()
        {
            var contextData = GitHubContext();
            var outputs = new DictionaryContextData();
            outputs.Add("traceparent", new StringContextData("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"));
            var job = new DictionaryContextData();
            job.Add("outputs", outputs);
            var needs = new DictionaryContextData();
            needs.Add("build", job);
            contextData["needs"] = needs;

            var result = CiTracing.TryExtractRemoteParent(contextData);

            Assert.Equal("0af7651916cd43dd8448eb211c80319c", result.TraceId.ToHexString());
            Assert.Equal("b7ad6b7169203331", result.SpanId.ToHexString());
        }

        // ── OTLP auth headers ───────────────────────────────────────────────────

        private static void WithOtlpEnv(string headers, string expose, Action body)
        {
            var e0 = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            var h0 = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
            var x0 = Environment.GetEnvironmentVariable("RUNNER_OTEL_EXPOSE_HEADERS_TO_STEPS");
            try
            {
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4318");
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", headers);
                Environment.SetEnvironmentVariable("RUNNER_OTEL_EXPOSE_HEADERS_TO_STEPS", expose);
                body();
            }
            finally
            {
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", e0);
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", h0);
                Environment.SetEnvironmentVariable("RUNNER_OTEL_EXPOSE_HEADERS_TO_STEPS", x0);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryCreateTracerProvider_RemovesOtlpHeadersFromEnvironment_ByDefault()
        {
            // The credential must not reach the environment that steps inherit.
            WithOtlpEnv("x-honeycomb-team=secret-key", null, () =>
            {
                using var provider = CiTracing.TryCreateTracerProvider();

                Assert.NotNull(provider);
                Assert.Null(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS"));
            });
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryCreateTracerProvider_KeepsOtlpHeaders_WhenExposureExplicitlyRequested()
        {
            foreach (var optIn in new[] { "true", "TRUE", "1" })
            {
                WithOtlpEnv("x-honeycomb-team=secret-key", optIn, () =>
                {
                    using var provider = CiTracing.TryCreateTracerProvider();

                    Assert.NotNull(provider);
                    Assert.Equal("x-honeycomb-team=secret-key", Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS"));
                });
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryCreateTracerProvider_LeavesEndpointAndProtocolForSteps()
        {
            // Steps must still be able to export and nest -- only the credential is withheld.
            WithOtlpEnv("x-honeycomb-team=secret-key", null, () =>
            {
                using var provider = CiTracing.TryCreateTracerProvider();

                Assert.NotNull(provider);
                Assert.Equal("http://localhost:4318", Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
            });
        }
    }
}
