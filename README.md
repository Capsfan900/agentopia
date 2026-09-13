# Agent Foundry

Agent Foundry is a local operations view and ordinary-file library for agent work. It reads live session
telemetry, presents it in two browser views, and lets you deliberately save sanitized work or versioned
agent artifacts. Passive monitoring does not send provider commands, run models, or modify source projects.

The canonical data and persistence rules are in [`docs/CONTRACT.md`](docs/CONTRACT.md).

## Start

Browser mode needs only Python's standard library.

```powershell
Set-Location '<path-to-your-clone>'
py -3 monitor.py --open
```

The default address is <http://127.0.0.1:8777/>. Stop the server with `Ctrl+C`.

Useful launch options:

```powershell
# Read a runner-neutral JSON state file instead of local Codex data
py -3 monitor.py --adapter json --state-file 'C:\path\agent-state.json' --open

# Use a different private settings/library location or loopback port
py -3 monitor.py --data-dir 'C:\path\AgentFoundry' --port 9000 --open
```

The server only accepts the exact loopback bind address `127.0.0.1`; ports must be 1024–65535. By default,
settings and the private library live in `%LOCALAPPDATA%\AgentFoundry`. Command-line values override saved
settings for that launch.

## Experimental Windows desktop

The local self-contained build is `desktop/windows/dist/AgentFoundry.exe`. It bundles its
Python and .NET runtimes and uses installed WebView2. Launch without options for monitoring;
`--enable-terminal` opts into the native Terminal page. Omit that flag to disable Terminal.
`--data-dir <folder>` selects a separate settings/library folder. Do not run browser and
desktop writers against the same folder; the desktop refuses to adopt an existing server.

Application navigation lives in the web header; the native window supplies only normal
window controls and lifecycle. Terminal opens lazily. Starting or restarting a pane requires
native confirmation of the enrolled profile/project/context. Closing a connection only
detaches; closing a pane or quitting stops its owned Windows processes. This is process
ownership, not a sandbox or a guarantee for externally brokered, elevated or WSL processes.

Codex, Windows PowerShell and Command Prompt use pinned executable locations and hashes. Git
Bash appears only when the native `C:\Program Files\Git\bin\bash.exe` matches its reviewed
hash. To deliberately enroll another local shell, create `terminal-profiles.json` in the
selected data directory (normally `%LOCALAPPDATA%\AgentFoundry`):

```json
{
  "schema_version": 1,
  "profiles": [
    {
      "name": "My reviewed shell",
      "executable": "C:\\Tools\\my-shell.exe",
      "sha256": "replace-with-the-reviewed-lowercase-sha256"
    }
  ]
}
```

Get the value to review and paste with
`(Get-FileHash -Algorithm SHA256 'C:\Tools\my-shell.exe').Hash.ToLowerInvariant()`.

The native host accepts at most eight exact records and re-verifies the local, non-reparse
executable before launch. Configured profiles receive no arguments; PATH lookup, command
strings, WSL and elevation are not supported. Invalid records are omitted and described in
the launch review so one bad record does not enroll or hide another.

The current self-contained distribution was rebuilt from the verified 54-file stage after
final review (`bundle-manifest.json` SHA-256 `8f92cbe04712be0016b5107f55a254086f0f6bdad008be287a4689f7c849ac13`;
`AgentFoundry.exe` SHA-256 `54fb90fbde8015b71ce5b41edb53b65734a165906074aa46e8150af468ff0367`).
Headless checks and clean publishing pass. Hidden run 15 and visible attempt 03 used the older
`85051ec4…` manifest, so they do not certify this rebuilt binary. Active, foreground, visible
rendering and user-perceived lag remain unverified. Port 8777 and visible acceptance remain
stopped; see the [current verification status](docs/verification-2026-09-12.md). The build is
not signed or installed automatically. Browser-mode launch and `Ctrl+C` remain the rollback workflow.

## Pages

- **Operations** — <http://127.0.0.1:8777/> groups sessions by recorded project directory. Search or filter
  Working, Approval, Idle, and Done states; inspect prompt context, current work, nested work breakdown, and
  collapsed evidence. Done is a live-record filter, not a durable archive.
- **Observatory** — <http://127.0.0.1:8777/observatory> projects the same payload into a desktop isometric
  view. Select a worker with the canvas or picker, use confirmed Send for an eligible Codex worker, inspect
  work or timeline, focus/stop following, drag to
  pan, use the wheel to zoom, and use Fit to restore the whole scene. Pause motion and reduced-motion support
  affect animation, not telemetry.
- **Library** — <http://127.0.0.1:8777/library> holds explicit saved work, templates, and components.
- **Settings** — <http://127.0.0.1:8777/settings> selects the Codex or generic JSON source and shows recorded
  Harness/Provider/Model and account status separately. Claude Code, Pi Agent, and Hermes are unavailable
  placeholders until adapters exist. Source and verbose-logging changes apply after Save; a new port and browser-open
  preference apply next start.

Windows path separators, extended-path prefixes, and case are normalized when the views group recorded
project directories. POSIX paths remain case-sensitive. A missing path is kept separate rather than guessed.

## Data sources and privacy

The Codex adapter reads local thread locks, rollout JSONL, the session index, and selected thread/turn data
from Codex SQLite files in read-only mode. It does not read `auth.json`. Visible user prompts, visible agent
statements, safe tool metadata, known file/test facts, usage, and status can appear. Hidden reasoning,
encrypted content, arbitrary tool-output bodies, and credentials are excluded. Codex's local storage is not
a stable public API, so unknown data degrades to explicit fallbacks.

