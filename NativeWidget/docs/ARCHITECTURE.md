# Native Widget — Architecture

Native Widget is a lightweight Windows desktop widget application built with WPF and .NET 8. It provides small always-available windows for Calendar, Tasks, Notes, Timers, Focus, Projects and translation workflows.

This document intentionally describes the reusable architecture without recording machine-specific paths, account history, private workspace details, production credentials, or user data.

## Stack

- .NET 8 / WPF (`net8.0-windows`)
- Plain code-behind; no MVVM framework
- Google OAuth implemented with Authorization Code + PKCE and a loopback redirect
- No credentials are compiled into the application
- Per-user application data is stored under `%AppData%\NativeWidget`

## Project layout

```text
NativeWidget/
  App.xaml(.cs)
  MainWindow.xaml(.cs)
  CalendarWindow*
  TasksWindow*
  NotesWindow*
  TimersWindow*
  FocusWindow*
  TranslationWindow*
  LabelsWindow*
  SettingsWindow*
  Models/
  Services/
```

Important service boundaries include:

- `GoogleCalendarService` / `GoogleTasksService` — Google API access
- `OAuthHelper` — PKCE + loopback OAuth flow
- `NotionSyncService` — optional Notion note synchronisation
- `NotesService` / `VocabularyService` / timer and label services — local data persistence
- `WindowSessionService` — widget visibility and window-bounds persistence
- `AutoStartService` — per-user Windows startup registration
- `WindowInterop` — Win32 window behaviour

## Window model

Each widget is an independent top-level WPF `Window`. The launcher owns references to singleton-style widget windows and toggles them with `Show()` / `Hide()` so state can remain alive for the process lifetime.

The launcher itself stays compact and opens a radial menu. Widget windows can be pinned, made click-through, hidden from Alt-Tab, and restored through application-level controls. Window bounds are clamped to the current virtual desktop during session restore so monitor changes do not leave windows inaccessible.

## Local data boundary

Runtime data belongs outside the repository. The default data directory is:

```text
%AppData%\NativeWidget
```

Examples include configuration, OAuth tokens, notes, timers, labels, translations and session state. These files must never be copied into source control, release archives, bug reports or screenshots without deliberate redaction.

The repository `.gitignore` separately excludes common credential files, private keys, local integration tokens, build output and local experiments.

## Google integration

Calendar and Tasks share the Google OAuth flow. Authentication uses PKCE with a loopback redirect. OAuth token state is cached locally under the application-data directory and refreshed when required.

The application does not contain a built-in Google client secret. Any client credentials supplied by a user are runtime configuration and must remain local.

Calendar and Tasks network operations use bounded retries for transient failures and avoid overlapping refresh operations. Invalid or revoked authorization is treated separately from transient network failure so the UI can request reconnection rather than retry indefinitely.

## Notes and optional Notion sync

Notes are stored locally, with an index plus one content file per note. Optional Notion synchronisation is disabled by default and uses a runtime integration token supplied by the user.

Local and remote changes are reconciled conservatively. Conflicting local content is preserved rather than silently discarded, and delete propagation is intentionally restricted so a sync failure cannot cascade into destructive data loss.

Notion credentials and database identifiers are runtime data, not repository configuration.

Database identity is retained across API/network/schema failures: these stop the sync pass
instead of creating another table. With no saved identity, the service first discovers
existing `NativeWidget Notes` child databases under the configured parent. Multiple matches
require an explicit Database ID in Settings. Newly created database IDs are saved before
follow-up requests. Equivalent hyphenated and compact IDs do not reset the selected table.

Note/index and settings saves use a flushed temporary file plus atomic replacement with a
rolling `.bak`. NotesService serializes index read/modify/write transactions. Unreadable or
missing indexes with surviving note files stop saves rather than returning an empty list.
Every differing remote body pull preserves the local Markdown in a uniquely named
`.conflict-*.md` file; a stale pull cannot replace a local body saved during network I/O.
Nested/unsupported remote bodies are not allowed to overwrite existing local notes, and
parent blocks containing children are never archived by the flat converter.

