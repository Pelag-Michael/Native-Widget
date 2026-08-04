# Contributing

Thanks for helping improve Native Widget.

## Before opening an issue

- Search existing issues first.
- Use the latest release or current `main` build.
- Remove OAuth tokens, Notion secrets, personal notes and calendar data from screenshots/logs.
- Include Windows version, .NET version and exact reproduction steps.

## Development

Requirements: Windows 10/11 and the .NET 8 SDK.

```powershell
dotnet build NativeWidget/NativeWidget.csproj -c Release
dotnet run --project NativeWidget.RoundTripTests/NativeWidget.RoundTripTests.csproj -c Release
```

Keep changes focused. Native Widget deliberately uses plain WPF and small services rather than
adding a UI framework or broad dependency for a single feature. Update `ARCHITECTURE.md` when a
change affects persistence, integrations, window behavior or troubleshooting.

## Pull requests

- Create a branch from `main`.
- Explain the user impact and why the change is needed.
- Include before/after screenshots for visual work.
- Confirm a Release build has no errors or warnings.
- Never commit files from `%AppData%\NativeWidget`, OAuth credentials or real user content.

By contributing, you agree that your contribution is licensed under the repository's MIT license.
