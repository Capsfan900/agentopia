# Agentopia guide

Agentopia helps you preserve, evaluate, reuse, transport and resume recorded agent work. The Library stores ordinary files and versioned records. Reading or importing a record never runs its instructions.

## Save Handoff

Save Handoff creates a durable handoff plus audit snapshot of the selected session and its child-agent family. It records the work, relevant available evidence, status and your resume notes so a person or future agent can continue later. You deliberately choose whether to include purpose-relevant prompt/tool details; review those details before sharing.

It does not save project files, transfer a running process, preserve hidden/model context or automatically recreate the full agent configuration. Missing information stays missing. Runtime completion, recorded test evidence and your verdict are independent facts.

Handoffs can be marked complete, reopened, reviewed and archived. These actions create new audit revisions; they do not rewrite the previous snapshot or delete the source session. The Operations Done filter is live history, not permanent storage.

In the Windows Terminal page, a verified Codex resume pane can show its live **Working now**, approval state and whether a matching Handoff exists. A fork is labeled as a source-session reference; a new session is not claimed as linked until its identity is known. Ordinary shells remain unlinked. This adjacent status is read-only and never includes terminal contents or saved prompt/trace detail.

## Components and Agent Templates

A **Component** is an independently versioned reusable ingredient: a Skill, Instruction, Workflow, Runbook or Script/Tool. A hook or evaluation procedure can be represented using these supported types; neither is currently a separate schema type. A source reference is a declaration, not a file that Agentopia follows or installs. You may deliberately store an exact file with licensing information; that does not prove compatibility or redistribution rights.

An **Agent Template** is the complete recipe. It pins exact component versions and declares harness/model requirements, instruction layers, tools and capabilities, permissions, quality procedures, environment, provenance, limitations and resume instructions. These are inert declarations, not live configuration.

Drafting an Agent Template from a handoff provides a starting point from recorded facts. Review and fill missing requirements yourself. It does not discover every instruction, dependency or component version that the original session used.

## Evaluate a change

An isolated candidate is a new Library item; the original is unchanged. Adding a component or replacing a pin through the candidate actions creates a new candidate, not an update to a running agent.

**Check schema & pinned references** checks record structure and recorded Library references and saves a validation report. It does not run commands, external tools, behavioral tests or benchmarks, or establish destination compatibility. Linked validation and manually run checks are not the same thing.

**Compare & review** displays the selected baseline and candidate. You run relevant checks outside Agentopia and record the scope, evidence, limitations and your own verdict. A successful structural check never supplies a human verdict.

**Mark reviewed version approved** records an approved Library revision after the required review and structural checks. The approved pointer and current draft may differ. Approval does not install, configure, deploy or launch anything.

## Agentopia Packages

An Agentopia Package (called an Agent Foundry bundle in the compatible v1 file format) is a portable checksummed ZIP containing a manifest, README and handoff/context documentation plus selected records.

- Exporting a Component includes that component record.
- Exporting an Agent Template includes the template and its exact pinned Library component records. External references remain declarations.
- Exporting a Handoff includes its recorded snapshot, not project files or the live process.
- Reference mode omits stored asset bytes and leaves external files as references.
- Vendor mode includes only deliberately selected stored files with licensing information, after you review the inventory and acknowledge redistribution responsibility.

Neither mode produces a project archive, credential package or hidden model state. External file availability, source changes, licenses and compatibility are not verified automatically. Inspect the export inventory and warnings before download.

**Import Agentopia Package** validates an Agentopia ZIP (including compatible Agent Foundry v1 packages) before importing its Agent Templates, Components and/or Handoffs as inert, locally untrusted records. It rejects arbitrary ZIP/files; it does not execute instructions, install components, modify a running agent or inherit foreign approvals. **New Component** is the direct local creation path. Import currently also supports Handoffs; describing it as components-only would hide an implemented capability.

## Relevance and provenance

Relevant records have retained evidence of direct agent instructions or a supporting agent workflow. Generic nearby files and uncertain records are excluded from the default view but retained in Excluded / All discovered. Exclusion is not deletion or a quality verdict.

Kind, domain, source project/path, maturity, provider/harness, portability and validation labels describe recorded evidence. Unknown means no sufficient evidence was recorded. Retained bytes do not prove portability. Classification is local, bound to an exact revision and separate from review/approval; it is not inherited through packages.

## Harness, Provider and Model

- **Harness:** the agent application/runtime orchestrating a session, such as Codex, Claude Code or Pi Agent. Hermes is a harness only when the source identifies it in that role.
- **Provider:** the service actually serving inference, such as OpenAI, Anthropic, OpenRouter, Azure OpenAI, Google or Local.
- **Model:** the specific model identifier/name.

The source adapter reads a data source; the JSON adapter is not a harness. No provider is inferred from a harness, model name, plan or path. Reported role is optional; Hermes is not classified from its name alone. Plan limits are account-level telemetry. Last-call context and tracked-session token totals have different scopes.

Settings reports recorded Harness, Provider and Model values separately and labels account telemetry by scope. It does not check sign-in. Claude Code, Pi Agent and Hermes are visible unavailable placeholders only; no adapter discovery or authentication is attempted.

An eligible Codex worker in Observatory exposes **Send instruction…** as its primary worker action. It opens the same review and explicit confirmation flow as Session actions; selecting a worker never sends by itself.

## Saved-agent goal and current boundary

The goal is a reproducible, versioned composition that can be inspected, tested, approved, exported/imported and eventually applied through a supported harness. Today Agent Templates store and review the composition. Save Handoff does not capture every instruction/component/dependency version or automatically reconstruct and launch the agent. A terminal is also not an automatic template installer.

## Open format status

Agentopia currently has a versioned file format, not an industry standard. Its JSON/Markdown records and checksummed ZIP packages are readable without a running Agentopia service. The executable reference validators and format contract describe what this implementation accepts; another tool must independently implement and test the same semantics before cross-tool interoperability can be claimed.

Current imports are strict: unsupported schema versions and unknown record keys are rejected, not executed or guessed. No extension mechanism or automatic migration is promised. Existing source projects, skills and agent configurations are read-only evidence and are never rewritten to conform to this format.
