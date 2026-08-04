# WinGet publishing path

Native Widget should enter WinGet after the installer is code-signed and the alpha release has
received basic external testing. The intended package identifier is:

```text
PelagMichael.NativeWidget
```

Release checklist:

1. Build `Native-Widget-Setup-vX.Y.Z-win-x64.exe` with `packaging/NativeWidget.iss`.
2. Code-sign the installer and executable.
3. Upload the immutable installer to the matching GitHub Release.
4. Install and uninstall it on a clean Windows VM.
5. Run `wingetcreate new <installer-url>` and verify publisher, version, architecture,
   silent install and uninstall behavior.
6. Submit the generated manifest to `microsoft/winget-pkgs`.

Do not publish a WinGet manifest that targets a mutable `latest` URL. Every manifest must use a
versioned Release asset URL and its exact SHA-256 hash.
