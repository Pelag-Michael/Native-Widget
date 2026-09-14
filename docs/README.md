# Workspace documentation map

This workspace contains the tracked Native Widget repository plus several
independent local projects. Do not infer that every sibling directory belongs
to the Native Widget product or shares its release lifecycle.

## Native Widget repository

| Area | Entry point |
|---|---|
| Product overview | [`../README.md`](../README.md) |
| Architecture | [`../NativeWidget/docs/ARCHITECTURE.md`](../NativeWidget/docs/ARCHITECTURE.md) |
| Contributing | [`../CONTRIBUTING.md`](../CONTRIBUTING.md) |
| Release/launch material | [`release/LAUNCH_KIT.md`](release/LAUNCH_KIT.md) |
| CodexPro local guide | [`guides/HUONG-DAN-CODEXPRO.md`](guides/HUONG-DAN-CODEXPRO.md) |
| Packaging | [`../packaging/WINGET.md`](../packaging/WINGET.md) |

`assets/` belongs to the Native Widget public README. Guide-specific assets
live under `guides/assets/`.

## Independent local projects

| Project | Purpose | Documentation entry |
|---|---|---|
| VoiceHub | Voice interface for coding agents and Hermes | [`../VoiceHub/docs/README.md`](../VoiceHub/docs/README.md) |
| Chrome for AI | Stealth real-Chrome MCP server | [`../grok-browser-mcp/docs/README.md`](../grok-browser-mcp/docs/README.md) |
| Custom Font Generator | Browser font editor/exporter | [`../CustomFontGeneratorWebsite/docs/README.md`](../CustomFontGeneratorWebsite/docs/README.md) |
| Workspace Canvas | Native host for the Mich canvas | [`../WorkspaceCanvas/docs/README.md`](../WorkspaceCanvas/docs/README.md) |
| AI Text Humanization | Humanization skill/engine experiment | [`../AI-text-Humanization/README.md`](../AI-text-Humanization/README.md) |
| VoiceAgent | Older standalone voice-agent prototype | [`../VoiceAgent/README.md`](../VoiceAgent/README.md) |

These directories are not tracked by the parent Native Widget Git repository.
Some, such as Chrome for AI and Custom Font Generator, have their own nested
Git repositories.

## Historical and generated directories

- `app-backup-*`, `archive/`, and `legacy/` are historical snapshots. Do not
  treat them as current implementation sources.
- `dist/`, `bin/`, `obj/`, `node_modules/`, and test-result directories are
  generated outputs or dependencies.
- `app/` and `notion-sync-experiment/` are experiments, not the current Native
  Widget entry point. Modify them only when a task names them explicitly.
