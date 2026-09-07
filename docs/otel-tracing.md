# OpenTelemetry tracing

This runner can emit an OpenTelemetry trace per job: one root span for the job and one
child span per step, with a **link** to the workflow run the job belongs to. Tracing is **opt-in** and configured entirely through the
standard `OTEL_*` environment variables — when no OTLP endpoint is set, the runner
behaves exactly as upstream and pays no measurable cost.

## What gets emitted

Attributes follow the OpenTelemetry [CICD](https://opentelemetry.io/docs/specs/semconv/cicd/)
and [VCS](https://opentelemetry.io/docs/specs/semconv/attributes-registry/vcs/) semantic
conventions (semconv 1.41, `development` stability). A workflow run maps to a CICD
pipeline run; the job dispatched to this runner maps to a pipeline task. Steps are finer
than semconv models, so they keep a `github.step.*` namespace while reusing the CICD
result vocabulary for their values.

| Span | Kind | Key attributes |
|------|------|----------------|
| job  | `Server`   | `cicd.pipeline.name`, `cicd.pipeline.run.id`, `cicd.pipeline.run.url.full`, `cicd.pipeline.task.name`, `cicd.pipeline.task.run.id`, `cicd.pipeline.task.run.result`, `vcs.repository.url.full`, `vcs.repository.name`, `vcs.ref.head.name`, `vcs.ref.head.revision`, `vcs.ref.head.type` |
| step | `Internal` | `github.step.name`, `github.step.number`, `github.step.type` (`run`/`repository`/`docker`), `github.action`, `github.step.result` |

Results use the CICD vocabulary (`success`, `failure`, `error`, `cancellation`, `skip`).
A `failure` (failed step/task) or `error` (abandoned, e.g. worker killed) also sets the
span status to `Error`; other outcomes leave it unset.

Each step's W3C trace context is published to that step's environment as `TRACEPARENT`,
so instrumented build tooling (test runners, `docker build`, custom CLIs) emits **child
spans of the step that ran them** rather than disconnected traces. The step spans in turn
nest under the job span, giving a single job → step → tool tree.

No workflow changes are required: a step's subprocess inherits the runner's own
environment — including the `OTEL_*` exporter configuration set on the runner (see below) —
and the runner overlays this per-step `TRACEPARENT` on top. Any tool that is itself
OpenTelemetry-instrumented therefore exports to the same collector and parents to its step
automatically. Tools that are not instrumented are unaffected.

## How a run's jobs are related

One trace per job, linked — not one trace per workflow run. CI is not request/response
work, and this is the shape Honeycomb recommends for it:

> "Since Honeycomb only charges by events (spans) and doesn't care how many trace
> identifiers there are, it's perfectly reasonable to create a new trace per job."
> — [Exotic Trace Shapes](https://www.honeycomb.io/blog/exotic-trace-shapes)

Each job span carries an OpenTelemetry **link** to its workflow run, with a target
derived identically by every job in the run and needing no coordination:

    SHA-256("<repository>/<run_id>/<run_attempt>")           -> trace id  (first 16 bytes)
    SHA-256("<repository>/<run_id>/<run_attempt>/workflow")  -> span id   (first 8 bytes)

SHA-256 is a distribution function here, not a security control — every input is public.

**Why a link rather than a shared parent.** A runner process handles exactly one job and
never learns the run's start time, its sibling jobs, or its conclusion, so no runner can
emit a span for the workflow itself. Parenting jobs to a derived workflow span therefore
leaves every trace with a MISSING ROOT, and Honeycomb documents that as the most damaging
kind of missing span: queries filtering on `is_root` do not count the trace at all. A link
keeps the relationship navigable while every job span stays a real, queryable root.

It also is not needed for grouping. The job span already carries
`cicd.pipeline.run.id`, so "every job in this run" is one `GROUP BY` away.

**Does `needs:` put jobs in one trace?** Only if you ask for it. A `needs:` edge alone
changes nothing — the downstream job is still its own trace, linked to the run. The two
merge into a single trace only when the upstream job explicitly exports its step trace
context as a job output:

```yaml
jobs:
  build:
    outputs:
      traceparent: ${{ steps.tp.outputs.traceparent }}
    steps:
      - id: tp
        run: echo "traceparent=$TRACEPARENT" >> "$GITHUB_OUTPUT"
  deploy:
    needs: build
    steps: [...]           # deploy's job span is now a child of build's step span
```

That case is safe precisely because the parent it attaches to is a span that **is**
emitted — the upstream job's step span, itself under a real root. It is the opposite of
parenting to a derived workflow span that nothing ever emits. So the rule is: attach to a
span that exists, link to one that does not.

**Re-runs.** `run_attempt` is part of the seed, so re-running a whole workflow links to a
different run. Re-running a *single failed job* does not increment `run_attempt`, so that
job links back to the original run — the intended reading of "this job belongs to that
workflow run".

## Cross-workflow trace propagation

When one instrumented job triggers another via `workflow_dispatch` or `workflow_call`,
the runner stitches the two workflow runs into a single trace automatically — as long as
the caller passes `TRACEPARENT` as a dispatch input and the callee declares it.

### Caller workflow

The step that triggers the downstream workflow already has `TRACEPARENT` set in its
environment by the runner. Pass it as a dispatch input:

```yaml
- name: Dispatch deployment
  env:
    INFRA_TOKEN: ${{ secrets.INFRA_DEPLOY_TOKEN }}
    IMAGE_TAG: ${{ steps.tag.outputs.tag }}
  run: |
    curl -fsS -X POST \
      -H "Authorization: token ${INFRA_TOKEN}" \
      -H "Content-Type: application/json" \
      "https://api.github.com/repos/org/infra/actions/workflows/deploy.yml/dispatches" \
      -d "{
        \"ref\": \"main\",
        \"inputs\": {
          \"image_tag\": \"${IMAGE_TAG}\",
          \"traceparent\": \"${TRACEPARENT}\"
        }
      }"
```

`gh workflow run` works the same way:

```yaml
- run: |
    gh workflow run deploy.yml \
      --ref main \
      --field image_tag="${IMAGE_TAG}" \
      --field traceparent="${TRACEPARENT}"
```

### Called workflow

Declare `traceparent` as an optional `workflow_dispatch` input. No other changes are
needed — the runner reads it automatically and starts the job span as a child of the
upstream step span.

```yaml
on:
  workflow_dispatch:
    inputs:
      image_tag:
        required: true
        type: string
      traceparent:
        description: "W3C traceparent for distributed tracing"
        required: false
        type: string
```

The `tracestate` W3C header is also supported as an optional input under the key
`tracestate`, if the caller propagates it.

### Resulting trace shape

```
ci / build job
  └── step: dispatch deployment           ← TRACEPARENT set here by runner
        ↓  (remote parent via inputs.traceparent)
infra / deploy job                        ← job span parented to the step above
  └── step: update manifests
  └── step: commit and push
  └── step: wait for rollout
```

Both workflow runs share the same `traceId`, so a single trace view in Honeycomb or
any other backend shows the full build → deploy pipeline without any manual span
construction.

## Same-run job chaining (`needs.<job>.outputs.traceparent`)

Jobs inside ONE workflow run are separate runner processes, so by default each job
span is its own trace root. To chain them (web → docker → deploy as one trace), a
dependency job exports its step trace context as a job output, and the downstream
job's span is parented on it automatically:

```yaml
jobs:
  docker:
    outputs:
      tag: ${{ steps.tag.outputs.tag }}
      traceparent: ${{ steps.tp.outputs.traceparent }}
    steps:
      # ... build steps ...
      - name: Export trace context
        id: tp
        run: echo "traceparent=$TRACEPARENT" >> "$GITHUB_OUTPUT"

  deploy:
    needs: [docker]   # ← deploy's job span is parented on docker's exported step
    steps: ...
```

Resolution order for the job span's remote parent:

1. `inputs.traceparent` (workflow_dispatch / workflow_call) — cross-workflow wins;
2. `needs.<job>.outputs.traceparent` — dependency jobs visited in ordinal-sorted
   name order, first valid traceparent wins (deterministic for multi-`needs` jobs);
3. otherwise the job span is a new trace root.

An exported `tracestate` output (`needs.<job>.outputs.tracestate`) is honored too.
Combine both mechanisms and an app's web → docker → deploy chain plus the
cross-repo deploy workflow it dispatches all share one `traceId`.

### How it works

Before starting the job span the runner reads `message.ContextData["inputs"]` — the
same data that populates `${{ inputs.traceparent }}` in YAML. If the value parses as a
valid W3C traceparent, the job span is started with that as its remote parent
(`ActivityContext.TryParse(..., isRemote: true)`). If the input is absent, empty, or
malformed the runner falls back to a new root span, so the called workflow degrades
gracefully when triggered manually or by other events.

> **Implementation note:** `workflow_dispatch` and `workflow_call` serialize the
> `inputs` context differently. `workflow_call` uses `DictionaryContextData`;
> `workflow_dispatch` (REST API or `gh workflow run`) uses
> `CaseSensitiveDictionaryContextData`. Both implement `IReadOnlyObject` from
> `GitHub.DistributedTask.Expressions2.Sdk`, which is the interface the runner uses to
> read the values. If you are porting this logic elsewhere, do not type-check against
> `DictionaryContextData` directly — it will silently miss every `workflow_dispatch`
> trigger.

## Configuration

All configuration uses the standard OpenTelemetry environment variables read by the
SDK. The only one the runner inspects directly is `OTEL_EXPORTER_OTLP_ENDPOINT`: if it
is unset, no tracer provider is built and tracing stays off.

| Variable | Example | Purpose |
|----------|---------|---------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://otel.observability.svc.cluster.local:4318` | Enables tracing and sets the collector endpoint. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` | OTLP transport. Use `http/protobuf` for a 4318 endpoint. |
| `OTEL_SERVICE_NAME` | `github.actions.runner` | `service.name` on every span (defaults to `github.actions.runner`). |
| `OTEL_RESOURCE_ATTRIBUTES` | `k8s.pod.name=...,k8s.namespace.name=...` | Extra resource attributes. The runner code is host-agnostic; identity is injected here. |

## Deploying on Actions Runner Controller (ARC)

Point the runner scale set at the image built from this fork
(`images/Dockerfile.otel`) and add the OTel environment to the runner container. The
downward API supplies the pod identity that lets traces be correlated with pod
metrics and Kubernetes events downstream.

```yaml
template:
  spec:
    containers:
      - name: runner
        image: ghcr.io/yackey-labs/actions-runner:<tag>   # built from images/Dockerfile.otel
        env:
          - name: POD_NAME
            valueFrom: { fieldRef: { fieldPath: metadata.name } }
          - name: POD_NAMESPACE
            valueFrom: { fieldRef: { fieldPath: metadata.namespace } }
          - name: NODE_NAME
            valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
          - name: OTEL_EXPORTER_OTLP_ENDPOINT
            value: "http://otel.observability.svc.cluster.local:4318"
          - name: OTEL_EXPORTER_OTLP_PROTOCOL
            value: "http/protobuf"
          - name: OTEL_SERVICE_NAME
            value: "github.actions.runner"
          - name: OTEL_RESOURCE_ATTRIBUTES
            value: "k8s.pod.name=$(POD_NAME),k8s.namespace.name=$(POD_NAMESPACE),k8s.node.name=$(NODE_NAME),cicd.worker.id=$(POD_NAME),cicd.worker.name=$(POD_NAME)"
```

The worker identity (`cicd.worker.*`) is supplied as a resource attribute rather than set
in runner code, so the runner stays host-agnostic and ops can change it without a rebuild.

> The OTLP traces pipeline must accept the runner namespace. If your collector drops
> the `arc-runners` namespace on the **logs** pipeline (to suppress verbose runner
> stdout), confirm that filter is not also on the **traces** pipeline.

## Correlating spans with pod metrics and Kubernetes events

Because each span carries `k8s.pod.name` (and namespace/node) as resource attributes —
the same attributes the collector's `k8sattributes` processor stamps on pod metrics,
and that the Kubernetes events receiver records on events — a backend can join all
three on `k8s.pod.name` over the span's time window:

- **span ↔ metrics:** CPU/memory for the pod between the step span's start and end.
- **span ↔ events:** scheduling, image pull, OOMKill, and eviction events for the pod
  during the job.

This turns "step X was slow" into "step X was slow *and* the pod was at its memory
limit with a kubelet `Evicted` event 3s later" without leaving the trace view.
