# Agent Foundry open format v1

This is the human-readable v1 interchange reference for the Agent Foundry files currently accepted and produced by `library.py`. The executable reference is the current `Library` validation/export/import implementation; where this document summarizes a generic JSON limit, readers must follow that code if a boundary matters. It is not an industry standard, and independent interoperability has not been tested. A reader should treat another implementation as unverified until it has been tested against the public fixtures and this implementation.

The format is ordinary JSON, UTF-8 text, and ZIP. It can be inspected without a running Agent Foundry app, network service, provider account, project checkout, or model. It is a current snapshot of records, not full history, a project archive, credential package, signature, or hidden model state.

## Bytes, revisions, and records

An immutable object is named by the lowercase SHA-256 of its exact JSON bytes. The writer emits compact, sorted, UTF-8 JSON (`ensure_ascii=False`, separators `,` and `:`); that is the byte form used for locally produced revisions. Do not parse and reserialize an object before hashing it.

The importer is broader than the writer: it passes raw member bytes to Python's JSON decoder (including the decoder's BOM-detected encodings) and accepts any member-key order, while validating bounded strict JSON. It rejects duplicate keys, non-finite numbers, malformed JSON, excessive nesting, and unsupported fields. It currently accepts bounded timestamp strings (maximum 80 characters); it does not ISO-8601-parse them. A portable writer should nevertheless emit UTF-8 UTC ISO-8601 timestamps.

Every object has exactly:

```json
{
  "schema_version": 1,
  "id": "canonical UUID",
  "kind": "work | template | component | validation",
  "parent_revision": "SHA-256 or null",
  "created_at": "bounded timestamp string",
  "action": "bounded action string",
  "payload": {}
}
```

Portable bundles do not carry `validation` objects. `parent_revision` is provenance for an immutable revision; it is not a request to fetch anything. A catalog head points at one current revision and has exactly `schema_version`, `id`, `kind` (`work`, `template`, or `component`), `title`, `revision`, `archived`, and `updated_at`, with optional `approved_revision` and `classification` only before export projection.

Object/head schema versions must equal integer `1`; object and head IDs use the validator's canonical UUID shape (the validator normalizes accepted case); revisions are 64 lowercase hexadecimal characters. Object `created_at` and `action` are nonblank strings at most 80 characters. Head title is nonblank and at most 200, `archived` is boolean, and `updated_at` is nonblank and at most 80. The optional head fields are validated by the library but are stripped from an exported projection.

## Payload schemas and constraints

Template payloads have exactly these required fields, plus optional `review` and `vendor_assets`:

```text
title, purpose,
harness {id, version, config_ref}, model {name, effort},
instruction_layers [{role, ref, rationale}],
components [{kind, ref, version, sha256, rationale}],
capabilities, tools,
permissions_policy {ref, description},
quality_policy {procedures, skill_mappings [{procedure, skill_ref}]},
environment {dependencies [{name, version, rationale}], required_env_names},
validation [{name, argv?, cwd_ref?, last_result_ref?}],
provenance {source_work_id, provider, session_id, captured_at},
resume {handoff, next_steps, open_questions}, compatibility, limitations
```

All listed nested objects reject missing or extra keys. Template `title` is at most 200 characters and `purpose` at most 4,000. Harness `id`/`version` are at most 200 and `config_ref` 2,000; model `name`/`effort` are at most 200. `instruction_layers` has at most 64 entries, each with three nonblank strings of at most 2,000. `components` has at most 128 entries; each `kind` is `skill`, `workflow`, `runbook`, `script`, or `instructions`, its `sha256` is lowercase SHA-256, and its other strings are at most 2,000.

`capabilities` and `tools` are lists of at most 256 nonblank strings (2,000 characters each). Permission `ref` is at most 2,000 and its description 4,000. Quality procedures are a non-empty list of at most 64 strings; at most 64 skill mappings may name only a listed procedure (`procedure` at most 200, `skill_ref` at most 2,000). Dependencies have at most 128 `{name, version, rationale}` entries (each at most 2,000). Required environment names are at most 256 strings and must match `[A-Za-z_][A-Za-z0-9_]{0,127}`; they name variables, never values. Validation has at most 64 entries: `name` is required (1,000), and optional `argv` is at most 64 strings, while `cwd_ref` and `last_result_ref` are at most 2,000. Provenance fields are at most 2,000; handoff text is at most 4,000; resume step/open-question lists are at most 64 strings. Compatibility and limitations are each lists of at most 128 strings.

