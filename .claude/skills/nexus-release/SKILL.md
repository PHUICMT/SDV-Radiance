---
name: nexus-release
description: Cut a SDV-Radiance release — bump version, changelog, merge+tag (auto-mirrors to the public repo), Release-build the Nexus zip, open its folder, and generate a fill-in Nexus-fields MD. Use when the user says "release", "ปล่อย version", "ออก x.y.z", or asks to prepare a Nexus upload.
---

# SDV-Radiance release

Repo: `e:\Dev\SDV-Radiance`. Dev repo `PHUICMT/SDV-Radiance-dev` (origin) auto-mirrors to public `PHUICMT/SDV-Radiance` on a `v*` tag push (`.github/workflows/publish-mirror.yml`). Never push to the public repo directly. SMAPI's GitHub update key points at the PUBLIC repo, so the tag/release there is what notifies players.

## Steps

1. **Version.** Bump `manifest.json` `Version` to the target `x.y.z`. Confirm nothing else pins the old version.

2. **Changelog.** Update `launch/CHANGELOG-draft.local.md` (gitignored, personal). Add an `## [x.y.z] — YYYY-MM-DD` block in Keep-a-Changelog form (Added / Changed / Fixed) for the GitHub/repo record. End every version with a **For translators** block listing new/changed `i18n/default.json` keys — diff against the previous tag:
   ```
   git show v<prev>:i18n/default.json | grep -oE '"[^"]+":' | sort -u > /tmp/a
   git show HEAD:i18n/default.json    | grep -oE '"[^"]+":' | sort -u > /tmp/b
   comm -13 /tmp/a /tmp/b     # keys new since prev
   ```
   **No em dash** ("—") anywhere in the changelog — it posts publicly (AI tell). Use colons/parentheses. Ship dormant/label-only features silently; advertise them only when their data actually ships.

3. **Commit + merge + tag.** Commit code on the working branch, then:
   ```
   git checkout main
   git merge --no-ff <branch> -m "release: x.y.z — <summary>"
   git tag -a vx.y.z -m "<summary>"
   git push origin main && git push origin vx.y.z
   ```
   Commit messages/PR bodies: NO Claude co-author trailer (this machine).

4. **Watch the mirror.** The tag push triggers the mirror. Check it:
   ```
   gh run list --workflow=publish-mirror.yml --limit 3
   ```
   If it **fails on the guard**, it is almost always the dev-privacy guard catching forbidden text in a mirrored file (`src/`, `shaders/`, root). The guard greps for: `Phase [0-9]`, `Phase L`, `เฟท`, `[BLOCKED-TERM:`, leftover internal docs, `.local.*`, `.claude/`, secrets. Reword the offending SOURCE comment (e.g. "Phase 1/2/3" → "Gather/Compose/Apply stage"), commit, then **re-tag**:
   ```
   git tag -d vx.y.z && git push origin :refs/tags/vx.y.z
   git tag -a vx.y.z -m "<summary>" && git push origin vx.y.z
   ```
   `docs/audit-*`, `docs/phase5b-*` and `.github/` are stripped/not-mirrored, so their matches are false alarms — only fix matches in mirrored files. Confirm success + public release:
   ```
   gh release view vx.y.z -R PHUICMT/SDV-Radiance
   ```

5. **Build the Nexus zip — RELEASE config** (Debug bundles the dev harness; Release strips it). Close the game first (DLL lock):
   ```
   Stop-Process -Name "Stardew Valley","StardewModdingAPI" -Force -ErrorAction SilentlyContinue
   dotnet build "e:\Dev\SDV-Radiance\SDV-Radiance.csproj" -c Release
   ```
   Zip lands at `bin\Release\net6.0\SDV-Radiance x.y.z.zip`.

6. **Open the folder** with the zip selected (standing preference — always open the output folder on any finished deliverable):
   ```
   Start-Process explorer.exe -ArgumentList '/select,"E:\Dev\SDV-Radiance\bin\Release\net6.0\SDV-Radiance x.y.z.zip"'
   ```

7. **Nexus fill-in MD.** Write `launch/RELEASE-x.y.z.local.md` with each Nexus box (File name `SDV-Radiance x.y.z`, version, category Main Files, file description, the changelog block for the Changelog widget) plus a pre-save checklist. Model it on the previous version's file.

   **Nexus Changelog widget format is NOT Keep-a-Changelog** — it is "one line per entry": every line becomes its own collapsible entry. So:
   - One self-contained sentence per line. NO leading `- ` (Nexus adds its own bullet, else it doubles).
   - NO bare `Added` / `Changed` / `Fixed` header lines (they become stray empty entries). Put the category inline as a prefix instead: `Added: ...`, `Changed: ...`, `Fixed: ...`, `Known: ...` (matches the house style used since 1.1.0).
   - Close with ONE `Translators: ...` line that points to the full key list (GitHub changelog / sticky post), NOT the full key dump. The full per-key list lives in the repo `CHANGELOG-draft.local.md` and the pinned post.
   - Still NO em dash anywhere.
   The Keep-a-Changelog block (with `###` headers and `- ` bullets) is only for the repo/GitHub `CHANGELOG-draft.local.md`, never for the Nexus widget.

8. **Report** the zip path, mirror/public status, and hand the Nexus MD to the user (they upload the zip + paste the changelog + set it as the main file themselves).