The JSON adapter reads an atomically replaced file with a top-level `sessions` array. See
[`example-state.json`](example-state.json) and the [live payload contract](docs/CONTRACT.md#live-payload-v1).
It does not discover sessions or gain provider controls.

Credentials remain in official provider clients. Agent Foundry has no API-key setting.

## Save and review work

From Operations, Observatory, or Library, open **Session actions…**, choose a session, and select **Save Handoff**.
The saved record includes that root session and its descendants at that moment. Fields are
allowlisted, common secret patterns are redacted, and full visible prompt/tool detail is excluded unless you
explicitly include it. Capture metadata records the sanitizer version, detail choice, and local Python/platform;
it does not make the snapshot fully reproducible. Only model/effort values already present in the source are
captured, while instruction/component versions and project dependencies remain unrecorded. Review any detailed
snapshot before sharing it.

In Library you can archive without deleting, explicitly mark a record complete or reopen it, and record a
separate `successful`, `mixed`, or `failed` user verdict once complete. Runtime completion, tests, cost, and
the user's verdict are independent facts.

## Templates and components

A template is a versioned manifest for reconstructing an agent setup: harness/model declarations,
instruction layers, exact component pins, capabilities/tools, permission and quality procedures,
environment names, provenance, handoff steps, compatibility, and limitations. It does not contain hidden
model state. Components—skills, workflows, runbooks, scripts, or instructions—are independently versioned
and pinned by exact SHA-256 revision.

Create or edit a draft, pin exact components, then **Validate structure**. Structural validation checks the
schema and available library pins only. Validation commands in a manifest are inert declarations; Agent
Foundry never runs project tests or benchmarks automatically.

Before promotion, choose a pinned baseline, run the relevant checks yourself, record scoped evidence and
unverified areas, give an explicit verdict, and attest that the candidate is no worse for that scope.
Promotion requires successful or explicitly accepted mixed evidence. It updates only the artifact's approved
library pointer—it does not install instructions, change global configuration, or alter a running agent.

If you close a saved review before promotion, **Compare & review…** offers an explicit fresh review-free
revision; previous review history and the approved pointer are preserved. A cloned draft suggests its original
as a baseline; the exact baseline revision is pinned when you open the comparison.

Checked examples: [`examples/template.json`](examples/template.json) and
[`examples/component.json`](examples/component.json). They are inert, unreviewed examples, not installed agents.

## Export and import

Every export starts with a preflight inventory. Review its warnings and acknowledge the matching plan before
download. Reference mode exports manifests and exact references without stored asset bytes. Vendor mode also
requires a separate redistribution confirmation and includes only explicitly selected, stored assets with a
license declaration. Agent Foundry never crawls a referenced repository or arbitrary source path.
Use **Pin component…** and explicitly select its stored file for vendor bundles. The export inventory shows
the actual source/config references and selected asset licenses; private-path, dirty-state and external-file
checks remain explicitly unverified. Unselected stored bytes are stripped even in vendor mode.

Portable ZIP bundles contain a manifest with checksums, `README.md`, `context.md`, and immutable JSON objects;
vendor bundles may also contain allowlisted assets. Import rejects unsafe paths, symbolic-link ZIP
entries, duplicate or malformed JSON, unsupported schemas, oversized/compression-bomb input, checksum
failures, and likely secrets. Imported instructions and commands remain inert. Foreign approvals, reviews,
and completion claims do not become local trust decisions.

The library's ordinary files and exported bundles are Git-ready if you choose to manage them that way.
Agent Foundry itself never initializes a repository, commits, or pushes.

## Explicit Send follow-up

When the selected Codex adapter reports the locally verified native capability, **Send follow-up…** can queue
one confirmed message to one existing Codex thread. The UI shows the exact destination and message before
confirmation. Requests are bounded, deduplicated, rate-limited, and have no automatic retry; output is not
exposed. No other provider mutation is supported.

The native executable and queue surface were locally verified. A live follow-up was not exercised as part of
the implementation audit, so check the destination thread after use—especially if a timeout is reported as
uncertain. An executable update disables the capability until its hash is explicitly re-verified.

## Troubleshooting

- **The server rejects startup:** verify the selected Codex directory or absolute JSON state file exists,
  restore/remove an invalid `settings.json`, and use `127.0.0.1` with a port from 1024–65535.
- **The browser says forbidden:** use the exact printed `127.0.0.1:<port>` address. Foreign or duplicate Host,
  foreign Origin, and cross-site mutation requests are rejected.
- **No sessions appear:** Codex discovery follows currently tracked thread locks; the JSON adapter only shows
  rows present in its state file. Confirm the source on Settings.
- **A working thread looks idle:** an in-progress Codex record with no changed rollout for 30 minutes is shown
  as stale/idle rather than inventing activity.
- **A library edit reports a conflict:** another revision won. Refresh files, inspect the new revision, and
  repeat the explicit action.
- **Import or export is blocked:** read the reported schema, checksum, size, secret, inventory, licensing, or
  acknowledgement warning. The app deliberately does not bypass it.
- **Send follow-up is unavailable:** select Codex and verify the installed native executable still matches the
  locally approved binary. JSON sources are read-only.

## Verify changes

```powershell
python -m unittest -q
node test_operations.cjs
node test_observatory.cjs
node test_foundry_ui.cjs
```

Node is needed only for these offline browser-code checks, not to run Agent Foundry. Current results,
baseline comparisons and unverified scope are recorded in [the verification report](docs/verification-2026-09-12.md).
