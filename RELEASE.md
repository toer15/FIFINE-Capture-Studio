# Releasing FIFINE Capture Studio

The application updates itself from GitHub Releases in
[`toer15/FIFINE-Capture-Studio`](https://github.com/toer15/FIFINE-Capture-Studio) using Velopack.

## Versioning

* Semantic version tags only: `v1.0.0`, `v1.0.1`, `v1.1.0`, `v2.0.0`.
* Pre-releases use a SemVer suffix: `v1.1.0-beta.1`. They are published as GitHub pre-releases and are
  only seen by installations on the **Beta** channel.
* `Version` in `src/FifineCaptureStudio/FifineCaptureStudio.csproj` is the source of truth for local
  builds. The release workflow passes the tag version with `-p:Version=…`, and fails the build if the
  produced executable does not report that version.
* Never reuse a tag or a version number. Velopack will refuse to downgrade an installation.

## Normal development

```powershell
git add .
git commit -m "Describe the change"
git push origin main
```

`.github/workflows/ci.yml` builds, runs the xUnit tests and smoke-tests every view. **Nothing is
published** by a normal push.

## Cutting a release

```powershell
git tag v1.0.1
git push origin v1.0.1
```

`.github/workflows/release.yml` then:

1. reads the version from the tag and rejects anything that is not SemVer;
2. restores, builds Release, runs the tests and smoke-tests the published app;
3. verifies the built `ProductVersion` matches the tag;
4. publishes self-contained `win-x64`;
5. downloads the previous release so Velopack can produce delta packages (skipped on the first release);
6. runs `vpk pack` (Setup.exe, full `.nupkg`, portable zip, `releases.win.json` with SHA-256);
7. uploads everything to the GitHub Release for that tag, marking it a pre-release when the version has
   a suffix.

The workflow needs no personal token: it uses the built-in `GITHUB_TOKEN` with `contents: write`, and
that permission exists only inside the workflow. No token is ever compiled into the app.

## Local packaging and update testing

```powershell
powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -Version 1.0.1
```

`vpk` is a .NET global tool that needs a working .NET 10 runtime. If it fails with a missing
`hostpolicy.dll`, point it at a healthy runtime (`$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"`). CI
installs both the .NET 8 SDK and .NET 10 with `actions/setup-dotnet`, so no extra setup is needed there.

To prove an update works without touching production releases:

```powershell
# 1. package 1.0.0 and install it
powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -Version 1.0.0 -OutputDir Releases-100
Start-Process "$env:LOCALAPPDATA\..\Local\FifineCaptureStudio\FifineCaptureStudio-win-Setup.exe" -ArgumentList --silent

# 2. package 1.0.1 somewhere else
powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -Version 1.0.1 -OutputDir Releases-101

# 3. ask the installed app to update from that folder and restart
& "$env:LOCALAPPDATA\FifineCaptureStudio\current\FifineCaptureStudio.exe" --update-test "$PWD\Releases-101"
# result is written to %LOCALAPPDATA%\FifineCaptureStudioData\update-test.txt
```

`--update-test` uses the same `UpdateManager` as the UI (check → download → SHA-256 verification →
apply → restart). It exists so update plumbing can be verified without publishing.

## Retention

Keep the newest stable release plus enough older releases for delta updates and rollback. Deleting
only the *assets* of old releases is safe; deleting the release itself removes the delta base for
clients still on that version. A sensible policy is: keep the last five stable releases and all
pre-releases from the current cycle.

## Repository visibility

The in-app updater uses Velopack's `GithubSource` without a token. That works for **public**
repositories. A private repository would require either shipping a read-only token (not acceptable —
never embed credentials in a client) or mirroring release assets to a public location, so the
repository must stay public for self-updates to work.

## Signing

Packages are not code-signed yet. Velopack still verifies every package against the SHA-256 in the
release feed, so a corrupted or tampered download is rejected, but Windows SmartScreen can warn about
the installer. Add an Authenticode certificate as workflow secrets and pass
`--signParams` to `vpk pack` to remove that warning.
