# Agent Foundry contract

This document is the canonical stable contract for Agent Foundry's live payload, saved library objects,
portable bundles, and trust boundaries. Historical plans and verification reports are not runtime contracts.

## Product boundaries

Agent Foundry has four surfaces:

- Operations (`/`) and Observatory (`/observatory`) are two projections of the same live payload.
- Settings (`/settings`) selects a local telemetry source and stores ordinary configuration.
- Library (`/library`) explicitly persists sanitized work and versioned artifacts.
- The only provider mutation is an exact, confirmed Codex Send follow-up when the verified native capability
  is available.

Monitoring is local, model-free, and passive. Saving settings/library data is an explicit local write. The app
does not edit source projects, install imported content, run declared commands, initialize Git, commit, push,
approve provider actions, or automatically change instructions.

## Live payload v1

`GET /api/state` and each `state` event from `/api/events` carry the same runner-neutral envelope:

```text
{
  generated: ISO-8601 string,
  adapter: "codex" | "json",
  source: string,
  sessions: [session, ...],
  account: account,
  counts: {sessions: integer, agents: integer, working: integer}
}
```

`source` is the selected local Codex data root or JSON state-file path, not a project identity. `counts` is a
current summary, not durable history.

### Session

The adapters preserve useful supplied fields and normalize these common fields:

```text
session: {
  id: string,
  parent: string?,
  depth: integer?,
  kind: string,
  agent_path: string?, nickname: string?, name: string,
  title: string?, task: string,
  cwd: string?, model: string?, effort: string?, provider: string?,
  status: status,
  turn_status: string?, stale: boolean?, approval_pending: boolean?,
  updated: ISO-8601 string?, age_seconds: number,
  current_action: string?, recent_activity: [activity, ...]?,
  turn_elapsed_seconds: number?,
  token_usage: object?, last_token_usage: object?, context_window: number?, rate_limits: object?,
  prompt_context: prompt_context,
  working_on: working_on,
  work_breakdown: [observation, ...],
  trace: trace
}
```

Live statuses are `working`, `tool`, `thinking`, `approval`, `idle`, `completed`, `interrupted`, and `failed`.
Tool observations may use `running`. Missing intent or provenance is labeled as a fallback; it is never
invented. An unchanged Codex rollout still recorded in progress becomes stale/idle after 30 minutes.

The views derive project identity from the recorded root session `cwd`. Descendants inherit that root.
Windows drive/UNC paths normalize extended prefixes, slash direction, and case. POSIX paths remain
case-sensitive. Empty/unknown paths remain separate. This grouping is a browser projection; the payload keeps
the recorded path.

### Semantic trace

```text
prompt_context: {
  id, summary, text, source, session_id, turn_id, timestamp
}

working_on: {
  summary, source, observation_id, status, task_path: [summary, ...]
}

trace: {
  version: 1,
  root_id,
  observations: [observation, ...],
  truncated: boolean
}

observation: {
  id, parent_id, session_id, kind, summary, source, timestamp, status,
  end_timestamp?, detail?, tool?, call_id?, files?, exit_code?
}
```

`work_breakdown` is the flat, parent-linked subset of semantic observations. Semantic kinds are `prompt`,
`agent`, `step`, `delegation`, and `outcome`. Evidence kinds are `tool`, `result`, `file`, `test`, `approval`,
`error`, and `metric`.

A visible user prompt owns intent until the next visible user prompt. Visible agent commentary/statements can
form steps. Tool evidence attaches to its semantic step and does not replace it. A child follows recorded
parent/delegation identity; cycle guards and fallback sources handle incomplete input. Consumers must
de-duplicate stable observation IDs when combining session traces.

Summaries are bounded to about 260 characters; expanded visible prompt/tool-input detail is bounded to 12,000
characters. Recent trace content is bounded while retaining required ancestors, and `truncated` reports that
loss. Hidden reasoning, reasoning summaries, encrypted content, arbitrary tool-output bodies, and injected
environment/instruction wrappers are excluded. Known result status, exit code, file/test facts, and generated
safe summaries may remain.

### Account

