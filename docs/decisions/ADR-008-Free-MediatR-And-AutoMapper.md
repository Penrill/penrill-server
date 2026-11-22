# ADR-008: MediatR Held at 12.5.0, No AutoMapper

## Status

Accepted, to be revisited

## Date

2026-11-22

## Context

The template uses MediatR 15 and AutoMapper 17. Since MediatR 13 and AutoMapper 15, Lucky Penny Software offers them
under the Reciprocal Public License 1.5 or a commercial licence, sized by the developers who write code calling them.
Penrill Server is source-available under FSL-1.1-MIT and installed by people on their own machines, and its core is
built into Penrill Cloud:

- RPL-1.5 asks that derived work be released under RPL-1.5, which FSL-1.1-MIT is not.
- A commercial or community licence belongs to the organisation that develops with it. Whether outside contributors and
  people who install the server need one, and where a licence key would live in public source, is not clear from the
  licence documents.

The last permissive releases are MediatR 12.5.0 (Apache-2.0) and AutoMapper 14.0.0 (MIT). AutoMapper 14.0.0 has three
high-severity denial-of-service advisories (GHSA-rvv3-g6hj-g44x, GHSA-gpwp-5797-mjff, GHSA-6vhm-j9h7-f9gh), fixed only
in the licensed releases.

## Decision

- MediatR is pinned at 12.5.0. `MediatR.Contracts` 2.0.1 in Domain is Apache-2.0 and unaffected.
- AutoMapper is not used. It mapped one query result; queries project with `Select` instead.

Do not upgrade MediatR or add AutoMapper back without revisiting this record.

## Rationale

- **The current versions with a licence** leave the open-source questions above unanswered.
- **AutoMapper 14.0.0 with the advisories suppressed** keeps a known vulnerable package in a public core for one mapping.
- **Replacing MediatR too** (for example with a source-generated MIT mediator) is the lasting fix, but a larger change;
  it is the option to take when 12.5.0 needs a fix it will not get.

## Consequences

**Easier:**
- No licence keys and no licence questions for contributors or people installing the server.
- One dependency fewer; projections are plain LINQ.

**Harder:**
- MediatR gets no further fixes. NuGet audit warnings fail the build, so a new advisory shows up at once.
- Use cases write their projections by hand.