`vendor_assets`, when present, is a list of at most 32 exact `{id, revision}` entries (canonical UUID and lowercase SHA-256). It is an explicit selection list, not a source-file discovery request.

Component kinds are `skill`, `workflow`, `runbook`, `script`, and `instructions`. Component versions are opaque bounded strings; they do not establish a shared version scheme. A component payload contains exactly `title`, `purpose`, `component_type`, `source_ref`, `version`, `sha256`, `rationale`, `compatibility`, and `limitations`, with optional `asset` and `review`. `purpose` and `rationale` are at most 4,000 characters; the other required component strings are at most 2,000. Its compatibility/limitations lists allow at most 128 strings. A selected asset has exactly base64 content, media type, license, and `selected: true`; base64 text is at most 2 MiB, media type 200, license 2,000, decoded bytes at most 1 MiB, and decoded SHA-256 must equal the component payload's `sha256`.

Artifact reviews (the optional template/component `review`) have exactly:

```text
candidate_revision: SHA-256,
baseline: {revision_or_evidence_hash: SHA-256, scope: nonblank string <=1000, artifact_id?: UUID},
verdict: "successful" | "mixed" | "failed", no_worse: boolean,
evidence: non-empty <=64 [{kind: "user-attested" | "measured", scope, summary, ref?}],
note: nonblank string <=4000, unverified: <=64 strings, user_confirmed: boolean
```

Evidence `scope`, `summary`, and optional `ref` are nonblank strings of at most 2,000. The manifest validators also reject recognized secret values; that scan is a defense, not proof of secrecy.

Work payloads have exactly `context`, a sanitized `snapshot`, and `completion {completed, source, statuses}`, with optional `capture` and `review`. Context is an optionally empty string of at most 4,000. Completion has exactly a boolean `completed`, source `runtime`, `user`, or `imported`, and at most 128 status strings. Imported work is made incomplete with `completion.source: "imported"`; no verdict is trusted.

A portable work snapshot is not arbitrary JSON: it must already equal the sanitizer's allowlisted result. Its scalar field allowlist is:

```text
id parent depth kind agent_path nickname name title task cwd model effort harness provider role
status turn_status progress eta_seconds estimate_basis updated age_seconds stale approval_pending
turn_elapsed_seconds current_action context_window last_event last_tool_status collaboration adapter
generated source selected_session_id root_id
```

It may also contain `sessions` (at most 128 recursively sanitized snapshots); `prompt_context`, `working_on`, `work_breakdown`, and `trace` as sanitized semantic values; `token_usage`, `last_token_usage`, and `rate_limits` as semantic values with details removed; and `recent_activity` as a sanitized semantic value. `collaboration` is accepted only through the scalar allowlist. Semantic values allow only these field names: `id`, `parent_id`, `session_id`, `kind`, `summary`, `source`, `timestamp`, `status`, `observation_id`, `turn_id`, `tool`, `task_path`, `version`, `root_id`, `end_timestamp`, `detail`, `text`, `time`, `observations`, `truncated`, `input_tokens`, `cached_input_tokens`, `cache_write_input_tokens`, `output_tokens`, `reasoning_output_tokens`, `total_tokens`, `model_context_window`, `plan_type`, `primary`, `secondary`, `used_percent`, `window_minutes`, `resets_at`, `limit_name`, `files`, `exit_code`, `call_id`, `ordinal`, `scope`, and `sessions`. Semantic lists are limited to 256 entries and the sanitizer cuts off beyond semantic depth 8. Unknown snapshot/semantic fields, unredacted recognized secrets, or `detail`/`text` where the sanitizer would remove them cause rejection rather than preservation. Readers should preserve the public fixture shape instead of relying on deeper nested structures.

`capture`, when present, has exactly `sanitizer_version: 1`, boolean `include_details`, `method`, `capture_environment {python, platform}`, and `configuration_limitations`. Capture environment strings are at most 200; method and configuration-limitations strings are at most 2,000. A work review is deliberately different from an artifact review: it has exactly `verdict` (`successful`, `mixed`, or `failed`), optionally empty `note` (4,000), and bounded `recorded_at` (80).

Harness, model, inference provider, component type, capabilities, and compatibility are independent declarations. Use `"unrecorded"` when a fact is absent rather than inferring it. `argv`, handoff text, instruction layers, validation declarations, source references, and external paths are data: importing or reading them does not execute commands, follow paths, install components, or create provider sessions.

## Bundle members and integrity

A ZIP contains only:

