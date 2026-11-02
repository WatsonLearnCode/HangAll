# Security Policy

## Supported versions

Security fixes are provided for the latest `v1.x` release and the current
`main` branch. Older releases may not receive fixes.

## Reporting a vulnerability

Do not disclose a suspected vulnerability in a public issue or discussion.
Use GitHub's **Report a vulnerability** flow from the repository's Security tab
when private vulnerability reporting is enabled.

If that flow is unavailable, open a minimal public issue asking the maintainer
to provide a private contact channel. Do not include exploit details, secrets,
personal data or a proof-of-concept in that issue.

Please include:

- A concise description of the issue and its impact.
- The affected version or commit.
- Reproduction steps that do not contain personal data.
- Whether the issue requires local access, a malicious file, a crafted
  clipboard payload, or another prerequisite.

## Release integrity

The current release binaries are unsigned. SmartScreen warnings are expected
until a signing process is added. Always compare downloaded files with
`SHA256SUMS.txt` from the same GitHub Release before running them.

## Scope

This policy covers the HangAll source, release binaries and installer. It does
not cover Windows OCR, .NET, Inno Setup, Manrope or the upstream projects listed
in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt); report issues in those
projects through their own security processes.
