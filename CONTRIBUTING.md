# Contributing to HangAll

Thanks for helping improve HangAll. Bug reports, focused fixes, documentation
improvements and feature proposals are welcome.

## Before opening an issue

- Reproduce the problem with the latest source or the latest release.
- Search existing issues first.
- Include the Windows version, architecture, display scaling and whether OCR is
  involved.
- Remove personal paths, screenshots, captured files and clipboard contents from
  logs before pasting them into an issue.
- Report security vulnerabilities privately according to [SECURITY.md](SECURITY.md).

## Development environment

HangAll is a Windows desktop application built with C# and WPF.

- Windows 10 version 1809 or later, or Windows 11
- .NET 8 SDK
- PowerShell
- Inno Setup 7.1.0 for installer work

Build the framework-dependent single-file application:

```powershell
scripts\build.ps1 -Version 1.0.0
```

Build the installer:

```powershell
scripts\build.ps1 -Version 1.0.0 -Installer
```

Build the self-contained portable application:

```powershell
scripts\build.ps1 -Portable -Version 1.0.0
```

## Pull requests

- Keep changes focused and explain the user-visible reason for the change.
- Follow the existing C# and WPF patterns instead of introducing a new
  framework or abstraction without a clear need.
- Keep local paths, generated binaries, logs, screenshots and user data out of
  commits.
- Update documentation or notices when behavior, dependencies, installer
  languages or attribution changes.
- State which build commands and manual checks were run.
- Link the issue being addressed when one exists.

## Licensing

By contributing, you agree that your contribution is distributed under the
repository's MIT License. Do not submit code or media copied from another
project unless its license permits redistribution and the source, copyright and
license notice are added to [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
