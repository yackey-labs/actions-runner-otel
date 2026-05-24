# OpenTelemetry tracing

This runner can emit an OpenTelemetry trace per job: one root span for the job and
one child span per step. Tracing is **opt-in** and configured entirely through the
standard `OTEL_*` environment variables — when no OTLP endpoint is set, the runner
behaves exactly as upstream and pays no measurable cost.

## What gets emitted

| Span | Kind | Key attributes |
|------|------|----------------|
| job  | `Server`   | `github.job`, `github.job.name`, `github.run_id`, `github.run_number`, `github.run_attempt`, `github.repository`, `github.workflow`, `github.actor`, `github.sha`, `github.ref`, `github.job.result` |
| step | `Internal` | `github.step.name`, `github.step.number`, `github.step.type` (`run`/`repository`/`docker`), `github.action`, `github.step.result` |

A failed result sets the span status to `Error`; succeeded/skipped/canceled set `Ok`.

Each step's W3C trace context is published to that step's environment as `TRACEPARENT`,
so instrumented build tooling (test runners, `docker build`, custom CLIs) emits **child
spans of the step that ran them** rather than disconnected traces. The step spans in turn
nest under the job span, giving a single job → step → tool tree. See the
`yackey-labs/ci-trace` action for a convenient way to consume it.

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
            value: "k8s.pod.name=$(POD_NAME),k8s.namespace.name=$(POD_NAMESPACE),k8s.node.name=$(NODE_NAME)"
```

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
