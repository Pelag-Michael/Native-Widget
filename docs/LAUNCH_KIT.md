# Native Widget launch kit

Use the screenshots in `docs/assets/`. Replace bracketed placeholders only after checking the
latest Release and benchmark numbers. Do not post the same text to every community.

## Core positioning

**One line:** A native, low-RAM Windows widget dock built with WPF and .NET 8 — no Electron.

**Short pitch:** Native Widget puts Calendar, Tasks, Notes, Timers, Focus, OCR translation and
Projects one hover away in small always-on-top windows. Local widgets need no account; Google and
Notion integrations are optional.

**Proof points:**

- WPF / .NET 8, Windows 10/11
- 25.3 MB framework-dependent publish
- 96.2 MB idle working set / 44.8 MB private memory on the development machine
- MIT licensed
- Portable Windows download with SHA-256 checksums

Always describe footprint numbers as one-machine measurements, not universal guarantees.

## Show HN

**Title**

```text
Show HN: Native Widget – a low-RAM Windows widget dock in WPF/.NET 8
```

**Post**

```text
I built Native Widget after an Electron prototype for a few floating panels used roughly
300–400 MB of RAM.

The rewrite is plain WPF on .NET 8: a hover-expand launcher for Calendar, Google Tasks, Notes,
Timers, Focus, OCR/selection translation, and Projects. It uses separate top-level windows,
hand-written Google OAuth PKCE, optional Notion sync, and no third-party UI framework.

On my development machine the idle launcher measures about 96 MB working set / 45 MB private
memory. The framework-dependent publish is 25 MB. These are reference measurements, not a claim
that every configuration will match them.

The first alpha and source are here:
https://github.com/Pelag-Michael/Native-Widget

I would especially value feedback on WPF architecture, onboarding, accessibility, and which
widget is actually useful enough to keep always available.
```

## Reddit — r/dotnet / r/csharp

Check each community's current self-promotion and show-off rules before posting.

**Title**

```text
I replaced my Electron widget prototype with plain WPF/.NET 8
```

**Body**

```text
I wanted Calendar, Tasks, Notes, timers and translation in small always-on-top panels, but my
first Electron version felt excessive for the job. I rewrote it as Native Widget using plain WPF
code-behind and small services—no MVVM framework, Google SDK or third-party UI kit.

The interesting parts were hand-written OAuth PKCE with a loopback listener, absolute-deadline
timers, Markdown/FlowDocument round trips, Notion block conversion, low-level selection capture,
Windows OCR, and keeping many top-level windows out of Alt+Tab.

Measured on my machine: 96 MB idle working set, 45 MB private memory, and a 25 MB
framework-dependent publish. Source and an alpha Windows build:
https://github.com/Pelag-Michael/Native-Widget

I would appreciate concrete feedback on the architecture and where the WPF implementation can be
simplified further.
```

## Reddit — r/WindowsApps / r/productivity

**Title**

```text
Native Widget: Calendar, Tasks, Notes and OCR translation one hover away
```

**Body**

```text
I built a small open-source Windows dock because I wanted my daily tools available without a full
dashboard or another Electron window.

The launcher expands on hover and opens separate always-on-top widgets for Google Calendar/Tasks,
Notes, timers, focus sessions, projects and screen/selection translation. Local features need no
account; online integrations are optional.

It is an early alpha, so I am looking for honest onboarding and usability feedback:
https://github.com/Pelag-Michael/Native-Widget
```

## Product Hunt

**Tagline (60 characters)**

```text
Native Windows widgets, one hover away
```

**Short description**

```text
A low-RAM WPF dock for Calendar, Tasks, Notes, timers, focus, projects and OCR translation—without Electron.
```

**Maker comment outline**

1. The pain: a few utility panels should not require a browser runtime.
2. The decision: Windows-first WPF rather than cross-platform Electron.
3. What works today: show three concrete daily workflows.
4. What is intentionally optional: Google/Notion accounts.
5. Ask for feedback on onboarding and the most valuable widget.

Do not launch on Product Hunt until there is a signed installer, a short demo video, and at least
five external testers; early votes cannot compensate for a rough first-run experience.

## Vietnamese communities — J2TEAM / C# .NET Việt Nam / Voz

```text
Mình vừa public Native Widget — một dock widget native cho Windows viết bằng WPF/.NET 8.

Lý do mình làm là bản prototype Electron dùng khoảng 300–400 MB RAM dù chỉ phục vụ vài panel nhỏ.
Bản WPF hiện có Calendar, Google Tasks, Notes, timer, Pomodoro, project tracker và dịch bằng vùng
chọn/OCR. Khi không dùng, panel Translate thu lại thành một thanh nhỏ và bung ra khi rê chuột.

Số đo tham khảo trên máy dev: khoảng 96 MB working set, 45 MB private memory; bản publish cần .NET
runtime khoảng 25 MB. Dự án MIT, có source và bản alpha tải thử tại:
https://github.com/Pelag-Michael/Native-Widget

Mình rất cần feedback thật về trải nghiệm cài lần đầu, UI và widget nào hữu ích nhất. Nếu gặp lỗi,
mọi người có thể mở issue kèm Windows version và bước tái hiện (nhớ che token/dữ liệu cá nhân).
```

## Launch sequence

1. Recruit 5–10 Windows testers directly; fix first-run failures.
2. Record a 20–35 second 1080p demo: hover launcher → Notes → OCR Translate → Calendar/Tasks.
3. Post the technical version to one .NET community and answer every substantive comment.
4. Incorporate feedback, cut the next release, then post to Windows/productivity communities.
5. Launch on Product Hunt only after installer signing and onboarding are ready.
6. Use a different screenshot and angle for each channel; do not cross-post simultaneously.

## Metrics to track weekly

- README views → Release page visits
- Release downloads → successful first launches
- First launch → integration connected or local widget used
- Issues per 10 downloads
- Stars from actual users, not raw impressions
- Returning contributors and accepted pull requests
