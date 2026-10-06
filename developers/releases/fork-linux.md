# Fork Linux builds and releases

This fork builds Linux x64 and ARM64 binaries. Its two workflows replace the
inherited desktop matrix, cloud deployment, floating submodule checks, and
external package-manager publishing workflows. Nothing is published to
ShareX/XerahS or KovaForge repositories.

## Build and preview

`Build Linux` runs on pull requests targeting `develop`, pushes to `develop`,
and manual dispatch. It checks the pinned submodule revisions, builds the full
desktop solution in Release with warnings as errors, runs the main, packaging,
and MCP test suites through `build/verify.ps1 FullVerification`, and packages
both native runner architectures. Markdown, release condition, and asset validator checks also run.

`Release Linux` uses that same build. A manual run defaults to `publish=false`
and produces downloadable artifacts without creating a tag or GitHub release.
The combined `linux-release` artifact contains eight packages, the JSON
SHA-256/source manifest, and `SHA256SUMS`. Logs are separate artifacts. Artifacts
expire after 14 days; published release assets remain available.

The package matrix includes `.tar.gz`, `.deb`, `.rpm`, and `.AppImage` for
`linux-x64` and `linux-arm64`. Debian and RPM metadata, archive payloads, the
Amazon S3 plugin, and AppImage startup under Xvfb are checked before publication.
The Xvfb startup check is a launch smoke test; it does not test a real desktop's
region selector, tray integration, or clipboard upload.

## Cut a release

1. Commit the intended `x.y.z` app version in root `Directory.Build.props`
   together with the reviewed code, then merge it into this fork's `develop`.
   Use the next unreleased version and the repository's commit format.
2. Run `Release Linux` on `develop` with `publish=true`. The workflow builds,
   tests, packages, and validates that exact source commit before creating
   `vX.Y.Z` and publishing a new release in `patrick-hudson/XerahS`.
3. Alternatively, push a matching `vX.Y.Z` tag at a commit already on `develop`.
   The tag must match `Directory.Build.props`; it follows the same build gate.

Manual publication cuts the version already committed; it does not increment
versions, change branches, or commit unrelated files. Publication is limited to
this fork. Existing tags must resolve to the built commit, and existing releases
and assets are never overwritten. A failed build creates no manual-release tag.
If publication fails after a new tag is created, keep the tag and rerun the
workflow; do not move it to different code.

Only the final publication job has `contents: write`; builds use read-only
permissions. The standard GitHub Actions token creates the tag and release in
the same run, so no personal access token or signing secrets are required. It
does not rely on a token-created tag triggering another workflow. Every GitHub
CLI operation names this repository explicitly.

## Linux packaging limits

The pinned submodules are initialized recursively and are never updated to a
floating remote branch head during CI. The existing Linux package script builds
plugins sequentially, checks their dependency layout, and bundles the
watch-folder daemon and `omaxerahs` helper. The separate MCP server is built and
tested but is not included in these desktop packages.

`XERAHS_ALLOW_NO_OMASNAP=1` allows the portable tarball to omit the optional
OmaSnap capture engine, whose Hyprland/Qt build requires a separate Arch toolchain.
These runners do not install that toolchain; the fork uses XerahS's existing Linux
capture backends. This does not disable capture on Mint. No Flatpak, Arch, PPA,
COPR, OBS, Chocolatey, Windows, or macOS publication runs.

The packages are unsigned. SHA-256 checksums verify downloaded bytes against
the release manifest. The AppImage packager pins appimagetool 1.9.0 but currently
downloads the type2 runtime from its `continuous` release; that runtime remains
a packaging reproducibility limit inherited from the packager.

The app's **Release** update channel checks `patrick-hudson/XerahS` by default,
including when upgrading an existing stable-channel profile. Fresh settings also
use **Custom** with `patrick-hudson/XerahS` for the **Pre-release** channel. Saved
pre-release selections of ShareX, KovaForge, another custom repository, or
**Any source** are preserved. To switch an existing pre-release profile to this
fork, select **Custom** and set its source to `patrick-hudson/XerahS`.

Keep release tags numeric, such as `v0.31.5`; the updater parses the tag as a
numeric version. The release title identifies the Linux fork. The Linux in-app
updater's install step still uses Windows installer arguments, so install a newer
`.deb` with the system package installer or `sudo apt install ./XerahS-X.Y.Z-linux-x64.deb`.
The package upgrades the existing `xerahs` installation and keeps the user's
settings outside the installation directory. AppImages run separately from the
installed package.
