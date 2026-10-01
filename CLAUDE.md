# AuFantic: notes for Claude

The README has the overview, how to build and test, and how the texts and languages work. This file holds what is
done the same way every time.

## Publishing a new version

When the user asks to publish (or "commit and tag as always"), do all of this without asking again:

1. **Changelog:** `CHANGELOG.md` has a section `## [x.y.z] - dd.mm.yyyy` at the top for the new version, with
   `### New`, `### Changed`, `### Fixed` as needed. Every change since the last tag is in it, in simple words for
   the people who use the app (what they see and where, not how the code changed). The release workflow fails if
   this heading is missing, and its text becomes the release notes.
2. **Version:** `<VersionPrefix>` in `Directory.Build.props` is the same `x.y.z`. The last tag (`git tag
   --sort=-creatordate`) says which version is out; the next one is normally one patch step up.
3. **Check:** `dotnet test -c Release` passes (that is what the workflow runs).
4. **Commit** everything on `main` (`Version x.y.z: <the main changes in a few words>`, then a short body) and
   **push** `main`.
5. **Tag** that commit `vx.y.z` (plain: `v0.2.1-test` is ignored by the app's update check) and **push the tag**.
   The tag starts `.github/workflows/release.yml`: tests, both exes, the zip `AutoFantic-x.y.z-win-x64.zip`, and a
   **draft** pre-release on GitHub.
6. **Watch the run** and say how it ended. Without the `gh` tool the public API answers too:
   `https://api.github.com/repos/Ironnix/AutoFantic/actions/runs?per_page=3` (`status`, `conclusion`, `head_branch`
   = the tag). It takes about 5 minutes. A draft itself is not visible without a login. If the run failed, fix the
   cause, and move the tag only if the release doesn't exist yet.
7. **Stop there.** Publishing the draft (GitHub → Releases → the draft → Publish) is the user's step: only then do
   installed copies find the update.

After a release, the first change for the next version opens its section in `CHANGELOG.md` and raises
`<VersionPrefix>`.

## Things that stay as they are

- People see the name **AuFantic**. The technical names stay `AutoFantic`: the exe, the zip, the data folder
  `%LocalAppData%\AutoFantic`, namespaces and project folders. Installed copies only update from a zip with that name.
- The logo's blade is in three places with the same numbers: `assets/logo.svg`, `src/AutoFantic.App/AuFantic.ico`
  (made from it) and `TrayIcon.MakeIcon`. Change them together.
- Never start the real app or `Start-Dev-Build.cmd` to test: it takes over the real fans. Test with `--simulate`
  (`--selftest`, `--selftest-calibration`, `--screenshot`), see the README.
