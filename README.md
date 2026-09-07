> ## ⚠️ This is a fork
>
> **`yackey-labs/actions-runner-otel`** — a fork of [`actions/runner`](https://github.com/actions/runner)
> that emits an OpenTelemetry trace per job — one root span for the job, one child span
> per step, and a link to the workflow run it belongs to — following the OTel
> [CICD](https://opentelemetry.io/docs/specs/semconv/cicd/)
> and [VCS](https://opentelemetry.io/docs/specs/semconv/attributes-registry/vcs/)
> semantic conventions.
>
> Tracing is **opt-in and zero-cost when off** — configured entirely through
> standard `OTEL_*` environment variables. With no OTLP endpoint set, the runner
> behaves exactly as upstream. **No workflow changes are ever required.**
>
> Full design and attribute reference: **[`docs/otel-tracing.md`](docs/otel-tracing.md)**
>
> ### Using it with Actions Runner Controller (ARC)
>
> Point the runner container at this image and give it an OTLP endpoint. That is
> the whole integration — nothing in your workflows changes:
>
> ```yaml
> template:
>   spec:
>     containers:
>       - name: runner
>         image: ghcr.io/yackey-labs/actions-runner-otel:<runner-version>-otel.<N>
>         command: ["/home/runner/run.sh"]
>         env:
>           - name: OTEL_EXPORTER_OTLP_ENDPOINT
>             value: "http://<your-otlp-collector>:4318"
>           - name: OTEL_EXPORTER_OTLP_PROTOCOL
>             value: "http/protobuf"
>           - name: OTEL_SERVICE_NAME
>             value: "github.actions.runner"
>           # Optional but recommended: attribute spans to the pod and node that
>           # ran them, so a slow job can be traced to a specific worker.
>           - name: OTEL_RESOURCE_ATTRIBUTES
>             value: "k8s.pod.name=$(POD_NAME),k8s.node.name=$(NODE_NAME),cicd.worker.id=$(POD_NAME)"
> ```
>
> Because each step's subprocess inherits the runner's environment, any
> OTel-instrumented tool a step invokes (test runner, `docker build`, a custom
> CLI) exports to the same collector and **parents itself to the step that ran
> it** — via the per-step `TRACEPARENT` the runner injects. Uninstrumented tools
> are unaffected.
>
> ### Authenticated collectors
>
> Auth needs no extra configuration here — the OpenTelemetry SDK reads the standard
> variables, so exporting straight to a vendor is just:
>
> ```yaml
>           - name: OTEL_EXPORTER_OTLP_ENDPOINT
>             value: "https://api.honeycomb.io"
>           - name: OTEL_EXPORTER_OTLP_PROTOCOL
>             value: "http/protobuf"
>           - name: OTEL_EXPORTER_OTLP_HEADERS
>             valueFrom:
>               secretKeyRef: { name: honeycomb-api-key, key: otlp-headers }
> ```
>
> **The runner removes `OTEL_EXPORTER_OTLP_HEADERS` from its environment once the
> exporter has read it.** Steps inherit the runner's environment, so leaving it in
> place would hand the credential to every line of workflow code — on a machine that
> runs other people's jobs. Steps keep `OTEL_EXPORTER_OTLP_ENDPOINT`,
> `OTEL_EXPORTER_OTLP_PROTOCOL` and `TRACEPARENT`, so instrumented tools still export
> and still nest under their step; they just cannot authenticate as the runner. Set
> `RUNNER_OTEL_EXPOSE_HEADERS_TO_STEPS=true` if you want them to.
>
> Prefer pointing the runner at a collector you control and letting **it** hold the
> vendor credential. Then no secret is anywhere near the runner, and you get batching,
> retry and redaction for free.
>
> ### Images
>
> `.github/workflows/build-otel-image.yml` publishes to
> `ghcr.io/yackey-labs/actions-runner-otel` on every push to `main`:
>
> | tag | meaning |
> |-----|---------|
> | `<runner-version>-otel.<N>` | immutable, SemVer-sortable — pin this |
> | `<runner-version>-otel.latest` | moving pointer to the newest build |
>
> `N` is the build number. The tag is valid SemVer (`otel.N` is a numeric
> pre-release identifier), so `9` sorts before `10` and dependency tooling can
> tell newer from older. The commit sha lives in the
> `org.opencontainers.image.revision` label rather than the tag, because a sha
> is alphanumeric and would make the tag unorderable.
>
> Pin the numbered tag. How you roll a new one out is deliberately not this
> repo's concern — the build publishes, and nothing here knows or cares what
> consumes it.
>
> ### Keeping the fork current
>
> Sync from upstream and rebase the OTel overlay on top; do not cherry-pick
> individual upstream changes. Upstream's scheduled dependency bots
> (`node-upgrade`, `docker-buildx-upgrade`, `dotnet-upgrade`, `npm-audit`) are
> **disabled here on purpose** — they propose changes to upstream-owned files
> that arrive with the next sync anyway, and merging them would put the fork
> ahead of upstream and manufacture conflicts. They are disabled via the Actions
> API rather than deleted, so there is no diff against upstream.

---

<p align="center">
  <img src="docs/res/github-graph.png">
</p>

# GitHub Actions Runner

[![Actions Status](https://github.com/actions/runner/workflows/Runner%20CI/badge.svg)](https://github.com/actions/runner/actions)

The runner is the application that runs a job from a GitHub Actions workflow. It is used by GitHub Actions in the [hosted virtual environments](https://github.com/actions/virtual-environments), or you can [self-host the runner](https://help.github.com/en/actions/automating-your-workflow-with-github-actions/about-self-hosted-runners) in your own environment.

## Get Started

For more information about installing and using self-hosted runners, see [Adding self-hosted runners](https://help.github.com/en/actions/automating-your-workflow-with-github-actions/adding-self-hosted-runners) and [Using self-hosted runners in a workflow](https://help.github.com/en/actions/automating-your-workflow-with-github-actions/using-self-hosted-runners-in-a-workflow)

Runner releases:

![win](docs/res/win_sm.png) [Pre-reqs](docs/start/envwin.md) | [Download](https://github.com/actions/runner/releases)  

![macOS](docs/res/apple_sm.png)  [Pre-reqs](docs/start/envosx.md) | [Download](https://github.com/actions/runner/releases)  

![linux](docs/res/linux_sm.png)  [Pre-reqs](docs/start/envlinux.md) | [Download](https://github.com/actions/runner/releases)

### Note

Thank you for your interest in this GitHub repo, however, right now we are not taking contributions. 

We continue to focus our resources on strategic areas that help our customers be successful while making developers' lives easier. While GitHub Actions remains a key part of this vision, we are allocating resources towards other areas of Actions and are not taking contributions to this repository at this time. The GitHub public roadmap is the best place to follow along for any updates on features we’re working on and what stage they’re in.

We are taking the following steps to better direct requests related to GitHub Actions, including:

1. We will be directing questions and support requests to our [Community Discussions area](https://github.com/orgs/community/discussions/categories/actions)

2. High Priority bugs can be reported through Community Discussions or you can report these to our support team https://support.github.com/contact/bug-report.

3. Security Issues should be handled as per our [SECURITY.md](https://github.com/actions/runner?tab=security-ov-file)

We will still provide security updates for this project and fix major breaking changes during this time.

You are welcome to still raise bugs in this repo.
