# Morning routine / What to do today

Original design proposal. See [native-routine.md](native-routine.md) for the implemented native controls and behavior.

## Daily experience

- Choose a morning reminder time, for example 06:45, and the weekdays to enable it.
- At that time, bring up one daily board over a dimmed desktop. Start NativeWidget if it is not running; reuse the existing instance if it is.
- The board contains only the selected calendar day's routine. The default view is today, not a full seven-day table.
- Left: a compact chronological timeline with start/end times and the existing WORK, GROW, PLAY, sinh hoạt and Swinburne colors. Highlight the current and next block.
- Right: “What to do today”, with checkboxes grouped under the same routine blocks. For example, a study block can contain “Prepare materials” and “Review notes”.
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

Personal routine data is stored only in the local application's data directory. Public demos and tests use generic examples.

- An HTML source can contain a different default schedule from its browser's edited localStorage state. Offer JSON import/export or an explicit export from the browser before migration.
- Category blocks alone are not concrete tasks. The checklist editor supplies actual work items.
- Conflicts between a source's notes and its structured times should be flagged for the user.

## Suggested first version

Build the weekly editor, today's filtered checklist, manual preview and local persistence first. Then add the dimmed morning board and scheduled launch. AI is unnecessary for the first version.
