# Native Widget

Lightweight, always-on-top Windows widget dock (WPF / .NET 8) — Calendar, Tasks, Notes,
Timers, Focus and Projects, each in its own small floating window, launched from a
hover-expand dock. Two-way sync with Google Calendar, Google Tasks, and (experimental)
Notion.

Built as a replacement for an earlier Electron prototype that used 300–400 MB of RAM for
what should be a handful of lightweight panels.

## Widgets

| Widget | What it does |
|---|---|
| **Calendar** | Google Calendar, 2-way — view a 14-day agenda grouped by day, create events (with optional daily/weekly/monthly recurrence), delete, per-event color tags |
| **Tasks** | Google Tasks, 2-way — multiple task lists, one level of subtasks, due dates with a day countdown, collapsible subtask groups, completed tasks sink below a divider, per-list window tint, pop out a list into its own window |
| **Notes** | Multi-note rich text (font/size/bold/italic/strikethrough, pasted images, generic file attachments, auto-linkified URLs and bare domains), color tags, free-form labels, reminders that show up in the Timers widget, optional 2-way Notion sync |
| **Timers** | Named countdowns created as a duration *or* an exact deadline. Stores an absolute deadline, so a timer keeps counting while the app — or the machine — is off |
| **Focus** | Minimal Pomodoro-style focus session |
| **Projects** | "What am I focused on" tracker; one current project shown large, others in a list, each optionally linked to a folder that opens in Explorer |

Tasks and Notes can each be tagged with a project and filtered by it.

## Design notes

- **No Electron, no MVVM framework, no third-party UI kit** — plain WPF code-behind, kept
  deliberately small. ~100 MB RAM per open widget window.
- **Framework-dependent build** — relies on the `Microsoft.WindowsDesktop.App 8.0` shared
  runtime already installed on the machine, so the published output stays a few MB.
- **Google OAuth implemented by hand** (PKCE + loopback `HttpListener`), no Google SDK.
- Every widget is a separate top-level `Window`, hidden from Alt+Tab
  (`WS_EX_TOOLWINDOW`), closable to hide rather than dispose, so reopening is instant.

See [`NativeWidget/ARCHITECTURE.md`](NativeWidget/ARCHITECTURE.md) for the full
architecture write-up, including a troubleshooting section of bugs hit during development
and how they were diagnosed.

## Build

Requires the .NET 8 SDK.

```bash
cd NativeWidget
dotnet build -c Debug          # build
dotnet publish -c Release -r win-x64 --self-contained false -o ../app
```

The published app lands in `app/` (git-ignored — attach it to a Release instead of
committing binaries). Run `app/NativeWidget.exe`.

## Setup

All credentials are entered in the app's own **Settings** widget and stored in
`%AppData%\NativeWidget\` — nothing is committed to this repo.

**Google Calendar + Tasks** (one consent covers both):

1. [console.cloud.google.com](https://console.cloud.google.com) → new project → enable
   **Google Calendar API** and **Google Tasks API**
2. *OAuth consent screen* → External → add your own email under **Test users**
3. *Credentials* → Create Credentials → OAuth client ID → **Web application**
4. Authorized redirect URI: `http://127.0.0.1:42813/callback`
5. Paste Client ID + Client secret into Settings, then open the Calendar widget and
   press **Kết nối**

**Notion sync** (optional, off by default, Notes only):

1. [notion.so/my-integrations](https://www.notion.so/my-integrations) → New integration →
   **Access token** → copy the secret
2. In Notion, open the page you want the notes to live under → `•••` → **Connections** →
   add that integration
3. Paste the token + that page's ID into Settings and tick **Bật đồng bộ Notion**

The app creates a `NativeWidget Notes` database under that page and syncs every 15s.

> Notes sync both ways with headings, lists, to-dos, quotes, code, bold/italic/strike,
> images, and generic attachments up to 20 MB. Attach files with the paperclip button,
> Explorer paste, or drag-and-drop. Local files are uploaded through Notion's file-upload
> API; remote assets are cached locally. Unsupported Notion blocks are left untouched, and
> note deletion still does not propagate in either direction.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Alt+F` | Find the launcher — pulses a glow and briefly expands the icon row |
| `Ctrl+Alt+G` | Un-ghost every widget (recovery if click-through was left on) |

## License

MIT — see [LICENSE](LICENSE).
