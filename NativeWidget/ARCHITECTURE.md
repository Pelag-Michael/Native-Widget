# Widgets — Context & Architecture

Native Windows desktop widget app (WPF, .NET 8) replacing an earlier Electron prototype
(too heavy, ~300-400MB RAM). Goal: lightweight floating widgets, always-on-top, no taskbar
icon, launched from a small hover-expand dock.

## Stack

- **.NET 8 / WPF** (`net8.0-windows`), framework-dependent (not self-contained) — relies on
  the `Microsoft.WindowsDesktop.App 8.0` shared runtime already on the machine. Baseline RAM
  ~100MB per running widget window.
- No third-party UI libs. No MVVM framework — plain code-behind, kept small on purpose.
- OAuth (Google) implemented by hand (PKCE + loopback `HttpListener`), no Google SDK.

## Project layout

```
NativeWidget/
  App.xaml(.cs)          Global styles/resources (colors, button/input/list/combo/scrollbar/
                          DatePicker templates) + TimerNotifier startup hook
  MainWindow.xaml(.cs)    The launcher: small hover-expand dock, global finder, spawns/toggles windows
  WorkspaceSearchWindow   Local search across notes, labels and projects
  CalendarWindow          Google Calendar widget (view-only, OAuth)
  NotesWindow              Multi-note rich-text widget (list view + editor)
  TimersWindow             Multiple persistent countdown timers / deadlines
  FocusWindow              Pomodoro-style focus timer widget
  SettingsWindow           Google OAuth credentials + auto-start toggle
  PromptDialog             Small themed "enter a value" modal, used for renaming
  Models/AppConfig.cs      User-editable settings, persisted to %AppData%\NativeWidget\config.json
  Services/
    GoogleCalendarService.cs  OAuth flow + Calendar API calls
    OAuthHelper.cs            Shared PKCE + loopback-redirect helper (reused if more OAuth added)
    NotesService.cs           Multi-note index + per-note Markdown files, XAML migration
    ItemTagsService.cs        Local free-form labels for Google-backed task/calendar items
    TimersService.cs          Countdown timer persistence (absolute deadlines)
    TimerNotifier.cs          App-wide watcher that announces finished timers exactly once
    AutoStartService.cs       HKCU Run-key toggle for "start with Windows"
    WindowInterop.cs          Win32 interop: hide from Alt-Tab, toggle always-on-top (pin)
  icon.ico                Generated app icon (see "Icon" below)
```

## Architecture: launcher + independent widget windows

Each widget (Calendar, Notes, Focus, Settings) is its **own top-level `Window`**, not a tab.
`MainWindow` (the launcher) holds nullable references and lazily constructs each window on
first click, then just toggles `Show()`/`Hide()` afterward — state and position persist for
the process lifetime. This was a deliberate pivot from an earlier single-window-with-tabs
design, so the user can have several widgets visible on screen simultaneously.

Every widget window:
- `WindowStyle="None"`, `AllowsTransparency="True"`, `Topmost="True"`, `ShowInTaskbar="False"`
- Calls `WindowInterop.HideFromAltTab(this)` in its constructor (sets `WS_EX_TOOLWINDOW`)
- Has a pin button in its header calling `WindowInterop.TogglePin` — pinned (always-on-top)
  is the default; unpinning lets other windows cover it
- Overrides `Closing` to `e.Cancel = true; Hide();` — the ✕ button hides, it never actually
  closes/disposes the window (so reopening from the launcher is instant, state intact)
- Root `Border` uses `CornerRadius="18"` + `ClipToBounds="True"` consistently — don't let
  child content (icons, hosted controls) overflow past the rounded corner, it looks broken
- Drag-to-move via a `MouseLeftButtonDown` handler calling `DragMove()` on a header `Grid`

`MainWindow` itself: fixed `CollapsedWidth`/`ExpandedWidth`, animates `Width` on
`MouseEnter`/`MouseLeave` to reveal/hide the icon row (`IconsPanel`). If you add/remove a
launcher icon, **recompute `ExpandedWidth`** — see the comment math in `MainWindow.xaml.cs`;
mismatches cause icons to overflow the rounded corner (this happened once, looked like a
"cut corner" bug).

