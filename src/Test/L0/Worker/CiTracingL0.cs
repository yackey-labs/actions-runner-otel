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
    }
}
