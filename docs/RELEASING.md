# Releasing

How a codenamed milestone becomes two GitHub pre-releases: the `amnesia-tdd-tcp` `Amnesia.exe`
and the archive a player extracts into their Amnesia folder. `scripts\package-release.ps1` does
the building and packaging; tagging, verifying, and publishing stay human steps.

Versions follow the scheme in the README's [Versioning](../README.md#versioning) section. Each
repository has its own version line: Whisper is multiplayer `v0.1.0` and `amnesia-tdd-tcp`
`v0.2.0`.

## Before you start

- Both repositories are checked out side by side (`amnesia-multiplayer` and `amnesia-tdd-tcp`),
  both clean, both on the commit you intend to release. The script refuses otherwise.
- Visual Studio with the Desktop development with C++ workload, for the `amnesia-tdd-tcp` build.
- The .NET 10 SDK.
- Two Windows PCs on the same private LAN, both with Amnesia: The Dark Descent installed, for
  the walkthrough.

## Checklist

1. **Tag `amnesia-tdd-tcp`** at the commit that supplies `Amnesia.exe`, on its own version line:

   ```powershell
   git -C ..\amnesia-tdd-tcp tag v0.2.0
   ```

2. **Tag this repository** with the version in `src\Directory.Build.props`:

   ```powershell
   git tag v0.1.0
   ```

3. **Run the script.** It refuses unless both checkouts are clean and exactly on a tag, builds
   `Amnesia.exe`, runs the Game Peer tests, publishes the Game Peer self-contained for `win-x64`,
   and writes the archive and its release notes under the ignored `artifacts\release` folder:

   ```powershell
   .\scripts\package-release.ps1
   ```

   For a throwaway build off an untagged commit, `.\scripts\package-release.ps1 -Dev` skips the
   tag check and names the archive `-dev+<short-sha>`. Dev builds are never published. If your
   `amnesia-tdd-tcp` checkout is not the sibling folder, pass `-GameRepositoryPath <path>`.

   Beside the archive, the script leaves `artifacts\release\staging` — the archive unzipped —
   so you can check the layout without extracting anything.

4. **Read `VERSION.txt`** in the archive and confirm it names the two tags and commits you meant
   to release, and the expected `LanProtocol` version.

5. **Run the walkthrough on two PCs from the extracted archive**, not from a dev build: the
   [Whisper walkthrough](../README.md#whisper-walkthrough-from-the-release-archive) in the README.
   A release is verified only when every step produces the documented outcome, in addition to the
   automated suite passing.

6. **Edit the generated release notes** if this release needs more than the generated summary.

7. **Push the tags**, so that `gh` attaches each release to the commit you tagged rather than
   creating a tag of its own:

   ```powershell
   git -C ..\amnesia-tdd-tcp push origin v0.2.0
   git push origin v0.1.0
   ```

8. **Publish**, by running the two `gh release create ... --prerelease` commands the script
   printed: the `amnesia-tdd-tcp` release first, attaching the same `Amnesia.exe` that went into
   the archive, then this repository's release, attaching the archive and the release notes.

## If the walkthrough fails

Delete the local tags (`git tag -d`), fix the problem on a branch, and start again from step 1.
Tags are only pushed after the walkthrough passes, so a failed attempt leaves nothing published
to withdraw.
