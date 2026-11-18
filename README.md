# Native Serials

Native Serials is a local, read-only Native Instruments inventory reporter.
It scans Native Access and Service Center files already present on the machine
and builds an accessible product list with serials hidden by default.
Inventory can show all products, products with recovered serials or products
without recovered serials, independently of whether serials are revealed.

The Inventory and Storage by Company tabs share one application window.
Storage by Company ranks registered product folders by total size
or product count, with sortable product sizes, cancellable background scans and
serial-free HTML reports. Shared folders are counted once in each company total
and once overall. Coverage includes registered content and installation paths,
plus product plug-ins, standalone applications and support data matched by name
and publisher in standard locations. All counted paths are listed; unmatched
files and nonstandard locations may remain uncounted. Verified enclosing NKS
libraries are included. Mac discovery also checks known manufacturer folders on
local external volumes, UAD resource packages and Waves component/sample data.
Registration metadata still does not prove that every installed file was found.

For the full manual, choose Help > Open Manual or open `Manual.html`.
Press `F1` for help about the focused control; help dialogs also offer
Open Manual and close with Escape.

Project page: <https://github.com/OnjLouis/NativeSerials>

Native Serials is neither endorsed nor supported by Native Instruments. It may
not detect every product serial you own.

## Sources

macOS:

- `/Users/Shared/Native Instruments/installed_products`
- `/Users/Shared/Native Instruments/Native Access/ras3`
- `/Library/Application Support/Native Instruments/Service Center`
- `/Users/Shared/Native Instruments/Logs/NTK`

Windows:

- `%PUBLIC%\Documents\Native Instruments\installed_products`
- `%PUBLIC%\Documents\Native Instruments\Native Access\ras3`
- `%CommonProgramFiles%\Native Instruments\Service Center`
- `%PUBLIC%\Documents\Native Instruments\Logs\NTK`

The Windows locations follow Native Instruments support guidance for Native
Access RAS3 activation tokens and Service Center XML metadata.

## Privacy

RAS3 JWT files can contain serial claims. Native Serials decodes them locally
and never sends data anywhere. Serials are masked in the main list until reveal
is enabled. HTML export can include revealed serials when the user explicitly
chooses that command.

## Build

The Windows build requires a .NET SDK with .NET Framework 4.8 reference
assemblies; the packaged app requires only .NET Framework 4.8. Mac builds require
Xcode's Swift 6 toolchain and target macOS 13 or later. The Mac package includes
both Apple Silicon and Intel architectures.

Mac:

```sh
cd NativeSerials/Mac
swift build
```

Windows:

```powershell
cd NativeSerials\Windows
.\Build.ps1
```

## Updates

Release ZIPs have signed `.zip.json` manifests and detached `.zip.json.sig`
signatures. Verification keys are embedded in the app; private signing keys are
not part of this repository. The Windows updater uses the MIT-licensed
Onj.SignedUpdater source under `ThirdParty`. Mac updates additionally verify the
Developer ID, notarization and universal executable before replacement. Failed
replacement restores the previous program where possible.

Mac version 1.0 users must quit the app and manually replace it in Applications
using the release ZIP, rather than using the old Install command, which does not
preserve the new app's signing identity. Later versions support verified in-app
updates. See the manual for installation requirements and recovery information.

## Support

Questions and feedback: <https://onj.me/contact>

Native Serials is free software. If you want to support Andre Louis software,
use `Help` > `Donate` in the app or visit <https://onj.me/donate>.
