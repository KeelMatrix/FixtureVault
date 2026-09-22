# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in FixtureVault, report it privately by either:

1. emailing **keelmatrix@gmail.com**;
2. opening a private GitHub security advisory.

Do **not** disclose vulnerabilities publicly or create a public issue containing sensitive fixture contents.

For ordinary bugs and feature requests, use the [public GitHub issue tracker](https://github.com/KeelMatrix/FixtureVault/issues). This security route is not for ordinary support or community-conduct reports.

Useful report details:

- affected package and version;
- target framework and runtime, where relevant;
- reproduction steps or proof of concept;
- impact;
- suggested mitigation, if known.

Reports are handled best-effort. Valid issues are investigated and fixed as appropriate for the currently supported release line.

## Supported Versions

Security fixes are prioritized for the latest maintained package line. Older versions may receive fixes on a case-by-case basis.

## Guidelines

- Do not post vulnerability details, credentials, personal data, or fixture contents in public repositories or issue trackers.
- Include enough safe information to reproduce and resolve the issue without disclosing secrets.
- Connection-string `Password`/`Pwd` detection honors doubled-quote escapes, JSON-escaped quoted delimiters, and serialized string boundaries, ignores empty/whitespace-only/already-redacted values, and never emits matched values in console or JSON diagnostics.
- `scan` treats configured roots as hard boundaries at traversal and file-open time, rechecks every queued directory through its complete current ancestor chain before and during enumeration, rejects links/reparse points and non-regular files, and fails closed when bounded reads detect replacement, growth, or shrinkage. A repository-wide path-policy traversal failure reports `FV-E015` and never emits outside filenames as `FV008` findings. Linux ARM64 uses its native open-flag and file-status ABI, covered by the package-consumer CI leg.
- Findings and skipped diagnostics share a bounded 4,096-record / 1 MiB JSON-field budget. Exhaustion returns `FV-E017` with an incomplete scan before another diagnostic is retained or serialized; it never silently truncates a report or activates successful-scan telemetry.
- Human-readable diagnostics escape control characters in untrusted repository-relative filenames; JSON retains the actual path value for machine use.
