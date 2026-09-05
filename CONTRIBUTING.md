# Contributing to Open Hand

Thanks for your interest in contributing! This document covers how the repository is structured and what to expect when opening issues or pull requests.

## Ways to contribute

- **Bug reports** — open a [GitHub issue](https://github.com/MatthewChastain/open-hand/issues) and include your Vintage Story version, game platform, a description of what happened vs. what you expected, and (if relevant) the relevant section of your client log from `VintagestoryData/Logs/`.
- **Feature requests** — open an issue describing the behavior you'd like and why. Anything that changes selection or hand-resolution semantics is worth discussing before implementation.
- **Code changes** — via pull requests, following the workflow below.

## Repository structure

| Branch | Purpose | Rules |
|---|---|---|
| `main` | Stable release branch; the default branch on GitHub. | Changes land only via pull request. The `state-tests` CI check must pass. Force pushes and deletion are blocked. |
| `develop` | Main working branch. | Direct pushes are restricted to the maintainer; outside contributions come in via pull request. The `state-tests` check gates merges. |

Tags (`v*`) mark releases.

## Pull request workflow

1. **Fork** the repository (or create a feature branch off `develop` if you have write access).
2. **Build and test locally** — see the [Build](README.md#build) section of the README. The state tests must pass:
   ```bash
   dotnet build OpenHand.sln -c Release
   dotnet run --project tests/OpenHand.StateTests/OpenHand.StateTests.csproj -c Release
   ```
3. **Open your pull request against `develop`** (not `main`), using the PR template. Cover what changed, why, and how you tested it.
4. **CI must be green.** The `state-tests` check (test suite + modinfo validation) is a required status check; a PR cannot merge while it is failing.
5. A maintainer reviews and merges. `develop` is periodically merged into `main` for releases.

Changes that alter player-facing behavior should be validated in-game (single-player at minimum, both GUI scales if the change touches HUD rendering) before requesting review — note what you checked in the PR's Testing section.

### Version compatibility

Open Hand targets the Vintage Story 1.22 API and supports the whole 1.22.x line (minimum 1.22.0); the Harmony patches are verified against decompiled 1.22.7 assemblies. Changes that alter patch targets or game-version assumptions should include the decompile evidence that supports them.

## Reporting a bug with multiplayer

Open Hand is server-authoritative: the server validates selection state and broadcasts it. If you see desyncs in multiplayer, include whether you run the mod on the dedicated server or in single-player LAN mode, and the `/openhand status` output from the affected client.

## Releases

The maintainer cuts releases: bump `version` in `src/OpenHand/modinfo.json` (and the csproj), merge `develop` into `main`, then tag `v<version>` and push the tag. The Release workflow verifies the tag matches the modinfo version, builds, and attaches the zip to the GitHub release. Releases are announced in the release notes; larger releases are also published to the Vintage Story Mod DB.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE) that covers this project.