Long code blocks are split into bounded rich-text arrays and carry continuation captions
so importing them joins the exact code content without Markdown escaping or extra newlines.
Children requests are batched by both block count and encoded JSON size. A pending-upload
marker is saved before page creation; if a multi-request upload fails, the next pass finishes
pushing the full local body rather than pulling an incomplete page over it.

`NativeWidget.RoundTripTests --notion-payload-checks [markdown paths...]` checks payload
limits offline. The explicit `--sync-configured-once` diagnostic uses the real configured
Notion integration and writes sync changes; it refuses to run while NativeWidget is open.

## Folder search

`WorkspaceSearchWindow` shares its search UI with a Folders mode. `FolderSearchService`
stores roots and its bounded directory index in `folder-search.json` under the data folder.
Traversal runs off the UI thread, breadth first with round-robin progress across configured
roots. It skips inaccessible directories, junctions, virtual environments, cache, dependency
and build directories, and stops at 100,000 entries with an explicit partial-index status.
Overlapping roots are pruned. Versioned cache schemas invalidate older incomplete indexes.
Refresh can be cancelled; changing roots
immediately prunes excluded cached results and invalidates older refreshes. Search matches
are ranked by exact name, name prefix, name substring, then path, with up to 60 rows shown.
Selecting a folder exposes copy-path, copy-CMD and Explorer actions. `Down` moves from input
to results; `Enter` opens the selection, `Ctrl+C` copies its path, and `Ctrl+Shift+C` copies
the CMD command when the search text box is not focused.

## Tasks, labels and projects

Google Tasks data remains in Google. Local labels, colours and project associations are stored in small local side tables so those UI features do not mutate unrelated Google fields.

Projects are local metadata. Paths selected by a user are runtime data and must never be hard-coded into source or documentation.

## Timers and focus

Persistent timers store absolute deadlines so they remain correct while the process or machine is offline. The application-wide notifier checks expired timers and records whether each notification has already fired.

The Focus widget is intentionally session-local and does not persist a running countdown.

## Translation and OCR

Translation is isolated behind `TranslationService`, allowing the provider implementation to change without affecting capture and UI code. Selection capture restores the previous clipboard contents and rejects obvious password-field captures. OCR uses Windows APIs after an explicit region selection.

Saved vocabulary is local user data and is not part of the repository or release package.

## Security and privacy rules

1. **Never hard-code credentials.** API keys, OAuth client secrets, access/refresh tokens and integration tokens are runtime-only data.
2. **Never commit runtime state.** `%AppData%\NativeWidget`, browser/session data, notes and token files stay outside Git.
3. **Keep public documentation generic.** Do not record real usernames, home-directory paths, email addresses, account identifiers, private project names or personal migration history.
4. **Minimise OAuth scopes.** Request only scopes required by the enabled features.
5. **Treat local tokens as sensitive.** UI fields for secrets are masked; logs and exception messages must not print credential values.
6. **Prefer fail-safe sync behaviour.** Conflicts preserve data; transient network failures do not clear already-rendered content.
7. **Release from a clean checkout.** Public artifacts are produced from repository source, not from a developer's runtime/data directory.

## Build and release

Development:

```bash
cd NativeWidget
dotnet build
dotnet run
```

A normal framework-dependent publish can be created outside the source tree:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o <publish-dir>
```

Public releases use `scripts/package-release.ps1 -Version X.Y.Z`. The release workflow builds from a clean GitHub Actions checkout, creates framework-dependent and self-contained archives, and publishes SHA-256 checksums.

Release packaging must include only application binaries plus explicitly selected public documentation/license files. Runtime configuration and `%AppData%` content are not part of a release.

## Maintenance checklist

After changes affecting integrations, persistence or release packaging:

- build and run the relevant tests;
- confirm no credential or runtime-data file is staged;
- review `git diff --cached` before pushing;
- verify OAuth/token values do not appear in logs;
- verify release archives are produced from a clean checkout;
- keep examples and docs on placeholder values and generic paths only.