The Codex adapter supplies non-secret telemetry when it exists:

```text
account: {
  provider: string,
  plan: string,
  token_usage: object,
  rate_limits: object,
  scope: "tracked sessions"
}
```

The JSON adapter preserves a supplied account object. Absence means unavailable, not zero. Metrics are
evidence and never a success verdict.

### Generic JSON input

The JSON source requires a top-level `sessions` array. At minimum, each row should provide a stable `id`;
`name`, `parent`, `task`, `status`, `cwd`, and `current_action` improve the projection. It may provide the full
semantic fields above. Missing semantic fields receive labeled fallbacks. The writer should atomically replace
the state file. The adapter never discovers sessions or sends provider actions.

## Settings contract

The default data directory is `%LOCALAPPDATA%\AgentFoundry` (with a platform-local fallback when
`LOCALAPPDATA` is absent). `settings.json` is an ordinary bounded JSON file:

```text
{
  schema_version: 1,
  config: {
    adapter: "codex" | "json",
    codex_home: string,
    state_file: string,
    host: "127.0.0.1",
    port: integer 1024..65535,
    open: boolean,
    verbose: boolean
  }
}
```

Settings writes require the current SHA-256 settings revision and use a same-directory temporary file,
`fsync`, and replace. Adapter/source and verbose-logging changes take effect immediately. Port/browser-open
launch behavior takes effect on restart. Credentials remain in provider clients and are not settings fields.
Claude Code, Pi Agent and Hermes may appear in capability presentation as disabled placeholders, but are not
valid `adapter` values until an implementation is added. Detected Harness, Provider, Model and account values
are read-only telemetry; no sign-in status is inferred.

## Library storage contract

Opening a missing library is read-only. The first explicit write creates `<data-dir>/library`:

```text
library/
  catalog/<uuid>.json
  objects/<sha256>.json
```

Catalog heads are small mutable pointers:

```text
{
  schema_version: 1,
  id: canonical UUID,
  kind: "work" | "template" | "component",
  title: string,
  revision: SHA-256,
  approved_revision?: SHA-256,
  archived: boolean,
  updated_at: ISO-8601 string
}
```

Objects are immutable and named by the SHA-256 of their canonical JSON bytes:

```text
{
  schema_version: 1,
  id: canonical UUID,
  kind: "work" | "template" | "component" | "validation",
  parent_revision: SHA-256 | null,
  created_at: ISO-8601 string,
  action: string,
  payload: object
}
```

The public item returned by the API combines its head and object metadata and exposes object `payload` as
`data`. One process-local re-entrant lock serializes writes. Each mutation checks `expected_revision`, writes
and `fsync`s the immutable object first, then atomically replaces the head. Archive, completion, verdict, and
promotion transitions create audit revisions; stale requests fail instead of overwriting newer state.

The in-memory catalog index is loaded once at startup and updated by local writes. **Refresh files** is the
only rescan. Malformed heads are ignored with sanitized visible warnings; files are not repaired or deleted.
User-controlled filesystem paths never enter catalog/object lookup. Library paths reject escape, symbolic
links, and Windows reparse points.

## Saved work

Saved work is a point-in-time, sanitized record of one selected root session and its descendant family:

```text
data: {
  context: string,
  capture: {
    sanitizer_version: 1,
    include_details: boolean,
    method: string,
    capture_environment: {python: string, platform: string},
    configuration_limitations: string
  },
  snapshot: {
    adapter?, generated?, source?, selected_session_id?, root_id?,
    sessions?: [allowlisted session, ...],
    ...allowlisted single-session fields
  },
  completion: {
    completed: boolean,
    source: "runtime" | "user" | "imported",
    statuses: [string, ...]
  },
  review?: {
    verdict: "successful" | "mixed" | "failed",
    note: string,
    recorded_at: ISO-8601 string
  }
}
```

Unknown/private snapshot fields are discarded; common secret patterns are redacted. Visible prompt/tool
details are omitted by default and included only by an explicit `include_details` choice. Even then they are
bounded and redacted and must be reviewed before sharing.

