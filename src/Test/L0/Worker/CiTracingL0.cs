using System;
using System.Diagnostics;
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
        [InlineData(TaskResult.Succeeded, "succeeded", ActivityStatusCode.Ok)]
        [InlineData(TaskResult.Skipped, "skipped", ActivityStatusCode.Ok)]
        [InlineData(TaskResult.Canceled, "canceled", ActivityStatusCode.Ok)]
        [InlineData(TaskResult.Failed, "failed", ActivityStatusCode.Error)]
        public void SetResult_MapsResultToTagAndStatus(TaskResult result, string expectedTag, ActivityStatusCode expectedStatus)
        {
            using var traced = new TracedActivity();

            CiTracing.SetResult(traced.Activity, "github.step.result", result);

            Assert.Equal(expectedTag, traced.Activity.GetTagItem("github.step.result"));
            Assert.Equal(expectedStatus, traced.Activity.Status);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void SetResult_DefaultsToSucceeded_WhenResultIsNull()
        {
            using var traced = new TracedActivity();

            CiTracing.SetResult(traced.Activity, "github.step.result", null);

            Assert.Equal("succeeded", traced.Activity.GetTagItem("github.step.result"));
            Assert.Equal(ActivityStatusCode.Ok, traced.Activity.Status);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void SetResult_IsNoOp_WhenActivityIsNull()
        {
            // Mirrors the disabled-tracing path: callers pass the null activity straight through.
            CiTracing.SetResult(null, "github.step.result", TaskResult.Failed);
        }

        // Attaches a sampling listener to CiTracing.Source so StartActivity returns a live
        // Activity, and removes the listener on dispose so it does not leak into other tests.
        private sealed class TracedActivity : IDisposable
        {
            private readonly ActivityListener _listener;

            public Activity Activity { get; }

            public TracedActivity()
            {
                _listener = new ActivityListener
                {
                    ShouldListenTo = source => source == CiTracing.Source,
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
                };
                ActivitySource.AddActivityListener(_listener);

                Activity = CiTracing.Source.StartActivity("test");
                Assert.NotNull(Activity);
            }

            public void Dispose()
            {
                Activity?.Dispose();
                _listener.Dispose();
            }
        }
    }
}
