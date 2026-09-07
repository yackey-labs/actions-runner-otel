using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
        /// Standard OTLP header variable, used for collector auth (for example
        /// <c>x-honeycomb-team=&lt;key&gt;</c> when exporting straight to a vendor). Read by
        /// the OpenTelemetry SDK; named here only so it can be removed from the environment
        /// once the exporter has consumed it — see <see cref="TryCreateTracerProvider"/>.
        /// </summary>
        private const string OtlpHeadersVariable = "OTEL_EXPORTER_OTLP_HEADERS";

        /// <summary>
        /// Set to <c>true</c>/<c>1</c> to leave <see cref="OtlpHeadersVariable"/> in the
        /// environment that steps inherit. Off by default: see the security note on
        /// <see cref="TryCreateTracerProvider"/>.
        /// </summary>
        private const string ExposeHeadersVariable = "RUNNER_OTEL_EXPOSE_HEADERS_TO_STEPS";

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
            var provider = OpenTelemetry.Sdk.CreateTracerProviderBuilder()
                .ConfigureResource(resource => resource.AddService(SourceName, serviceVersion: BuildConstants.RunnerPackage.Version))
                .AddSource(SourceName)
                .AddOtlpExporter()
                .Build();

            // OTEL_EXPORTER_OTLP_HEADERS is how an authenticated collector is configured, so
            // it routinely holds a credential (x-honeycomb-team=<key>, Authorization=...).
            // Steps inherit this process's environment -- that inheritance is deliberate and
            // is what lets an instrumented tool inside a step export to the same collector --
            // but it would also hand that credential to every line of workflow code the
            // runner executes, on a machine that runs other people's jobs.
            //
            // The exporter has already read the value by this point (AddOtlpExporter binds
            // its options during Build), so removing it here costs the runner nothing and
            // closes the exposure. Steps keep OTEL_EXPORTER_OTLP_ENDPOINT/PROTOCOL and
            // TRACEPARENT, so tools still export and still nest under their step -- they
            // simply cannot borrow the runner's credentials.
            //
            // Escape hatch for anyone who genuinely wants in-step tools authenticating as the
            // runner. Off by default because the safe direction is the one you have to ask for.
            if (!IsTruthy(Environment.GetEnvironmentVariable(ExposeHeadersVariable)))
            {
                Environment.SetEnvironmentVariable(OtlpHeadersVariable, null);
            }

            return provider;
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
        /// <item><description>
        /// A context DERIVED from the workflow run identity — see
        /// <see cref="FromWorkflowRun"/>. This is the default, and it replaces the previous
        /// behaviour of every job starting its own trace. It requires no workflow changes
        /// and no coordination between jobs: parallel jobs with no <c>needs</c> edge still
        /// land in one trace per workflow run.
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

            var fromNeeds = FromNeedsOutputs(contextData);
            if (fromNeeds != default)
            {
                return fromNeeds;
            }

            return FromWorkflowRun(contextData);
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

        private static bool IsTruthy(string value)
            => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

        /// <summary>
        /// Derives a deterministic trace context for the workflow RUN from
        /// <c>github.repository</c>, <c>github.run_id</c> and <c>github.run_attempt</c>.
        ///
        /// A runner process handles exactly one job. It cannot see the workflow's start
        /// time, its sibling jobs, or its conclusion, so no runner can emit a span for the
        /// workflow itself. What every runner in a run CAN do is independently compute the
        /// same identifiers from values all of them already have — no coordination, no
        /// workflow changes, and it works for parallel jobs that have no <c>needs</c> edge
        /// to inherit from.
        ///
        /// The job spans therefore share one trace and hang off a common workflow span id.
        /// That span is never emitted, so the trace has no root. This is deliberate: the
        /// grouping is the valuable part, and emitting a real root would require a
        /// <c>workflow_run</c>-triggered reporter added to every consuming repository —
        /// which would cost exactly the per-repository wiring this runner exists to avoid.
        /// Workflow duration remains derivable from the job spans (earliest start to latest
        /// end).
        ///
        /// SHA-256 is used as a distribution function, not for security: the inputs are all
        /// public. Trace and span ids are drawn from DIFFERENT hashes so the span id is not
        /// a prefix of the trace id.
        ///
        /// Note on re-runs: <c>run_attempt</c> is part of the input, so re-running a whole
        /// workflow produces a new, separate trace. Re-running a SINGLE failed job does not
        /// increment <c>run_attempt</c>, so that job rejoins the original run's trace —
        /// which is the intended reading of "this job belongs to that workflow run".
        /// </summary>
        private static ActivityContext FromWorkflowRun(IDictionary<string, PipelineContextData> contextData)
        {
            if (!contextData.TryGetValue("github", out var githubRaw) ||
                githubRaw is not IReadOnlyObject github)
            {
                return default;
            }

            var repository = TryGetString(github, "repository");
            var runId = TryGetString(github, "run_id");
            if (string.IsNullOrEmpty(repository) || string.IsNullOrEmpty(runId))
            {
                return default;
            }

            // run_attempt is absent on older server versions; treat that as attempt 1 rather
            // than refusing to group the run.
            var runAttempt = TryGetString(github, "run_attempt");
            if (string.IsNullOrEmpty(runAttempt))
            {
                runAttempt = "1";
            }

            var seed = $"{repository}/{runId}/{runAttempt}";
            var traceId = ActivityTraceId.CreateFromBytes(Digest(seed, 16));
            var spanId = ActivitySpanId.CreateFromBytes(Digest($"{seed}/workflow", 8));

            return new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded, isRemote: true);
        }

        private static string TryGetString(IReadOnlyObject obj, string key)
            => obj.TryGetValue(key, out var value) ? value?.ToString() : null;

        // First <paramref name="length"/> bytes of SHA-256(input). An all-zero id is invalid
        // per the W3C spec; SHA-256 makes that outcome not worth guarding against, but the
        // check is cheap and turns an impossible-in-practice case into a fall-through rather
        // than an ArgumentException at job start.
        private static byte[] Digest(string input, int length)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var bytes = new byte[length];
            Array.Copy(hash, bytes, length);

            var allZero = true;
            foreach (var b in bytes)
            {
                if (b != 0) { allZero = false; break; }
            }
            if (allZero) { bytes[length - 1] = 1; }

            return bytes;
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
