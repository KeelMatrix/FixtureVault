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
- `FV007` classifies structured credential values independently for connection-string `Password`/`Pwd`, `AccountKey`/`SharedAccessKey`/`SharedAccessSignature`, API-key headers and URL queries, Basic/Bearer authorization, Cookie/Set-Cookie, and generic assignments. Case-insensitive generic keys are `ApiKey`/`api_key`/`api-key`, `ClientSecret`/`client_secret`/`client-secret`, `Password`, `Pwd`, `Secret`, and `Token`; raw assignment keys must be unquoted or enclosed by matching single or double quotes and may use `=` or `:`, and decoded JSON property names use the same aliases. Leading-only, trailing-only, and mismatched key quotes are not assignment syntax. Raw text preserves literal backslashes; valid JSON strings decode once. Supported decoded JSON credential properties treat non-empty string, number, `true`, and `false` scalars as sensitive unless a string is an accepted redaction marker; `null` and empty/whitespace-only strings are clean, while object and array values are containers whose nested members remain independently inspected. Query values URL-decode once before field parsing. Parsed generic fields own only their exact key/operator/value spans, leaving prefix text, unknown-key syntax, and text beyond comma or whitespace boundaries independently inspected without re-scanning classified values. Clean parsed fields never suppress later fields, lines, array items, nested objects, or sibling JSON values. Azure-style sibling assignments use semicolon, comma, and whitespace boundaries. After an empty Azure value, the shared sibling grammar recognizes syntactically valid `name=value` and guarded `name:` forms; an arbitrary-name colon must be followed by whitespace or end-of-input, so URI-like values remain intact, while only Azure credential keys are classified. Empty, whitespace-only, and finite accepted redaction markers are clean, while parsed quote/backslash data remains sensitive. Field boundaries, quoted delimiters, repeated keys, and embedded separators are handled without disclosing matched values in console or JSON diagnostics.
- `scan` treats configured roots as hard boundaries at traversal and file-open time, rechecks every queued directory through its complete current ancestor chain before and during enumeration, rejects links/reparse points and non-regular files, and fails closed when bounded reads detect replacement, growth, or shrinkage. A repository-wide path-policy traversal failure reports `FV-E015` and never emits outside filenames as `FV008` findings. Linux ARM64 uses its native open-flag and file-status ABI, covered by the package-consumer CI leg.
- Findings and skipped diagnostics share a bounded 4,096-record / 1 MiB JSON-field budget. Exhaustion returns `FV-E017` with an incomplete scan before another diagnostic is retained or serialized; it never silently truncates a report or activates successful-scan telemetry.
- Human-readable diagnostics escape control characters in untrusted repository-relative filenames; JSON retains the actual path value for machine use.
