using System;
using System.Collections.Generic;
using System.Diagnostics;
using GitHub.DistributedTask.Expressions2.Sdk;
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
        /// Extracts a remote <see cref="ActivityContext"/> for the job span from
        /// <paramref name="contextData"/>. Returns <see langword="default"/> if no valid
        /// trace context is present. Two sources are consulted, in order:
        ///
        /// <list type="number">
        /// <item><description>
        /// <c>inputs.traceparent</c> (+ optional <c>inputs.tracestate</c>) — set via
        /// <c>workflow_dispatch</c> or <c>workflow_call</c> inputs, stitching
        /// cross-workflow runs into a single trace. Takes precedence.
        /// </description></item>
        /// <item><description>
        /// <c>needs.&lt;job&gt;.outputs.traceparent</c> — a dependency job in the SAME run
        /// that exported its step <c>TRACEPARENT</c> as a job output
        /// (<c>echo "traceparent=$TRACEPARENT" &gt;&gt; "$GITHUB_OUTPUT"</c> + an
        /// <c>outputs:</c> mapping). This chains a run's jobs (build → deploy) into one
        /// trace. Jobs are visited in ordinal-sorted name order and the first valid
        /// traceparent wins, so multi-dependency jobs resolve deterministically.
        /// </description></item>
        /// </list>
        ///
        /// Both <c>workflow_call</c> (uses <see cref="DictionaryContextData"/>) and
        /// <c>workflow_dispatch</c> (uses <see cref="CaseSensitiveDictionaryContextData"/>)
        /// are handled via <see cref="IReadOnlyObject"/>, the common interface of both types.
        /// </summary>
        public static ActivityContext TryExtractRemoteParent(IDictionary<string, PipelineContextData> contextData)
        {
            if (contextData == null)
            {
                return default;
            }

            var fromInputs = FromDispatchInputs(contextData);
            if (fromInputs != default)
            {
                return fromInputs;
            }

            return FromNeedsOutputs(contextData);
        }

        // inputs.traceparent / inputs.tracestate (workflow_dispatch & workflow_call).
        private static ActivityContext FromDispatchInputs(IDictionary<string, PipelineContextData> contextData)
        {
            if (!contextData.TryGetValue("inputs", out var inputsRaw) ||
                inputsRaw is not IReadOnlyObject inputs ||
                !inputs.TryGetValue("traceparent", out var traceparentObj))
            {
                return default;
            }

            inputs.TryGetValue("tracestate", out var tracestateObj);
            return ParseContext(traceparentObj?.ToString(), tracestateObj?.ToString());
        }

        // needs.<job>.outputs.traceparent — dependency jobs of the same run that exported
        // their step trace context as a job output.
        private static ActivityContext FromNeedsOutputs(IDictionary<string, PipelineContextData> contextData)
        {
            if (!contextData.TryGetValue("needs", out var needsRaw) ||
                needsRaw is not IReadOnlyObject needs)
            {
                return default;
            }

            var jobNames = new List<string>(needs.Keys);
            jobNames.Sort(StringComparer.Ordinal);

            foreach (var jobName in jobNames)
            {
                if (!needs.TryGetValue(jobName, out var jobObj) ||
                    jobObj is not IReadOnlyObject job ||
                    !job.TryGetValue("outputs", out var outputsObj) ||
                    outputsObj is not IReadOnlyObject outputs ||
                    !outputs.TryGetValue("traceparent", out var traceparentObj))
                {
                    continue;
                }

                outputs.TryGetValue("tracestate", out var tracestateObj);
                var ctx = ParseContext(traceparentObj?.ToString(), tracestateObj?.ToString());
                if (ctx != default)
                {
                    return ctx;
                }
            }

            return default;
        }

        private static ActivityContext ParseContext(string traceparent, string tracestate)
        {
            if (string.IsNullOrEmpty(traceparent))
            {
                return default;
            }

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