```text
README.md
context.md
manifest.json
objects/<sha256>.json
assets/<sha256>             # vendor mode only
```

The manifest has exactly `schema_version`, `mode`, `created_at`, `items`, `objects`, `assets`, `documents`, and `compatibility {declared, evaluated}`. `mode` is `reference` or `vendor`; `created_at` is a nonblank string at most 80 characters; `evaluated` must be `false`; and declared compatibility is at most 128 strings. `items` is a non-empty list of at most 64 valid catalog heads. `objects` and `assets` are lists of at most 64 entries, and `documents` has exactly two entries. A document/object entry has exactly `{path, sha256}`; an asset entry has exactly `{path, sha256, component_id, license, media_type}` (license at most 2,000, media type at most 200). The importer hashes raw object, document, and asset bytes before it parses or decodes their content. It does not sign the manifest or the ZIP container, so a manifest/ZIP signature is not part of v1.

The manifest's catalog entries must match every listed object by id, kind, and revision; no orphan objects or undeclared files are accepted. A component asset must match exactly one component object and that component's declared hash, license, and media type. The only accepted relative member names are exactly `README.md`, `context.md`, `manifest.json`, `objects/<64 lowercase hex>.json`, and `assets/<64 lowercase hex>`. Absolute names, backslashes, traversal segments, drive-colon prefixes, directory entries, names over 240 characters, ZIP links, duplicate names, and other relative names reject.

Current hard limits are 8 MiB compressed upload, 2 MiB per member/object, 16 MiB total expanded members, 64 ZIP entries, and a maximum 200:1 uncompressed-to-compressed member ratio. Generic JSON accepts only `null`, booleans, integers, finite floats, strings, lists, and objects; strings are bounded to 20,000 characters by default, object keys to 200, lists/objects to 256 entries, and nesting beyond the validator's depth 12 rejects. `content_base64` is the one bounded-string exception (up to twice the 1 MiB asset maximum before base64 decoding). These limits are implementation limits, not a claim that a broader reader will accept more.

## Projections and import graph

Before export, a template's component `sha256` is a revision pin. The exporter identifies a local component when `ref` is a UUID after removing an optional `library:` prefix; it includes that pinned component object and its catalog head. Reference mode strips stored component asset bytes and derives replacement immutable component/template revisions so pins continue to match. It also strips template review and vendor selections. Vendor mode requires explicit `vendor_assets` selections and copies only selected component bytes plus their license/media metadata; unselected asset bytes are stripped.

Import first validates the entire ZIP, then preserves each foreign object for provenance and creates local `import_candidate` revisions. It rewrites local template pins to the mapped local component revisions, removes foreign reviews, clears head approvals/classifications, and resets imported work completion. Compatibility remains a declaration and is returned as unevaluated.

New candidate identity uses the immutable foreign object's `created_at`, not package creation time. Repackaging the same object graph therefore reuses the local candidates. Existing legacy candidates are reused without rewriting their objects or heads only when the complete transformed object matches, allowing its stored timestamp as the sole difference. A meaningfully changed local revision remains a conflict. Component mappings are resolved before template pins.

The current reference rule has two legacy quirks, not a recommended stable profile: a bare UUID is accepted as an internal component-reference alias, and a malformed `library:` reference is treated as external/unverified rather than rejected. Writers seeking portability should use canonical `library:<uuid>` for internal pins and explicit non-library references otherwise.

## Strictness, evolution, and current gaps

Unknown schema versions, fields, and ZIP files reject. There is no automatic migration and no inferred execution. A future extension must use an explicitly versioned and namespaced format; transformations should create new immutable records while preserving originals.

Bundle `compatibility.declared` and the root payload's `compatibility` can still disagree; import validates the bundle declaration but does not enforce equality with the root payload.

This is a current limitation, not a promise of a stable interoperable profile. Consumers must review compatibility, references, licenses, limitations, and any possible secret exposure themselves; checksum validation and known-pattern secret scanning do not prove safety or destination suitability.

## Public conformance evidence

`examples/open-format-v1/` holds fixed public component, template, handoff-work, and asset bytes. `test_open_format.py` verifies their declared hashes, generates temporary vendor/reference ZIPs using only the standard library and the existing `Library` validators/exporter/importer, proves unknown version/key/hash rejection, and round-trips inert handoff data. Run:

```text
py -3 -m unittest test_open_format -q
```

If the `py` launcher is unavailable, invoke an installed Python 3 interpreter with the same `-m unittest` arguments.
