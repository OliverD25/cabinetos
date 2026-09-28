# ADR 0012: The marketplace index is served from GitHub Pages of a separate public repository

- Status: accepted
- Date: 2026-09-28
- Decided by: the architect's recommendation, taken on the creator's
  instruction in chat on 2026-09-28 ("where you can choose your recommended
  option, do this"). Creating the repository is the creator's step.

## Context

Phase 9 built the marketplace client over a static index
([marketplace.md](../marketplace.md)): one JSON file listing every extension
with its download URL and SHA-256, fetched over HTTPS with ETag caching,
or read from a folder for testing. `marketplace.index` defaults to a
placeholder address (`https://marketplace.cabinetos.invalid/index.json`)
that can never resolve, so the app has nowhere to look for real extensions.
Article 2 asks that the marketplace infrastructure stays free and open
source.

Options considered:

- **GitHub Pages of a separate public repository** (`cabinetos-marketplace`
  under the creator's account). Static files over HTTPS with GitHub's
  certificate, free, no server to run, ETag caching works. Extension authors
  send pull requests to that repository without touching the app, and the
  index can go public before the app's repository does.
- **GitHub Pages of the app's repository.** One repository less, but the
  index would wait for the app's repository to go public, and every
  extension change would be a commit in the app's history.
- **A raw file on GitHub** (`raw.githubusercontent.com`). Simplest, but the
  raw host sends caching headers of its own and rate-limits, and a `raw`
  address is not a home for an index.
- **The creator's own domain.** Full control, but a server or a hosting
  contract to run, and a certificate to renew.

## Decision

The real index lives in a separate public repository, `cabinetos-marketplace`
under the creator's GitHub account, published through GitHub Pages at
`https://oliverd25.github.io/cabinetos-marketplace/index.json`. The
repository holds the index, the extension archives (or links to release
assets of their own repositories) and the script that builds the index
(`sdk/marketplace/build-index.ps1` moves there, or stays here with a publish
step; whoever creates the repository decides and records it).

## Consequences

- **The creator creates the repository and turns on GitHub Pages.** That is
  outward-facing, so no unattended session does it.
- **Until it exists, the placeholder stays.** When it exists, the default
  changes in one commit: `DEFAULT_MARKETPLACE_INDEX` in
  `core/crates/cabinetos-config/src/model.rs`, the config schema
  (`sdk/config/cabinetos.schema.json`), [config.md](../config.md) and
  [marketplace.md](../marketplace.md), and the shell's empty state, which
  today recognises the placeholder address
  (`ui/CabinetOS.Tests/MarketplaceTests.cs` pins that text).
- **Publisher trust stays open.** The index is fetched over HTTPS and every
  download is checked against the index's SHA-256, but nothing yet proves
  who wrote the index. Signing it is a later record.
- A `file:` URL or a folder keeps working for testing, as today.
