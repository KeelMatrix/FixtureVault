# FV007 Detection Grammar

This document is the single source of truth for FixtureVault's high-confidence sensitive-data (`FV007`) grammar. README, security guidance, package help, and development documentation intentionally keep only a short summary and link here.

## Representation boundaries

- Raw fixture text is inspected as written. Backslashes are literal data.
- A structurally valid JSON object, array, or string is decoded exactly once when its structural depth is at most 64 containers. Every string value, nested value, and array item is then inspected independently. A JSON container is not allowed to suppress a sensitive sibling.
- A valid JSON representation deeper than 64 containers is an incomplete sensitive-data inspection: FixtureVault reports `FV-E014`, exits `2`, sets `Completed=false`, and does not record successful-scan telemetry. A malformed or non-JSON bracket-prefixed fixture remains eligible for raw-text inspection; it is not treated as a depth failure.
- URL query fields end at raw `&`, `#`, carriage-return, or line-feed boundaries before one URL decode. Encoded delimiters remain part of the field value after decoding.
- Diagnostics identify the fixture and rule only; matched values and full fixture contents are never printed.

## Credential families

FV007 recognizes these structured families:

- connection-string `Password` and `Pwd` assignments;
- Azure `AccountKey`, `SharedAccessKey`, and `SharedAccessSignature` assignments;
- API-key headers and query fields;
- Basic/Bearer authorization values;
- Cookie and Set-Cookie values; and
- generic assignments using the case-insensitive aliases `ApiKey`/`api_key`/`api-key`, `ClientSecret`/`client_secret`/`client-secret`, `Password`, `Pwd`, `Secret`, and `Token`.

Generic raw keys are either unquoted or enclosed by matching single or double quotes. Leading-only, trailing-only, and mismatched key quotes are not assignment syntax. Generic assignments accept `=` and `:`. Decoded JSON property names use the same aliases without raw quote syntax.

For a recognized JSON credential property, non-empty string, number, `true`, and `false` scalar values are sensitive. `null`, empty strings, whitespace-only strings, and the accepted finite redaction markers `***`, `<redacted>`, `[redacted]`, `redacted`, `masked`, and `removed` are clean. Objects and arrays are containers; their nested members are inspected independently.

## Ownership and boundaries

Every parser owns only the exact key/operator/value span it successfully parsed.

- Connection-string masking is a redaction boundary, not a detector switch. Only the value of a syntactically parsed connection-string indicator (`Application Name`, `Data Source`, `Database`, `Initial Catalog`, `Integrated Security`, `Server`, `User ID`, or `UID`) is protected from generic fallback. An indicator in one record never masks another record.
- Quoted connection-string values own only the value through its closing quote, including embedded `Pwd=` or `Password=` text. Cursor advancement over separators and later sibling assignments is independent, so the same text outside that owned value remains eligible for generic detection.
- Unquoted connection-string values stop at semicolons, commas, line breaks, or whitespace that begins another assignment. This preserves independently supported whitespace-separated fields. Empty values yield to a syntactically valid following sibling assignment.
- Real sibling credentials, including `;Pwd=...;`, comma-separated fields, and whitespace-separated fields, remain detectable. Unknown-key syntax, prefix text, and text after a parsed boundary remain independently eligible for generic detection.
- Clean or accepted-marker fields affect only their own spans. They never suppress later fields, lines, JSON members, or siblings.
- Azure fields use semicolon, comma, and whitespace boundaries. After an empty Azure value, `name=value` and guarded `name:` siblings are recognized; an arbitrary-name colon must be followed by whitespace or end-of-input so URI-like values remain intact. Only the three Azure credential names are classified.
- Basic/Bearer values are classified by their actual bounded value. Ordinary log prefixes and trailing metadata do not become credential data; accepted markers remain clean and genuine values remain findings.

## Safety and compatibility

Detection and redaction remain separate. FV007 does not mutate fixtures, weaken policy, or disclose matched values. The shared assignment parser advances monotonically: each unquoted whitespace run is inspected once, giving linear parser work in the fixture text rather than quadratic lookahead. Work is bounded by the scanner's documented read and diagnostic budgets, and strict policy reports a finding with exit code `1`; non-strict policy reports the same finding as a warning with exit code `0`.
