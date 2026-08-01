# iRoute governance

iRoute is maintained as an Apache-2.0 community project. Governance is designed
to keep technical decisions reviewable and the safety/compatibility boundary
stable while the maintainer group grows.

## Roles

- **Contributor:** participates through issues, reviews, documentation, tests,
  or code.
- **Reviewer:** has demonstrated expertise in an area and can provide an
  approving review, but does not merge independently unless also a maintainer.
- **Maintainer:** triages reports, protects releases, merges changes, manages
  security advisories, and is accountable for project-wide compatibility.

Roles are earned through sustained, constructive contributions and can be
revisited when participation changes. No role grants ownership of contributor
work or an exemption from review and security rules.

## Decision process

Routine fixes use pull-request review and required checks. A change requires an
ADR when it alters a public boundary, dependency direction, persistence model,
security model, compatibility promise, governance rule, or long-lived operating
assumption.

Maintainers seek consensus after technical evidence is available. If consensus
cannot be reached, the maintainers responsible for the affected boundary make
the smallest reversible decision, record dissent and tradeoffs in the ADR, and
identify a reevaluation trigger. Security embargo decisions remain private
until coordinated disclosure.

## Merge and release authority

- Authors do not provide the sole approving review for their own material
  changes.
- Required CI and compatibility gates cannot be bypassed for convenience.
- A maintainer may merge an urgent security fix under embargo after private
  review; the public explanation follows with the advisory.
- Releases follow [docs/releasing.md](docs/releasing.md), use immutable tags,
  and are derived from a reviewed commit on the default branch.

## Conduct and moderation

Participation is governed by [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
Conduct reports should be sent privately to project maintainers. A technical
vulnerability uses the separate channel in [SECURITY.md](SECURITY.md).

## Changes to governance

Governance changes require a public pull request, an explanation of impact, and
maintainer consensus. A governance change cannot retroactively relicense an
accepted contribution.
