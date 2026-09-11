# Commit checks

Run `git config core.hooksPath .githooks` once per clone to enable the repository's local commit checks.

The versioned checks reject identity trailers and internal metadata in new commit messages. The public repository workflow checks all commits reachable from the checked-out references on every push and pull request. It also requires every reachable commit to have both author and committer set exactly to `KeelMatrix <keelmatrix@gmail.com>`.

Protect the default branch by requiring the `Commit history hygiene / commit-history` status check and requiring branches to be up to date before merging. Branch-protection and required-status settings are repository-administrator decisions and must be enabled separately from these versioned checks.
