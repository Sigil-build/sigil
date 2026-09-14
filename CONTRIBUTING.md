# Contributing to Sigil

**Sigil does not accept code contributions.** Pull requests will be closed
unmerged, and that is a licensing decision rather than a comment on the code in
them — please do not spend an afternoon on a patch expecting it to land.

What *is* genuinely wanted is below, and it is the more useful half.

## Why pull requests are closed

Sigil is licensed under the [Sigil License 1.0](LICENSE), which grants no right
to create derivative works. Soliciting patches for a codebase nobody may derive
from would be incoherent — and every merged outside contribution would give its
author a copyright stake in the result, fracturing the single ownership the
license depends on. Reconstructing that later, contributor by contributor, is a
well-known way to lose a project.

The full reasoning, including the alternatives weighed and rejected, is in
[ADR-016](docs/architecture/adr-016-licensing.md).

## What to send instead

- **Bug reports.** The most valuable thing you can file. Sigil installs
  software on other people's machines; a reproducible failure report is worth
  more than a patch. Include your `sigil.yaml` (redacted as needed), the exact
  command, the `Setup.exe` exit code, and the install log.
- **Security vulnerabilities.** Do **not** open a public issue. Follow
  [SECURITY.md](SECURITY.md).
- **Feature requests and design arguments.** Open an issue. If you think a
  decision recorded in `docs/architecture/` is wrong, say which ADR and why —
  ADRs get amended when the argument is good.
- **Documentation errors.** File them as issues. Wrong documentation is a bug
  with the same severity as wrong code, and the docs-vs-code audit that preceded
  the first release found seven real product defects hiding behind prose that
  described behaviour the engine never had.

## Reading the source

You may read it. That is what source-available means, and the repository is
public deliberately: an installer is a piece of software people grant
Administrator rights to, and "trust us" is not an argument. Audit it, check the
security claims in [SECURITY.md](SECURITY.md) against the code, and tell us
where they do not hold.

What the license does not permit is copying it, modifying it, reusing parts of
it in your own work, or building a competing tool from what you learn in it.
The exception — and it is deliberately broad — is the artifacts Sigil generates
for you: the `Setup.exe` and packages built from your own manifest are yours to
ship to as many users as you like, royalty-free. See the LICENSE for the exact
terms.

## Code of Conduct

Issue threads and discussions follow the
[Contributor Covenant 2.1](CODE_OF_CONDUCT.md).
