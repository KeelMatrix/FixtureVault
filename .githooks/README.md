# Commit Checks

Run `git config core.hooksPath .githooks` once per clone to enable the repository's local commit checks.

The versioned checks reject identity trailers and internal metadata in new commit messages. The public repository workflow first fails closed when `git rev-parse --is-shallow-repository` reports a shallow checkout, then checks all commits reachable from the checked-out references on every push and pull request. History authors must be `KeelMatrix <keelmatrix@gmail.com>` or `dependabot[bot] <49699333+dependabot[bot]@users.noreply.github.com>`. History committers may be those two identities or GitHub's web-flow identity, `GitHub <noreply@github.com>`. Authorship is the author identity; a valid GitHub web commit must not be rejected merely because its technical committer is GitHub.

The commit-message hook uses a closed, configured deny contract. It rejects only the configured internal reference prefixes, internal vocabulary words, identity or metadata trailer labels, review-process phrases, and generated attribution phrase. The message is normalized as one stream first so the configured forms cannot be hidden by line breaks, whitespace runs, controls, or non-ASCII separators. The lists are plain literals at the top of `.githooks/commit-msg` and are the complete policy surface.

Trailer checks apply only to a contiguous block of trailer-shaped lines at the end of the message. A label such as `Agent:` in ordinary body prose is accepted when later body text follows it.

Non-internal ticket-shaped strings such as `ABC-1` and `PROJ-42` are accepted. Technical vocabulary and version spellings such as `SHA-224`, `SHA-3`, `TLS-1.1`, `AES-128`, `CVE-2026-1234`, `ISO-8601-1`, `IEEE-754-2019`, `SHA-256/384`, `net8.0.1`, and `UTF-8 1` are also accepted. These strings are indistinguishable from legitimate public identifiers, and the company rule against internal material in public history is defined exactly by the configured internal prefix and vocabulary lists. This is deliberate contract behavior: it avoids classifying arbitrary engineering prose by token shape.

Protect the default branch by requiring the `Commit history hygiene / commit-history` status check and requiring branches to be up to date before merging. Branch-protection and required-status settings are repository-administrator decisions and must be enabled separately from these versioned checks.
