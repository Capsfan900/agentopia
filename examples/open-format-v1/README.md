# Open-format v1 public fixtures

These tiny public records exercise the current Agent Foundry v1 importer. They contain no credentials, private paths, source-project data, or executable instructions.

`test_open_format.py` creates temporary reference and vendor ZIPs from these fixed bytes, then uses the existing `Library` importer/exporter. The ZIPs are deliberately runtime-generated rather than committed binary files.

| Record | SHA-256 of the exact UTF-8 file bytes |
| --- | --- |
| `component.object.json` | `f08c0aa946f137478a7a73e3746969fe35abdf92afd593fe46b77a8375ca880f` |
| `template.object.json` | `309dcd249a60021c53c2872772beddd14e6afdfdfba72faef07f38bfae37fc17` |
| `handoff-work.object.json` | `57c64223682cf1fc0e482724ed147d23b8f7a92d5e38e6035c38fbc63282735a` |
| `component-asset.txt` | `0f438f5272722ce06a732b050778087967e0e798cbc6c320a5b368bea681c1f6` |

The template pins the component by UUID and its object revision. Its relative and external references are declarations only; they are not resolved, installed, or executed. The handoff text explicitly says not to execute it.
