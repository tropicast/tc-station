# How to obtain the release signing secrets

The release workflow (`.github/workflows/release.yml`) reads the secrets below. All are optional:
without them the build still succeeds, but packages are only test/ad-hoc signed. Add them under
**Settings → Secrets and variables → Actions → New repository secret**.

| Secret | Platform | Where the value comes from |
|---|---|---|
| `WINDOWS_PFX_BASE64` | Windows | [Windows](#windows) step 2 |
| `WINDOWS_PFX_PASSWORD` | Windows | Password you chose when exporting the PFX |
| `WINDOWS_MSIX_PUBLISHER` | Windows | [Windows](#windows) step 3 |
| `MACOS_CERTIFICATE_BASE64` | macOS | [macOS](#macos) step 2 |
| `MACOS_CERTIFICATE_PASSWORD` | macOS | Password you chose when exporting the `.p12` |
| `MACOS_SIGNING_IDENTITY` | macOS | [macOS](#macos) step 3 |
| `APPLE_ID` | macOS | Apple account e-mail used for the Developer Program |
| `APPLE_TEAM_ID` | macOS | [macOS](#macos) step 4 |
| `APPLE_APP_SPECIFIC_PASSWORD` | macOS | [macOS](#macos) step 5 |

Never commit certificates or passwords. Delete local `.pfx`/`.p12` copies once uploaded, and keep
the originals in a password manager.

## Windows

MSIX packages must be signed by a certificate whose **subject equals the manifest publisher**.
Users only get a clean install if the certificate chains to a trusted root.

1. **Get a code-signing certificate.**
   - Production: buy a code-signing certificate from a public CA (DigiCert, Sectigo, SSL.com…) or use
     [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/). Public CAs now
     usually ship keys on hardware tokens or cloud HSMs; for GitHub-hosted runners choose a
     provider that lets you export a PFX or sign through an API.
   - Testing only (not trusted by other machines):
     ```powershell
     $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Tropicast" `
       -KeyUsage DigitalSignature -FriendlyName "Tropicast test" `
       -CertStoreLocation Cert:\CurrentUser\My `
       -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
     ```
2. **Export to PFX and encode it** (`WINDOWS_PFX_BASE64`, `WINDOWS_PFX_PASSWORD`).
   ```powershell
   $pwd = Read-Host -AsSecureString "PFX password"
   Export-PfxCertificate -Cert $cert -FilePath tropicast.pfx -Password $pwd
   [Convert]::ToBase64String([IO.File]::ReadAllBytes("tropicast.pfx")) | Set-Clipboard
   ```
   Linux/macOS: `base64 -w0 tropicast.pfx` (macOS: `base64 -i tropicast.pfx`).
   The password is the one you typed above.
3. **Read the publisher** (`WINDOWS_MSIX_PUBLISHER`). It must match the subject character for
   character, including order:
   ```powershell
   (Get-PfxCertificate tropicast.pfx).Subject
   # e.g. CN=Tropicast, O=Tropicast, C=FR
   ```

A self-signed PFX is accepted by the release build, which only warns that the chain is untrusted.
Anyone installing that MSIX must first import the `.cer` into `LocalMachine\TrustedPeople`.
Once you use a public CA certificate, set the repository variable `TC_REQUIRE_TRUSTED_CHAIN` to `1`
(Settings → Secrets and variables → Actions → Variables).

## macOS

Requires a paid [Apple Developer Program](https://developer.apple.com/programs/) membership.

1. **Create a "Developer ID Application" certificate.** In Xcode: *Settings → Accounts → Manage
   Certificates → + → Developer ID Application*. (Or create it at
   developer.apple.com → Certificates, using a CSR from Keychain Access.) Only the Account Holder
   can create Developer ID certificates.
2. **Export it as `.p12`** (`MACOS_CERTIFICATE_BASE64`, `MACOS_CERTIFICATE_PASSWORD`). In Keychain
   Access, open *My Certificates*, right-click the "Developer ID Application: …" entry (with its
   private key) → *Export…* → `.p12`, and choose a password. Then:
   ```bash
   base64 -i tropicast.p12 | pbcopy
   ```
3. **Find the signing identity** (`MACOS_SIGNING_IDENTITY`):
   ```bash
   security find-identity -v -p codesigning
   # 1) ABCD… "Developer ID Application: Your Name (TEAMID1234)"
   ```
   Use the quoted name (without quotes), or the 40-character hash.
4. **Team ID** (`APPLE_TEAM_ID`): the 10-character code in parentheses above, or
   developer.apple.com → *Membership details*.
5. **App-specific password** (`APPLE_APP_SPECIFIC_PASSWORD`): sign in at
   [account.apple.com](https://account.apple.com) → *Sign-In and Security → App-Specific
   Passwords* → generate one named e.g. "tropicast-notarize". It looks like `abcd-efgh-ijkl-mnop`.
   `APPLE_ID` is the e-mail of that same account (2FA must be enabled).

Verify locally before uploading:
```bash
xcrun notarytool history --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APP_SPECIFIC_PASSWORD"
```

## Adding them with the GitHub CLI

```bash
gh secret set WINDOWS_PFX_BASE64 --repo tropicast/tc-station < <(base64 -w0 tropicast.pfx)
gh secret set WINDOWS_PFX_PASSWORD --repo tropicast/tc-station      # prompts for the value
gh secret set WINDOWS_MSIX_PUBLISHER --repo tropicast/tc-station --body "CN=Tropicast, O=Tropicast, C=FR"
gh secret set MACOS_CERTIFICATE_BASE64 --repo tropicast/tc-station < <(base64 -w0 tropicast.p12)
gh secret list --repo tropicast/tc-station
```

## Checking it worked

Push a pre-release tag such as `v0.1.0-rc1` from `main`; the draft release's logs should no longer
show "unsigned"/"ad-hoc" warnings. Then verify on a clean machine:
`Get-AuthenticodeSignature` (Windows) and `spctl -a -vv -t install Tropicast.dmg` plus
`xcrun stapler validate` (macOS). These clean-machine checks have not been performed yet.
