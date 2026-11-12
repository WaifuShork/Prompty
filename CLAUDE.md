# Prompty: notes for Claude

WPF / .NET 8 desktop app in `src/Prompty`. Build with `dotnet build src/Prompty`; release builds come from `build.ps1`.

## Git & releases

- **Never push** (`git push`, tags included) unless the owner explicitly asks in that conversation. Commit locally as work completes.
- Write commit messages as [Conventional Commits](https://www.conventionalcommits.org): `feat(scope): …`, `fix(scope): …`,
  `refactor:`, `perf:`, `docs:`, `build:`, `ci:`, `chore:`, `test:`. The type decides the next version.
- Versioning is semver, run by `release.ps1`:
  - `feat` → minor; everything else → patch. This is automatic; just run `.\release.ps1` when a release is asked for.
  - **Major versions only when the owner says so** (`.\release.ps1 -Major`). Never pass `-Major`, or mark commits breaking to force one, on your own.
- `release.ps1` updates `<Version>` in `src/Prompty/Prompty.csproj` and `CHANGELOG.md`, commits `chore(release): vX.Y.Z` and tags it.
  It does not push. Don't edit `<Version>` by hand.
- Notable changes can go under `## [Unreleased]` in `CHANGELOG.md`; the release script carries them into the next version.
