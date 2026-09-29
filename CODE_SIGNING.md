# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> [!NOTE]
> AutoFantic has applied for free code signing. Until SignPath Foundation has approved it, releases are **not signed** yet, and Windows SmartScreen warns when you start them ("Windows protected your PC" → *More info* → *Run anyway*).

## What gets signed

Only `AutoFantic.exe` and `autofantic-spike.exe` from a release: built by GitHub Actions from this repository's source when a version tag is pushed ([release workflow](.github/workflows/release.yml)), never on a personal PC. Both must say they are *AutoFantic* in the release's version, or they aren't signed ([artifact configuration](.signpath/artifact-configuration.xml)). Every signing request is approved by hand.

Once AutoFantic is signed, its updater only installs an update that is signed by the same publisher.

## Team roles

| Role | Who |
|------|-----|
| Committers and reviewers | [Ironnix](https://github.com/Ironnix) |
| Approvers | [Ironnix](https://github.com/Ironnix) |

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it, **except the update check**: at every start and once a day, AutoFantic asks GitHub's public API for the list of AutoFantic releases. GitHub sees the PC's IP address and "AutoFantic/<version>"; nothing about the PC, its fans or its measurements is sent. It can be switched off in *Settings → Updates → Check by itself*. A new version is downloaded only when the user clicks *Update*. See [Privacy](README.md#privacy).

## Setting it up (for the maintainer)

1. Turn on two-factor authentication on GitHub, then apply at [signpath.org](https://signpath.org) (open source, MIT license, released on GitHub).
2. In SignPath, once approved: a project **AutoFantic** (slug `AutoFantic`), linked to the predefined **GitHub.com** trusted build system; a signing policy with the slug **`release-signing`** (manual approval); as artifact configuration the contents of [`.signpath/artifact-configuration.xml`](.signpath/artifact-configuration.xml). Two-factor authentication on SignPath too.
3. In GitHub → *Settings → Secrets and variables → Actions*: the secret **`SIGNPATH_API_TOKEN`** (a SignPath API token of a user who may submit signing requests) and the variable **`SIGNPATH_ORGANIZATION_ID`**.
4. The next version tag builds, sends both exes to SignPath, waits for the approval and puts the signed exes into the release. Without the secret, the workflow skips signing and the release is unsigned, as before.