Three global hotkeys (`RegisterHotKey`, all `Ctrl+Alt+<key>`, handled in `HotkeyHook` off
`WM_HOTKEY`): `Ctrl+Alt+G` un-ghosts every widget (the only way back once one is
click-through, since a ghosted window can't be clicked at all — not even its own un-ghost
button). `Ctrl+Alt+F` ("Find") pulses a blue glow (`DropShadowEffect` on `RootBorder`,
animated `Opacity`, 3 pulses) and briefly pops the icon row open, so the launcher — a single
small floating icon, easy to lose behind other windows — is unmistakable. This is a
deliberate hotkey, **not** a real Alt+Tab hook: every widget calls
`WindowInterop.HideFromAltTab`, so there's no reliable way to intercept the actual Alt+Tab
  keystroke without a global low-level keyboard hook fighting Windows' own switcher.
`Ctrl+Alt+K` opens `WorkspaceSearchWindow`, which searches local notes (title, preview, label,
assigned-project name), labels and projects. Selecting a note opens its editor; a project makes
it the current focus project; a note label filters the Notes list. The launcher itself remains a
52px circular drag handle; hover opens a separate circular `Popup` whose function icons orbit a
matching launcher icon in the center. Keep the fixed menu size and canvas positions in sync if
another launcher action is added.

## Styling (all in App.xaml, global)

- Dark, neutral theme (`PanelBg`, `MutedBrush`, `ChipBg`) — **no branded accent color on
  chrome/icons**, only `AccentBrush` (blue) on primary action buttons (Connect, Load, Play).
  This was an explicit correction after a first pass used a purple-blue gradient everywhere.
- Icons are **Segoe MDL2 Assets glyphs** (`&#xE787;` etc.), not emoji — emoji render as
  fixed-color bitmap glyphs and ignore `Foreground`, which looked inconsistent/"black" on
  the dark theme. MDL2 glyphs are monochrome and pick up `Foreground` like text.
- `ScrollBar`, `ComboBox`/`ComboBoxItem` have full custom `ControlTemplate`s in `App.xaml`
  (implicit, no `x:Key`) so every `ListBox`/`RichTextBox`/`ComboBox` in the app gets the
  themed look automatically — don't restyle these per-window.
- Any `Border` hosting non-WPF-native content (there is none currently, WebView2/Notion was
  removed) needs manual `Clip` = rounded `RectangleGeometry` on `SizeChanged`; `ClipToBounds`
  alone clips to the rectangular bounds, not the rounded shape.

## Widgets

### Shared interaction rules
`IconBtnStyle` and `TabIconStyle` intentionally change from their rounded-square resting shape
to a circle on hover. Secondary row actions follow the same convention: hidden until the pointer
is over a card, so dense lists still scan cleanly.

### Calendar
Google Calendar, **2-way**: view, create (`AddEventDialog` - title, date, time or all-day,
optional RRULE recurrence), and delete. OAuth (Authorization Code + PKCE), `Web application`
client type with a fixed loopback redirect `http://127.0.0.1:42813/callback`. Tokens cached
at `%AppData%\NativeWidget\google-token.json`, refreshed transparently. Fetches events in a
**14-day date range** (not a flat top-N count — an early version used `maxResults=8/20` and
got dominated by same-day recurring "birthday" all-day events, hiding real future events).
Events are grouped by day with headers (Hôm nay / Ngày mai / weekday+date). Auto-refreshes
every 5 min while visible, and on `Activated` (so reopening the widget always shows current
data without needing to reconnect). A row's delete button sits inside a `ListBox` item that
also handles `MouseUp` to open the event's Google link - `FindAncestor<ButtonBase>` in the
`MouseUp` handler skips that open when the click actually landed on a button, since
`ButtonBase` only marks the narrower `MouseLeftButtonUp` handled, not the generic `MouseUp`
that bubbles past it.

