# Morning routine / What to do today

Proposal only: this feature is not implemented yet.

## Daily experience

- Choose a morning reminder time, for example 06:45, and the weekdays to enable it.
- At that time, bring up one daily board over a dimmed desktop. Start NativeWidget if it is not running; reuse the existing instance if it is.
- The board contains only the selected calendar day's routine. The default view is today, not a full seven-day table.
- Left: a compact chronological timeline with start/end times and the existing WORK, GROW, PLAY, sinh hoạt and Swinburne colors. Highlight the current and next block.
- Right: “What to do today”, with checkboxes grouped under the same routine blocks. Example: Tuesday's morning Swinburne block can contain “Mang laptop” and “Đọc tài liệu trước giờ học”.
- Completed items stay checked for that date; tomorrow gets its own progress. Unfinished items do not silently migrate to another day.
- Actions: “Bắt đầu ngày”, “Nhắc lại 10 phút”, “Bỏ qua hôm nay”. Esc dismisses immediately. The normal widget remains usable after dismissing.
- Dim level and whether to dim all monitors are settings. Start with 55% black on the active monitor, without sound. Do not change Windows brightness or other apps' opacity.

## Editing panel

- Separate “Routine” panel with a weekday selector, editable time blocks and nested checklist items.
- Block fields: start, end, title, category and optional notes. Checklist items belong to a block.
- Clearly distinguish “Lặp mỗi tuần” from “Chỉ ngày này” overrides. Changing today's checklist must not accidentally rewrite the weekly template.
- Import the supplied HTML once, then store structured data independently in the widget's AppData directory. Include routine data, reminder settings and dated completion state in backups.
- Preview any weekday, with a clear “Xem thử” label; previews must not consume today's scheduled reminder.

## Scheduling behavior

- An in-app timer alone is insufficient when the process has exited. Use a Windows scheduled task to launch a dedicated `--daily-brief` entry point under the current signed-in user's session.
- Extend the single-instance handoff so this command opens the daily board in the existing process instead of merely surfacing the launcher.
- Schedule in local time. Keep a per-date shown/acknowledged marker to prevent duplicate reminders from startup, resume and scheduler triggers.
- If the reminder was missed, show it on the first unlocked session within a configurable morning catch-up window. Avoid an evening full-screen surprise.
- If the machine is locked, defer presentation until unlock. If powered off, present after login; powering on a shut-down computer is outside the feature's scope. Waking from sleep can be a separate opt-in.
- Hiding widgets and exiting the app must remain distinct. If the user enables the morning reminder, clearly explain that its scheduled task may launch the app again the next morning. Disabling the reminder removes its task.

## Source observations

Source: `C:\Users\haivo\Downloads\routine-a3-tuan.html`.

- `DEFAULT_SCHEDULE` has 45 blocks across seven weekdays, generally 06:00–23:00.
- Tuesday: Swinburne 08:00–11:30 and 13:30–16:30. Wednesday's data: Swinburne 17:00–20:30. Friday: Swinburne 10:30–12:30.
- The prose notes mention a Thursday evening class, which conflicts with Wednesday's actual schedule data. Import should flag the conflict, not silently choose a new time.
- Browser edits are saved to localStorage key `mich_routine_a3_state_v6`; reading the HTML alone imports its embedded defaults, not necessarily the currently edited browser state. Offer JSON import/export or an explicit export from the browser before migration.
- WORK/GROW blocks describe categories, not concrete tasks. The checklist editor is needed to supply actual work items; do not invent daily tasks from those labels.

## Suggested first version

Build the weekly editor, today's filtered checklist, manual preview and local persistence first. Then add the dimmed morning board and scheduled launch. AI is unnecessary for the first version.
