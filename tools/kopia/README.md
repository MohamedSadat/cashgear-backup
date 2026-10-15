# Pinned Kopia engine

CashGear Backup bundles [Kopia](https://github.com/kopia/kopia) (Apache-2.0) as its backup engine.
Only the exact build recorded in [`kopia.pin.json`](kopia.pin.json) is ever used.

| File | Committed | What |
|---|---|---|
| `kopia.pin.json` | yes | Version, release asset URL/size/SHA-256, the `kopia.exe` SHA-256, signing-key fingerprint |
| `fetch-kopia.ps1` | yes | Downloads and verifies the pinned build |
| `LICENSE` | yes | Kopia's Apache-2.0 licence at the pinned tag |
| `kopia.exe` | **no** (gitignored) | Installed by the script; ~53 MB |

## Install (once per clone, and after a pin change)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\kopia\fetch-kopia.ps1
```

The script downloads the release zip, requires its SHA-256 to match both the pin and the release's
`checksums.txt`, extracts only `kopia.exe`, checks its SHA-256 and `--version`. It is a no-op when the
installed exe already matches; `-Force` reinstalls.

## Bumping the version (maintainers)

1. Read Kopia's [compatibility notes](https://kopia.io/docs/advanced/compatibility/) and release notes:
   repository format changes are one-way.
2. Run `fetch-kopia.ps1 -PinVersion X.Y.Z`. It verifies the GPG signature on `checksums.txt` with
   Kopia's signing key (fingerprint `7FB99DFD47809F0D5339D7D92273699AFD56A556`, needs `gpg`/`gpgv`
   from Git for Windows or Gpg4win), checks the zip against it, requires a valid Authenticode
   signature from `CN=Kopia LLC`, fetches `LICENSE` at the tag and rewrites the pin.
3. Run the full test suite, including the spike/engine integration tests, against the new binary.
4. Review the pin diff and commit it with the test results in the message.

## Licence obligations

- Ship `LICENSE` with the product and list Kopia on an "Open-source components" screen; state if we
  ever modify it.
- Kopia has no `NOTICE` file at v0.23.1 (the script warns if one appears).
- **To do before the installer ships (Phase 4):** `kopia.exe` statically links many Go modules whose
  licences are not in the release zip. Generate their notices (for example with `go-licenses` against
  the pinned tag) and ship them with the product.
- Don't use the Kopia name or logo as product branding.
