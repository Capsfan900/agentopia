# Semantic trace phase ownership

Historical phase record. Current audit ownership and acceptance are in
[`docs/superpowers/plans/2026-09-12-operational-audit.md`](docs/superpowers/plans/2026-09-12-operational-audit.md).
The old write assignments below are superseded for this audit.

Lead: `/root/astra_semantic_trace_lead` (Astra). No git repository and no AGENTS.md exist in this workspace. Preserve the dashboard and cyberpunk Observatory. No commits, cloud services, dependencies, or hidden reasoning. The VibeGame adapter does not apply.

| Path | Owner | Mode |
| --- | --- | --- |
| `semantic-trace-ownership.md` | Astra lead | read/write |
| `README.md` | Astra lead | read/write |
| `monitor.py` | `semantic_parser` (Sol) | read/write |
| `test_monitor.py` | `semantic_parser` (Sol) | read/write |
| `index.html` | `semantic_ui` (Terra) | read/write |
| `observatory.html` | `semantic_ui` (Terra) | read/write |
| `test_observatory.cjs` | `semantic_ui` (Terra) | read/write |
| All other existing files | all agents | read-only |
| Rollout/session input data | all agents | read-only |

The `semantic_evidence` Luna worker owns no files. Readers may inspect any task-relevant file. A worker may only edit its listed paths. The lead handles contract decisions and final integration; any ownership transfer must be recorded here before edits. Temporary verification artifacts may be created under the system temporary directory.

## Work graph

1. Luna indexes parser/event shapes/API/UI injection points while Astra defines the payload.
2. Astra fixes the shared contract using that evidence.
3. Sol adds the five required failing semantic tests, records their failures, then implements parser/API changes.
4. Terra integrates the explicit payload into dashboard and Observatory in parallel with Sol after the contract is fixed.
5. Astra reviews the actual files, resolves cross-lane issues, updates documentation, and runs the complete existing suites. Controller `/root` owns the final browser pass because IAB visibility is unavailable in this subagent and hidden-tab setup stalled.
6. Phase B follows stable trace integration: the same Terra owner adds an inspector-backed humanoid click menu, timeline binding, and camera focus/follow with explicit exit and Fit return. Task navigation requires a reliable supported link already present in project data; rename requires an existing safe API. Otherwise show the task name and omit rename. Astra owns integration checks; controller `/root` verifies desktop behavior and console errors.

Ponytail applies to every lane: understand the flow first; reuse existing helpers/data and native browser/Python features; no dependencies or speculative abstractions; keep the smallest correct change and meaningful behavior checks; preserve security, accessibility, and data protections. The company protocol owns model choice, file ownership, and integration.

## Released payload contract v1

Existing session/API fields remain compatible. Each session adds:

```text
prompt_context: {id, summary, text, source, session_id, turn_id, timestamp}
working_on: {summary, source, observation_id, status, task_path: [summary, ...]}
work_breakdown: [observation, ...]  // semantic nodes, flat and parent-linked
trace: {version: 1, root_id, observations: [observation, ...], truncated: boolean}
observation: {id, parent_id, session_id, kind, summary, source, timestamp, status,
              end_timestamp?, detail?, tool?, call_id?, files?, exit_code?}
```