The Calendar header exposes explicit refresh and disconnect controls plus a compact last-sync
status, instead of reserving permanent bottom-row space for disconnect. Events can carry
local-only free-form labels and one local project assignment, alongside their existing local
color; these never alter the Google Calendar event.

### Notes
Google-Keep-style: a **list of notes** (title + preview) that opens into an editor, with
back / new / rename / delete. Stored as `%AppData%\NativeWidget\notes\index.json` plus one
`<id>.md` per note. Markdown is an internal interchange format and is never shown in the
editor. Existing per-note `.xaml` files are converted once and deliberately retained as
backups; older single-note formats (`notes.xaml`, `notes.txt`) still migrate into the
first entry on first run.

A note's title is auto-derived from its first line **until** the user renames it by hand,
at which point `TitleIsCustom` pins it so saving no longer overwrites the chosen name.
List cards normalize title/preview whitespace and render each as one trimmed line; the full
body remains untouched in storage. This keeps long or newline-heavy notes from expanding a
single card until it pushes the rest of the scrollable list off-screen.

**Free-form labels** — `NoteMeta.Tags` (`List<string>`), edited via a plain comma-separated
`PromptDialog` (the tag icon on each card), rendered as small pill chips under the preview.
Project assignment reuses the launcher's Projects icon so it remains visually distinct from labels.
Independent from the project tag (`ItemProjectTagsService` — a note has at most
one project, but any number of labels). `TagFilter` combo populated from the distinct set
of tags across all notes, rebuilt on every `RenderList()`.

