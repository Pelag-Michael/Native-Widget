# Native routine

Open **Daily routine** in the launcher, press **Ctrl+Alt+R**, or run `NativeWidget.exe --routine`.

- **Week** opens the full weekly timetable in its own window.
- **Settings → General** controls table scale, dimming and the daily reminder. Scale fits within the display so background cards remain visible.
- **Settings → Routine** edits each weekday's time blocks and tasks. Save before changing days. Tasks repeat with the weekly template; completion state belongs to a specific date.
- **Settings → Cards** adds text or photos. Photos have a Polaroid frame, caption, frame/text color wheels and serif/handwriting font choices. Images are copied into the widget's private data folder.
- **Snooze 10m** works while the process remains open. **Dismiss**, **Start day**, and Escape close the overlay.

The reminder is off until explicitly enabled and saved. Enabling it creates a per-user Windows scheduled task named `NativeWidget Morning Routine`; disabling it removes that task. The task can start a closed widget, or hand off to the running instance. It does not power on a shut-down PC. Late startup/unlock catches up within four hours; the daily board is shown at most once per local date. Exiting the application does not disable an enabled scheduled reminder.

## Local data

Routine data and progress: `%APPDATA%\NativeWidget\routine\routine.json`, with an atomic `.bak` backup. Photos: the sibling `images` directory. Back up the entire `routine` folder.

JSON imports can use a native exported `Week` object or the generic demo's weekday-keyed schedule structure. Imports replace the weekly template, reset completion state and preserve the current local cards/settings. The application also supports a local, one-time `import-source.json` migration when no native routine file exists.

No personal schedule is embedded in the application, public demo, test fixtures or committed screenshots. The public HTML uses generic examples. Earlier repository commits contained a personal demo schedule; removing it from current files does not remove copies from Git history.
