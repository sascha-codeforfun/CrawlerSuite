# Security Policy

## Intended use and threat model

CrawlerSuite is built to crawl a website **you own or control**. Point it at your
own site, and it captures a snapshot and reports what it finds.

The security hardening — an admission gate over fetching and writing — is **defense-in-depth for the
realistic accidents of crawling your own property**: a compromised page, an
open redirect, an unexpected link target. It is not a guarantee that the tool
is safe to aim at arbitrary, untrusted, or hostile sites. Crawling targets you
don't trust is outside the intended use, and that risk rests with the operator.

In normal use the practical risk surface is therefore dominated by deployment
and configuration choices — where it runs, what it is pointed at, and where its
output is opened — rather than by remote attackers.

The admission gate decides on an address's text: http(s) only, the configured
site and its allowed subdomains only, no IP literals. Where the site's own host
names resolve is trusted to their owner — an intranet site or split DNS resolves
to private addresses by design — so it is not refused but recorded: each crawl
keeps the addresses it looked up and connected to (`dns.json`), the site folder
keeps them across crawls (`dns-history.json`), and an address a host never had
is reported as information.

## Supported versions

Only the **latest release** is supported. When a new version ships, the
previous one is immediately superseded and receives no further security fixes —
the remedy is always to upgrade.

| Version | Supported |
| ------- | --------- |
| 2.0.x   | yes |

Latest release: https://github.com/sascha-codeforfun/CrawlerSuite/releases/latest

The console edition (Crawler 1.x) is a separate project with its own policy.

## Reporting an issue

Security issues are handled in the open, like any other bug. **Please open a
regular [GitHub issue](https://github.com/sascha-codeforfun/CrawlerSuite/issues)**
and include:

- the version or commit affected,
- the component involved (fetching/redirects, download limits, file/snapshot
  writing, or exported CSV/log output),
- a clear description and minimal steps to reproduce,
- any relevant configuration, with real hostnames, paths, and credentials
  removed.

The most useful reports describe a **bypass of the admission gate** — for
example a way to defeat the host allowlist, escape the snapshot write root, or
smuggle a formula into exported output. Those are genuine code defects rather
than configuration mistakes, and they are exactly what the hardening is meant
to stop.

This is a small, best-effort project with no formal SLA. Valid reports will be
acknowledged, assessed, and — when confirmed — fixed in a later release with
credit, unless you prefer otherwise.