**Reminders** — the clock button on a card opens `ReminderDialog` (same duration-field UI
as Timers' "add" form: days/hours/minutes from now). Setting one just calls
`TimersService.Add(...)` and stores the returned `CountdownTimer.Id` in
`NoteMeta.ReminderTimerId` — the Timers widget shows it automatically since it already
lists every `CountdownTimer` with no note-specific code needed there. Re-opening the dialog
on a note that already has one shows a "Xoá nhắc" option, which deletes that timer and
clears `ReminderTimerId`.

**Notion sync (experimental, off by default)** — `NotionSyncService`, polled every 15s from
`NotesWindow`'s own timer (only the main list window polls, not pop-outs).
The editor's Save button and `Ctrl+S` share one path: save Markdown locally, wait for any
in-flight background pass, then run an immediate Notion sync with visible success/failure
feedback. The 15s timer remains for receiving remote changes.
Titles and bodies are both 2-way. A canonical Markdown SHA-256 stored in
`NoteMeta.LastSyncedHash` identifies which side changed. A clean open editor follows remote
changes on the next polling cycle; a dirty editor is excluded from that pass so its in-memory
draft is not overwritten. If both sides changed since the shared hash, the Notion version stays
authoritative and the local version is retained as `<id>.conflict-<timestamp>.md` instead of
silently replacing either user's work.

The note body lives as real Notion blocks. Supported mappings are paragraph, heading 1/2,
bulleted/numbered list item, to-do, quote, code, bold/italic/strikethrough rich-text,
image, and generic file attachments. Any regular file up to Notion's 20 MB single-part limit
can be added through the attachment button, Explorer paste, or drag-and-drop. Attachments are
copied under `notes\attachments\<note-id>`, represented as `📎` Markdown hyperlinks, opened
with the OS default application, and synced as Notion file blocks. Local files use Notion's
file-upload API; pulled remote images/files are downloaded locally so temporary signed URLs
never become the source of truth. Replacing a body appends the new supported blocks first,
then archives only old supported blocks. Unsupported blocks such as toggles/embeds remain
untouched, so a failed request can produce duplicates but cannot empty the page. **No
delete propagation either direction** remains the safety rule.

The service targets Notion API `2026-03-11`: `NotionDatabaseId` identifies the database
container and `NotionDataSourceId` its queryable data source. Existing database IDs are
upgraded by discovering and caching their first data source automatically. Only `Title`
and `LocalId` are data-source properties.

Mapping: a `LocalId` rich_text property on each Notion page stores the local note's ID
directly — no separate lookup table. A page created straight in Notion (empty `LocalId`)
becomes a new local note reusing the Notion page ID as the local ID, then that same ID gets
written back to `LocalId` before the pass ends — skipping that write-back would make next
pass treat it as still-unmapped and create a duplicate page every 15s.

**Gotcha already hit once**: `JsonContent.Create(anonymousObject)` silently lowercases the
first letter of every C# property name (`Title` → `title`), which is harmless for the
snake_case Notion API fields (already lowercase in the C# source) but corrupted the custom
database property names, making every `GetProperty("Title")` throw `KeyNotFoundException`
on every sync pass with no visible symptom until a diagnostic log was added. Fixed by
passing `JsonSerializerOptions { PropertyNamingPolicy = null }` to every `JsonContent.Create`
call in this service (see `NotionSyncService.JsonOptions`).

Was prototyped first as a **fully separate, disposable experiment** (`notion-sync-experiment/`
at the repo root, plain Python scripts hitting the Notion API directly, its own token in a
local `.env` — never touched `NativeWidget/` or `app/`) specifically to validate the
integration-token auth flow and a full CRUD round trip before writing any C#. Once that
confirmed feasible, the experiment folder's test database/page were archived and only then
did the real feature (this section) get built into the app.

The editor is `RichTextBox`-based, not a plain `TextBox` — supports font family (3 fixed
choices: serif/sans/mono), font size and bold/italic/strikethrough applied to the current
selection. A wrapping toolbar also applies heading 1/2, bullet/number list, interactive
checkbox, quote and code styles to the selected paragraphs or caret paragraph. The block
shortcuts are `Ctrl+Alt+1/2`, `Ctrl+Shift+8/7/9/Q/C`; `Ctrl+S` saves and syncs; no Markdown
syntax is exposed. Link detection accepts `http(s)`, `www`, and bare domains, adds HTTPS when
the scheme is omitted, and runs after Space/Enter plus load/save. A stored text offset restores
the caret after a Run is split. A normal click opens the hyperlink or attachment through the
Windows shell; Ctrl+click is not required inside the editable document.

### Projects
Minimal "what am I focused on" tracker for the user's startup-style side projects, some
with a code repo/folder, some not. One project is `CurrentId` — shown big, bold, with an
accent-colored bar (via the shared `ColorTagButton`) and, if it has a folder, a clickable
path that opens it in Explorer (`Process.Start` with `UseShellExecute=true` on the
directory). Every other project sits in a compact list below; clicking one promotes it to
current. `ProjectEditDialog` (add/edit) uses `Microsoft.Win32.OpenFolderDialog` — a native
.NET 8 WPF API, no `System.Windows.Forms` dependency needed. Persisted to
`%AppData%\NativeWidget\projects.json`.

Tasks and Notes can each tag an individual item with a project — `ItemProjectTagsService`
(`item-project-tags.json`, key `"task:<id>"`/`"note:<id>"` → project ID) is a **local-only**
side table, not a structural link into Google Tasks or the notes store. A per-item tag
button (`ProjectPickerDialog`) sets it; a small `ProjectFilter` combo in each widget filters
by it. Deleting a project does not clean up its tags — a filter for a deleted project's ID
would just show nothing, harmlessly.

### Tasks
Tasks support free-form local labels through `ItemTagsService` (`item-tags.json`, key
`"task:<id>"` to a label list). Calendar events use the same service with `"event:<id>"`, and
both widgets use the same tag icon and comma-separated `PromptDialog` editing as Notes.
Calendar event project assignments are also kept in the existing local `ItemProjectTagsService`
map, so neither enhancement changes a Google record.

Google Tasks, **2-way** (unlike Calendar, which is view-only): add, check off, delete, and
subtasks nest one level deep via Google's `parent` field. Shares `google-token.json` with
Calendar — `GoogleCalendarService.Scope` requests both `calendar.readonly` and `tasks` in
the same consent, so connecting Calendar once is enough; `GoogleTasksService` never runs
its own OAuth flow. A `ComboBox` switches between the user's Google Tasklists.

Deleting a parent with subtasks prompts to delete the children too (cascade), rather than
leaving them orphaned with a dangling `parent` reference.

Subtasks can be collapsed per-parent (`_collapsedParents`, a `HashSet<string>` of task IDs
kept in memory only — not persisted) — the chevron re-renders from the last-fetched task
list (`_lastRenderedTasks`) instead of re-fetching from Google.

Tasks can carry a **due date** (`GoogleTasksService.SetDueDateAsync`) shown as a day-count
under the title ("còn N ngày" / "quá hạn N ngày"). Google Tasks only stores a date, never a
time of day, so the countdown is always in whole days.

Each Google Tasklist can be **tinted**: `TaskListColorsService` stores listId→hex locally
(Tasks has no native per-list color field), and `TasksWindow` blends that color into the
panel's base dark background at low opacity (`TintedPanelBg`) — full swatch strength would
be unreadable as a background.

Multiple Tasks windows can be open at once, each locked to a different list — mirrors
`NotesWindow`'s pop-out pattern: `TasksWindow(config, lockedListId)` disables the list
picker and makes `Window_Closing` close for real instead of hiding, since a pop-out instance
isn't tracked by the launcher's singleton reference.

This replaced an earlier, abandoned attempt at Microsoft To Do integration (see "Removed
features"). The full existing Microsoft To Do content (~370 pending tasks/subtasks across
15 lists) was migrated over in one session — not via the Graph API (still blocked), but by
reading Microsoft To Do's own client-side IndexedDB cache (`todo_886dffb0d6189fb7`, object
stores `lists`/`tasks`/`steps`) directly through the browser tool's `javascript_exec`, then
replaying it into Google Tasks via its REST API from a throwaway PowerShell script (not
part of this repo). Two things worth remembering if this is ever needed again:
- Google Tasks' default per-user write quota gets hit fast if requests aren't throttled -
  space calls out (~150ms) and retry 403/429 with backoff, or bulk inserts silently start
  failing partway through.
- A subtask whose parent task was already marked complete can't be migrated as a child
  of nothing - either skip it or (what was done here) recreate the completed parent too,
  purely as a container, so the nesting still matches the original structure.

### Timers
Multiple named countdowns, created either as a **duration** (days/hours/minutes) or as an
**exact deadline** (date picker + HH:mm) — the toggle button beside the title field swaps
the two input modes. Each timer stores an **absolute `EndsAtUnix` deadline**, never a
"seconds remaining" counter; that is the whole reason a timer keeps counting correctly
while the app is closed or the machine is powered off. Persisted to
`%AppData%\NativeWidget\timers.json`.

`TimerNotifier` (started from `App.OnStartup`, so it runs even when the Timers window is
closed) polls every 5s for timers that are expired and not yet `Notified`, announces them,
then flips the flag so each fires exactly once. It also does a startup sweep 2s after
launch that reports **how overdue** each one is ("kết thúc 3 giờ 20 phút trước") — that
branch is what covers timers that ran out while the machine was off.

The Timers add form offers 5m/15m/25m/1h presets. Cards highlight the next live deadline and
show elapsed progress derived from `Remaining / DurationSeconds`; no new timestamp is stored,
so existing timer files remain compatible.

### Focus
Simple Pomodoro-style countdown, separate from Timers (in-session focus, not a deadline).
`DispatcherTimer`, editable minutes field plus 5-min step buttons, 25m/50m/90m presets,
Play/Pause and a compact elapsed-progress bar. No
persistence — resets each session by design.

### Settings
The Google client secret and Notion token use masked `PasswordBox` fields. Compact badges show
whether Google is connected and whether Notion sync is enabled; credentials remain stored in
the existing `AppConfig` file after the user chooses `Lưu cài đặt`.

Google OAuth Client ID/Secret input (with inline step-by-step instructions for the Google
Cloud Console flow) + `Load` (saves to `AppConfig`) + `Logout` (clears the Calendar token) +
auto-start-with-Windows checkbox (`AutoStartService`, HKCU Run key).

## Removed features (do not re-add without asking)

- **Microsoft To Do** — required an Entra ID (Azure AD) tenant for app registration; the
  user's personal Microsoft account wasn't eligible for a free dev tenant (M365 Developer
  Program declined it twice), and full Azure signup needs a credit card, which the assistant
  is not allowed to enter. Dropped entirely.
- **Notion** — was a `WebView2`-embedded iframe of a public Notion page (no OAuth; Notion's
  API doesn't let you render the real Notion UI, only raw block data). Caused the app to hang
  and the widget to vanish/crash when editing entries. Removed along with the
  `Microsoft.Web.WebView2` package dependency entirely.

## Build / run / publish

```bash
# dev loop
cd NativeWidget
dotnet build
dotnet run

# "install": publish framework-dependent next to the source, then point the Start Menu
# shortcut (WScript.Shell COM) at ..\app\NativeWidget.exe. Re-run after any change you
# want the Start Menu / auto-start entry to pick up — dotnet run's bin\Debug output is
# not what those point to.
dotnet publish -c Release -r win-x64 --self-contained false -o "..\app"
```

**Install location matters — publish to `Documents`, never `%LocalAppData%`.** Confirmed
root cause (see Troubleshooting below): only `AppData\{Local,LocalLow,Roaming}` are
virtualized for this agent; `Documents` is not. The Start Menu shortcut must point at the
`Documents`-side build, or the user keeps running a stale copy no matter what gets rebuilt.

### Icon

`icon.ico` is generated by a throwaway `System.Drawing.Common` console script (not in this
repo) that draws a black rounded square with a white 2×2 rounded-square grid, then
hand-assembles a multi-resolution ICO (16/24/32/48/64/128 as raw BMP DIB frames, 256 as
PNG). Regenerate the same way if the logo changes — there is no source PSD/SVG, the drawing
code *is* the source.

Two traps, both of which produced a silently-wrong icon before:
- Don't build frames via `Icon.FromHandle(bmp.GetHicon()).Save()` — it truncates the pixel
  data for the larger sizes, and Windows Shell discards the **entire** icon file if any
  frame is malformed, falling back to the default app icon. Write the DIB by hand:
  `BITMAPINFOHEADER` with `biHeight = 2 × height`, bottom-up 32bpp BGRA rows, then a
  zeroed 1bpp AND mask (4-byte-aligned rows).
- Don't rely on `Icon.ExtractAssociatedIcon` to verify — it always returns 32×32 regardless
  of what's embedded. Verify with `SHGetFileInfo` (what Explorer actually calls), or by
  loading each frame out of the `.ico` with `new Icon(path, w, h)` and sampling pixels.

## Known rough edges

- No error surfacing for OAuth/network failures beyond the connect button flipping to
  "Lỗi, thử lại" — acceptable for a personal tool, would want real error text before sharing
  this with anyone else.
- `AutoStartService` writes `Environment.ProcessPath` to the registry — only correct once
  the app is running from its **published** location; if you delete/move the publish folder,
  the Run-key entry silently points at nothing.
- Timer notifications are `MessageBox` popups, not real Windows toasts, so they steal focus
  and can't be reviewed in Action Center after dismissal.

## Data & disk footprint

- **User data** (config, notes, timers, OAuth token): `%AppData%\NativeWidget` — a few
  hundred KB, all JSON/XAML text plus any pasted note images.
- **Runnable app** (what the Start Menu shortcut and autostart both point to):
  `Documents\Agent antigrav\desktop shit\app\` — ~0.6MB, framework-dependent (relies on
  the shared `Microsoft.WindowsDesktop.App 8.0` runtime already on the machine, not
  bundled).
- **Source + build cache**: `Documents\Agent antigrav\desktop shit\NativeWidget\` —
  ~140MB, almost entirely disposable `bin/`/`obj/` output from repeated Debug/Release
  builds. Safe to delete `bin` and `obj` any time; `dotnet build` regenerates them.
- Single-instance is enforced with a named `Mutex` in `App.OnStartup` — a second launch
  just brings the existing launcher window forward (via `FindWindow` on its "Widgets"
  title) and exits instead of spawning a duplicate process.

## Widget window resizing
`WindowStyle="None"` windows can normally only be resized via a visible corner grip
(`ResizeMode="CanResizeWithGrip"`) - there's no native border to grab. Calendar, Tasks,
Notes, Timers, Projects and Settings instead use `System.Windows.Shell.WindowChrome`
(`CaptionHeight="0"`, `ResizeBorderThickness="6"`, `GlassFrameThickness="0"`) with
`ResizeMode="CanResize"`, which restores real edge/corner hit-testing without needing a
native title bar. Focus was left on `NoResize` - it's a fixed small form with no list
content to grow into.

## Troubleshooting (symptom → cause → fix)

**"A button in a code-behind-built row went invisible / has no icon."**
Cause: the icon glyphs are Segoe MDL2 private-use codepoints (`U+E700`–`U+F8FF`). Pasted
into a `.cs` file as *raw characters* they survive on disk but are invisible in every tool
that displays the file, so any later rewrite of that file silently drops them and leaves
`Content = ""` — a button that renders as an empty gap. Fix: always write them as C#
escapes (`""`), never as literal characters. Sweep for regressions with:
`python -c "s=open(f,encoding='utf-8').read(); print([hex(ord(c)) for c in s if 0xE000<=ord(c)<=0xF8FF])"`

**"Vietnamese task titles show as `Ã¡o dÃ i` / `giáº¥y tá»`."**
Cause: UTF-8 bytes were decoded as a single-byte codepage somewhere in the migration
pipeline (PowerShell's `Get-Content` without `-Encoding utf8` is the usual culprit) and then
re-encoded as UTF-8. Fix: reverse it per character — `ch.encode('cp1252')`, falling back to
`bytes([ord(ch)])` for the bytes cp1252 leaves undefined (`0x81 0x8D 0x8F 0x90 0x9D`, which
is why a plain `s.encode('cp1252')` finds *zero* matches and looks like a false all-clear) —
then `.decode('utf-8')`; PATCH each task's `title` back. A title that fails to round-trip is
already correct and must be left alone. 194 of 895 titles were repaired this way.

**"I changed X but the app still behaves like before."**
Cause: build/publish output written to a path this agent's sandbox silently redirects
(`%AppData%\Local|LocalLow|Roaming\...` → `…\Packages\Claude_<id>\LocalCache\...`), while
the user's real Start Menu shortcut points at the true, un-redirected path — so the two
stop being the same file. `Documents\...` is *not* redirected, which is why the app is
published there (see "Build / run / publish" above), never to `%LocalAppData%`.
Confirmed via `Get-Item` timestamp/hash comparison on both path aliases, and by having the
user check the running app's own UI state (e.g. Settings fields) rather than trusting
anything this agent read back from disk. **If in doubt, verify through the user's eyes, not
the agent's filesystem view.**

**"Settings shows empty Client ID/Secret even though I saved them before."**
Two possible causes, both seen in practice:
1. A stale `NativeWidget.exe` process is still running from before the credentials were
   saved (each window's `AppConfig` is loaded once at `MainWindow` startup and shared by
   reference — it never re-reads the file). Fix: fully kill the process, relaunch.
2. The sandbox path issue above, if the fix was applied by the agent writing files
   directly rather than by the user's own running app calling `AppConfig.Save()`. Fix: the
   user re-enters credentials once, through the real UI — that write goes through their
   real process to their real file, resolving it permanently.

**"Icon isn't showing in the Start Menu / search results" (generic file/page icon).**
Checklist, roughly in the order this was actually debugged:
1. Confirm the icon is genuinely embedded: `SHGetFileInfo` on the exe (P/Invoke), *not*
   `Icon.ExtractAssociatedIcon` (always reports 32×32 regardless of what's really there —
   a false negative/positive trap). If `SHGetFileInfo` shows the right image, the exe is
   fine and the problem is shell-side caching or a stale build (see path issue above).
2. If the icon is genuinely wrong/missing even via `SHGetFileInfo`: inspect the raw `.ico`
   bytes. `Icon.FromHandle(bmp.GetHicon()).Save()` silently truncates pixel data for larger
   frames; Windows Shell then discards the *whole file* (not just that frame) and falls
   back to a generic icon. Diagnose by reading each `ICONDIRENTRY`'s declared width/height
   against the actual `BITMAPINFOHEADER` dimensions inside its data blob — a mismatch or a
   `dataLen` far shorter than `width×height×4` bytes means a broken frame. Fix by
   hand-writing the DIB (see "Icon" section above) instead of trusting `Icon.Save()`.
3. Once the file itself is verified correct, Windows' Start Menu/search still cache icons
   per-shortcut. `ie4uinit.exe -ClearIconCache` + deleting `iconcache*.db` + restarting
   `explorer.exe` clears most of it; if that's not enough (and the sandbox blocks deleting
   `StartMenuExperienceHost`'s own cache folders directly), recreate the `.lnk` under a
   **new filename** pointing at a **new filename** for the icon — Windows caches by path
   identity, so a fresh name forces a re-read. A full user reboot is the last-resort fix
   for anything this can't reach.

**"Auto-start with Windows" is checked but nothing launches on reboot.**
The install path contains spaces (`Documents\Agent antigrav\desktop shit\app\...`), and
Windows splits a Registry Run-key value on its first space when launching it unless the
value is quoted — `AutoStartService.SetEnabled` writes the path wrapped in `"..."` for
exactly this reason. If the checkbox was ticked before that fix landed, the stale
unquoted value silently fails forever (no error, no log — it just never runs). Re-toggle
the checkbox off/on once to rewrite it, or fix the registry value directly.

**"Two copies open after a reboot — one with an old UI."**
`AutoStartService` writes whatever `Environment.ProcessPath` was *at the moment the
checkbox was last ticked* into the registry Run key — it does not track later moves. If
the app was published to a different folder since then (see the path-redirection entry
above), the Run key silently keeps launching the stale copy from the old location, while
the Start Menu shortcut launches the current one — two different builds, two processes.
Fix: `Set-ItemProperty` the Run key to the current exe path, delete the stale build
folder, and (now that single-instance is enforced) relaunch once to confirm only one
process exists.

**"A widget opens completely blank — no data, no error message."**
Suspect overlapping async loads racing each other, not a failed API call. `TasksWindow`
hit this: `MainWindow.ToggleWidget_Click` calls `Refresh()` *before* `Show()`, so `Loaded`
(and `Activated`, if hooked) fire on top of an already-running load; each one called
`ListSelect.Items.Clear()`, and **`Items.Clear()` itself raises `SelectionChanged`**, which
re-entered the load again — leaving the combo and list empty with nothing thrown. Fix is a
pair of guards: an `_isBusy` flag so only one load runs at a time, and a
`_suppressSelectionChanged` flag held while repopulating the combo.

Note this class of bug will **not** reproduce in a test that calls `Show()` then
`Refresh()` sequentially — the test has to mirror the launcher's real order
(`Refresh()` → `Show()` → `Activate()`) to trigger the overlap.

**"Google Calendar shows an event but I marked it done in Google Calendar / it's a Task."**
Not a bug: Calendar *Events* have no completion concept in the API at all — only Google
*Tasks* (a separate API/resource) do. Some Task-backed items appear in the Calendar UI as a
synthetic `eventType: "focusTime"` entry; you can confirm this by checking the event's
`description` field, which literally says "please go to tasks.google.com/task/…" when
that's the case. `GoogleCalendarService` only reads the Events API — it cannot see Task
completion, and the user explicitly declined adding Google Tasks as a second integration.

**"Calendar list looks dominated by a few days / duplicate recurring events, later days are missing."**
Don't fetch by `maxResults` count — early versions did, and got flooded by same-day
recurring all-day events (e.g. contact "birthday" reminders that all happened to recur on
the same date), starving out real upcoming events. Fetch by an explicit `timeMin`/`timeMax`
**date range** instead (currently 14 days) and let the range, not a count, bound the result.
