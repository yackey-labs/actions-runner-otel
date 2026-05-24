using System;
using System.Collections.Generic;
using System.Diagnostics;
using GitHub.DistributedTask.Pipelines.ContextData;
using GitHub.DistributedTask.WebApi;
using GitHub.Runner.Sdk;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GitHub.Runner.Worker
{
    /// <summary>
    /// Optional OpenTelemetry tracing of job and step execution.
    ///
    /// Tracing is opt-in. <see cref="TryCreateTracerProvider"/> returns <see langword="null"/>
    /// unless an OTLP endpoint is configured through the standard
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable. When no provider is created
    /// there is no listener on <see cref="Source"/>, so every <c>StartActivity</c> call
    /// returns <see langword="null"/> at negligible cost and the runner behaves exactly as
    /// before.
    ///
    /// Exporter and resource configuration (protocol, headers, timeout, service name,
    /// resource attributes) is read from the standard <c>OTEL_*</c> environment variables by
    /// the OpenTelemetry SDK. This type intentionally has no knowledge of the host
    /// environment (for example Kubernetes); deployments inject identity such as
    /// <c>k8s.pod.name</c> via <c>OTEL_RESOURCE_ATTRIBUTES</c>.
    /// </summary>
    public static class CiTracing
    {
        /// <summary>
        /// Name of the <see cref="ActivitySource"/> that emits job and step spans. Also used
        /// as the default <c>service.name</c> when one is not supplied via
        /// <c>OTEL_SERVICE_NAME</c>.
        /// </summary>
        public const string SourceName = "github.actions.runner";

        /// <summary>
        /// Environment variable that gates tracing. The OpenTelemetry SDK reads it natively;
        /// the runner only checks for its presence to decide whether to build a provider.
        /// </summary>
        private const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

        /// <summary>
        /// W3C Trace Context environment variable. Each step span's <see cref="Activity.Id"/> is
        /// published here for that step, so tools the step invokes nest under the step span.
        /// </summary>
        public const string TraceParentVariable = "TRACEPARENT";

        public static readonly ActivitySource Source = new(SourceName, BuildConstants.RunnerPackage.Version);

        /// <summary>
        /// Builds a <see cref="TracerProvider"/> when an OTLP endpoint is configured, otherwise
        /// returns <see langword="null"/>. The caller owns the returned provider and must
        /// dispose it on process exit so buffered spans are flushed.
        /// </summary>
        public static TracerProvider TryCreateTracerProvider()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OtlpEndpointVariable)))
            {
                return null;
            }

            // Fully qualified: GitHub.Runner.Sdk (namespace) and OpenTelemetry.Sdk (class) are
            // both in scope, so the bare name "Sdk" would be ambiguous.
            return OpenTelemetry.Sdk.CreateTracerProviderBuilder()
                .ConfigureResource(resource => resource.AddService(SourceName, serviceVersion: BuildConstants.RunnerPackage.Version))
                .AddSource(SourceName)
                .AddOtlpExporter()
                .Build();
        }

        /// <summary>
        /// Records the outcome of a pipeline task or step on <paramref name="activity"/>. The
        /// tag value follows the OpenTelemetry CICD result vocabulary (success, failure, error,
        /// timeout, cancellation, skip). A failed or abandoned result also sets the span status
        /// to <see cref="ActivityStatusCode.Error"/>; other outcomes leave it unset.
        /// </summary>
        public static void SetResult(Activity activity, string resultTag, TaskResult? result)
        {
            if (activity == null)
            {
                return;
            }

            var value = result ?? TaskResult.Succeeded;
            activity.SetTag(resultTag, MapResult(value));
            if (value == TaskResult.Failed || value == TaskResult.Abandoned)
            {
                activity.SetStatus(ActivityStatusCode.Error, value.ToString());
            }
        }

        /// <summary>
        /// Extracts a remote <see cref="ActivityContext"/> from <paramref name="contextData"/> when
        /// the job was dispatched with a W3C <c>traceparent</c> input. Returns
        /// <see langword="default"/> if no valid trace context is present.
        ///
        /// Callers pass a <c>traceparent</c> (and optionally <c>tracestate</c>) via
        /// <c>workflow_dispatch</c> or <c>workflow_call</c> inputs so that the downstream job
        /// span becomes a child of the upstream step span, stitching cross-workflow runs into
        /// a single trace.
        ///
        /// The inputs live at the top-level <c>inputs</c> key of the job's context data —
        /// the same source that populates <c>${{ inputs.traceparent }}</c> in workflow YAML.
        /// </summary>
        public static ActivityContext TryExtractRemoteParent(IDictionary<string, PipelineContextData> contextData)
        {
            if (contextData == null ||
                !contextData.TryGetValue("inputs", out var inputsRaw) ||
                inputsRaw is not DictionaryContextData inputs)
            {
                return default;
            }

            if (!inputs.TryGetValue("traceparent", out var traceparentRaw))
            {
                return default;
            }

            var traceparent = traceparentRaw?.ToString();
            if (string.IsNullOrEmpty(traceparent))
            {
                return default;
            }

            inputs.TryGetValue("tracestate", out var tracestateRaw);
            var tracestate = tracestateRaw?.ToString();

            return ActivityContext.TryParse(traceparent, tracestate, isRemote: true, out var ctx)
                ? ctx
                : default;
        }

        // Maps a runner TaskResult onto the OpenTelemetry CICD result enum.
        // https://opentelemetry.io/docs/specs/semconv/cicd/
        private static string MapResult(TaskResult result) => result switch
        {
            TaskResult.Succeeded => "success",
            TaskResult.SucceededWithIssues => "success",
            TaskResult.Failed => "failure",
            TaskResult.Canceled => "cancellation",
            TaskResult.Skipped => "skip",
            TaskResult.Abandoned => "error",
            _ => "success",
        };
    }
}