`capture` describes how this local snapshot was sanitized and records the local Python/platform environment.
It is provenance, not a reproducibility claim: model and effort are captured only when the selected source
recorded them, while instruction/component versions and project dependencies are unrecorded. Existing records
without `capture` remain valid.

Archive is independent of completion. Runtime terminal status can initialize completion; the user can
explicitly mark complete or reopen. A verdict is allowed only on a completed saved record. Runtime state,
test/tool evidence, cost/usage, and the user's verdict are independent. No verdict is inferred.

Imported work is a new local candidate: foreign review is removed and completion becomes untrusted/imported
and incomplete until a local explicit decision.

## Template manifest

A template `data` object has exactly these required fields, plus optional `review` and `vendor_assets`:

```text
{
  title: string,
  purpose: string,
  harness: {id, version, config_ref},
  model: {name, effort},
  instruction_layers: [{role, ref, rationale}, ...],
  components: [{kind, ref, version, sha256, rationale}, ...],
  capabilities: [string, ...],
  tools: [string, ...],
  permissions_policy: {ref, description},
  quality_policy: {
    procedures: [semantic procedure ID, ...],
    skill_mappings: [{procedure, skill_ref}, ...]
  },
  environment: {
    dependencies: [{name, version, rationale}, ...],
    required_env_names: [NAME, ...]
  },
  validation: [{name, argv?, cwd_ref?, last_result_ref?}, ...],
  provenance: {source_work_id, provider, session_id, captured_at},
  resume: {handoff, next_steps: [string, ...], open_questions: [string, ...]},
  compatibility: [string, ...],
  limitations: [string, ...],
  review?: review,
  vendor_assets?: [{id: component UUID, revision: SHA-256}, ...]
}
```

Component kinds are `skill`, `workflow`, `runbook`, `script`, and `instructions`. A library component reference
uses `ref: "library:<uuid>"` and pins its exact object revision in `sha256`. Other references remain external
and explicitly unverified. Environment entries contain names only, never secret values. Validation `argv`
and paths are declarations and are never executed or followed.

`quality_policy.procedures` contains provider/tool-neutral procedure IDs. `skill_mappings` may point at a
currently useful skill, but mappings are optional and never turn that skill into a hard dependency.

## Component manifest

A component `data` object is independently versioned:

```text
{
  title: string,
  purpose: string,
  component_type: "skill" | "workflow" | "runbook" | "script" | "instructions",
  source_ref: string,
  version: string,
  sha256: SHA-256,
  rationale: string,
  compatibility: [string, ...],
  limitations: [string, ...],
  asset?: {
    content_base64: string,
    media_type: string,
    license: string,
    selected: true
  },
  review?: review
}
```

`asset` is accepted only after a deliberate local selection, with a non-empty license and matching content
hash, up to 1 MiB. Reference export removes stored bytes. Vendor export includes only components explicitly
listed by exact ID/revision in the template's `vendor_assets`; source paths are never crawled.

## Candidate validation, review, and promotion

Structural validation produces an immutable report bound to one exact candidate revision:

```text
{
  validation_id: SHA-256,
  subject_id: UUID,
  subject_revision: SHA-256,
  scope: "structural",
  checks: [{name, status}, ...],
  errors: [string, ...],
  warnings: [string, ...],
  result: "valid" | "invalid"
}
```

External references, destination compatibility, and functional behavior remain `unverified`; schema success
must not be described as behavioral success.

Promotion requires a later review revision whose manifest, excluding `review`, exactly equals the validated
candidate payload:

```text
review: {
  candidate_revision: SHA-256,
  baseline: {
    artifact_id?: UUID,
    revision_or_evidence_hash: SHA-256,
    scope: string
  },
  verdict: "successful" | "mixed" | "failed",
  no_worse: boolean,
  evidence: [{kind: "user-attested" | "measured", scope, summary, ref?}, ...],
  note: string,
  unverified: [string, ...],
  user_confirmed: boolean
}
```