- Stable observation IDs include session identity and rollout ordinal/item/call identity. `parent_id` is null at a prompt root. Allowed semantic kinds: `prompt`, `agent`, `step`, `delegation`, `outcome`; evidence kinds: `tool`, `result`, `file`, `test`, `approval`, `error`, `metric`. `work_breakdown` contains only semantic kinds. Sources are concise strings (`user_prompt`, `delegated_task`, `agent_commentary`, `agent_message`, `semantic_step`, `tool_inferred`, `prompt_fallback`, `metadata_fallback`, `runtime_event`). Runtime states use existing status vocabulary (`working`, `tool`, `approval`, `idle`, `completed`, `interrupted`, `failed`) and may use `running` on tool observations.
- A visible user prompt owns root intent until the next visible user prompt; injected environment/AGENTS/delegation wrappers do not become unrelated user requests. Keep prompt anchoring when the prompt lies outside the existing tail window. De-duplicate paired event/response representations. Visible assistant commentary/progress forms semantic `step` nodes. Only explicitly visible assistant channels are eligible; analysis/reasoning/summaries/encrypted content are excluded.
- A child is attached through its recorded parent/session path and the matching delegation. Its `prompt_context` remains the originating root prompt at delegation time, even after its parent receives a later prompt. If that association cannot be proved, use the nearest available parent context with an explicit fallback source. Nested child task paths follow recorded ancestors, with cycle guards. Shared ancestor nodes may appear in multiple local arrays with the same IDs; consumers de-duplicate them.
- Trace arrays contain the session's local observations and required ancestor nodes. Parent IDs may resolve through other sessions in the same snapshot. Consumers may collect descendant session arrays for a subtree, retaining only observations belonging to that subtree/root and de-duplicating IDs. A child agent node attaches to a recorded delegation or nearest parent semantic node; its own steps attach beneath that agent node.
- `working_on` precedence is delegated subagent assignment, latest visible progress/commentary/user-visible agent statement, active explicit semantic step, active tool mapped to its enclosing semantic step, then prompt fallback. A tool command never replaces the semantic statement. The source records which precedence branch supplied it. `status` reflects approval/final/stale outcomes and never infers ongoing work solely from old text.
- Keep summaries at about 260 characters, expanded prompt/tool input detail bounded at 12000 characters, and recent observations bounded (about 120 plus required ancestors). Mark truncation honestly. Escape on render. Full prompt/tool detail appears only after explicit expansion, never in hover/title attributes. Keep existing safe secret redaction; do not expose arbitrary tool output bodies. Result observations use safe status, exit code, known file/test facts, and a bounded generated summary. Unchanged logs should use cached semantic data; avoid adding another polling or reporting process.
- JSON input can provide these additions; otherwise normalize existing fields into clearly labeled fallbacks. Unknown/missing data degrades safely. No ETA or percentage completion in either UI. Account/context metrics remain.
- Dashboard presents Working on now, Prompt context, nested Work breakdown and source badges, with commands/tools/files/tests/results/metrics under collapsed Evidence stream. Filters, metrics, recent activity, finished groups, approvals and open state remain functional. Observatory uses the same semantic content and status for selection, labels and station choice while preserving its city rendering.

Implementation lanes must message the lead before changing this contract. No cloud services, dependencies, or invented task links. Existing tests excluding raw tool result text remain valid.

Parser checkpoint sequence: after five required RED failures were recorded, the controller requested a smaller landing. The same Sol worker retains exclusive ownership and first lands the visible prompt/step/tool parser, reports it, then proceeds to snapshot/nested delegation integration and caching after lead review. This changes delivery granularity, not the approved contract.

## Phase B interaction decisions

Explicit humanoid selection (pointer or the keyboard-accessible picker/canvas) opens a compact worker actions menu bound to that worker. Its actions reuse the existing inspector: Inspect work selects the current work/prompt/provenance view; Open timeline selects the nested breakdown/progress/evidence view. Hover must not retarget an open menu or its inspector. Escape dismisses the menu and keyboard focus remains usable. The menu uses native buttons with visible focus and is desktop-first.

Focus camera centers and follows the chosen worker. A visible Stop following action exits follow; Fit view also exits and fits all workers. Manual pan exits follow to respect user input. Removed workers clear follow/menu state. A change of selected worker stops following the old one until Focus camera is explicitly chosen again. Camera geometry and animated travel remain the existing implementation.

No supported task URL or nickname mutation API was found. Display task name as the navigation fallback, without an invented link or rename control. No agent state mutation actions. Controller browser acceptance covers desktop menu/selection/escape, inspect/timeline binding, follow/stop/fit/manual pan, disclosure persistence during live refresh, old JSON fallback, and no console errors.

Historical scope update: both pages and remaining acceptance became desktop-first. Mobile-specific work and verification are not required; preserve existing responsive behavior incidentally. Phase B kept Inspect work / Open timeline / Focus camera read-only. The earlier prohibition on native packaging has since been superseded by explicit authorization for a thin Windows WebView2 host and secure, context-aware Terminal. Electron remains rejected. See `docs/superpowers/specs/2026-09-12-terminal-webview2.md` and the functional-completion plan; final visual polish is deferred to Claude by the latest user instruction.

Live-format provenance refinement: source `retained_user_prompt` identifies only explicit `message` records with `role: user` restored from `compacted.payload.replacement_history`. Compaction summaries, assistant/developer records, encrypted items and reasoning are never used. This source distinguishes a retained visible prompt from a newly recorded direct prompt. Missing newer local records remain a documented fallback limitation.