The baseline can be a pinned prior artifact, saved work, configuration revision, or explicit evidence hash;
it is never fabricated. Promotion requires a valid bound structural report, a non-failed explicit verdict,
`no_worse: true`, non-empty scoped evidence, a note, and `user_confirmed: true`. Mixed can promote only as an
explicit scoped acceptance with its limitations visible. Green tests alone are insufficient. Users run
project checks or benchmarks themselves and record the evidence; Agent Foundry does not run them.

Promotion writes an audit revision and changes only `approved_revision`. The current draft and approved
candidate remain distinct. No project, provider, installed skill, global configuration, or running agent is
changed.

## Portable bundle contract

An export requires two calls. Preflight computes the exact inventory and warnings and returns a `plan_hash`.
Export requires the same item revision and plan hash plus `acknowledge_warnings: true`; vendor mode additionally
requires `confirm_vendor: true`. Any changed head/inventory rejects the stale plan.

The bound inventory includes titles, versions, exact revisions, selected asset hashes/licenses/media types,
and actual known source/config/component/path references. Reference availability, repository dirty state,
external credential contents and destination compatibility are not checked. Stored-content secret scanning
recognizes known patterns only; neither this scan nor a preflight acknowledgement proves confidentiality.

A ZIP contains only:

```text
README.md
context.md
manifest.json
objects/<sha256>.json
assets/<sha256>          # vendor mode only
```

The bundle manifest is schema version 1 and lists the mode, timestamp, catalog items, every object/document/
asset path and checksum, declared compatibility, and `evaluated: false`. README/context provide human-readable
handoff, limitations, inertness, and compatibility warnings.

Reference mode includes exact manifests/references but derives byte-free component objects where necessary.
Vendor mode includes only selected stored assets and matching component manifests, licenses, media types, and
checksums. Neither mode reads arbitrary source paths.
Unselected component asset bytes are removed from JSON objects in both modes; derived component revisions
and their template pins are updated together. Vendor selection is explicit when pinning a component in the UI.

Import validates the complete ZIP before its commit point. It rejects traversal/absolute/backslash paths,
links, unsupported files or schemas, duplicate paths/IDs/JSON keys, non-finite or deeply nested JSON,
undeclared/orphan objects, oversized uploads/members/totals, suspicious compression ratios, mismatched hashes
or identities, inconsistent asset/license metadata, and detected secret patterns. Secret detection is a
defense, not a guarantee; recipients still review exports.

Valid imported objects are preserved for provenance, but each public item points to a deterministic local
`import_candidate` revision. Foreign `approved_revision` and reviews are stripped; imported work completion
is untrusted. Instructions, validation declarations, handoff steps, and commands remain data. The compatibility
report preserves declarations and says destination compatibility was not evaluated.

Library directories and bundles are ordinary files suitable for deliberate Git management. Agent Foundry
does not perform Git operations.

## HTTP and action boundary

GET routes require one accepted loopback Host and reject a foreign Origin when supplied. Responses use
no-store, MIME-sniffing protection, same-origin/frame protections, and a per-response script nonce policy.

POST routes require exact loopback Host and Origin, one session token obtained from the local bootstrap,
strict bounded JSON with a single Content-Length, unique keys, finite numbers, and route-specific schemas.
The browser receives only stable public error codes.

Send follow-up requires the exact action schema, canonical thread/request UUIDs, an explicit confirmation,
an existing Codex session, the pinned executable hash, per-thread exclusion, deduplication, and rate
limiting. It invokes one native queue command without a shell, hides process I/O, and never retries
automatically. A timeout is uncertain rather than success. The executable/queue surface was locally verified;
a live send was not exercised during the audit. JSON sources and all other provider controls are read-only or
unsupported.

## Honest limitations

- Codex local files are an implementation-dependent source and may change.
- Missing local events, intent, parent links, metrics, compatibility data, or provider capabilities remain
  unavailable/fallback rather than inferred.
- The library lock coordinates one server process; it is not a cross-process transaction manager.
- Secret-pattern redaction/scanning cannot prove that content is secret-free.
- Structural validation cannot establish quality, compatibility, performance, cost, or user success.
- Portable bundles do not restore hidden model state or automatically recreate a provider session.
