# Native Serials

Native Serials is a local, read-only Native Instruments inventory reporter.
It scans Native Access and Service Center files already present on the machine
and builds an accessible product list with serials hidden by default.

For the full manual, open `Manual.html` from the Native Serials folder or press
`F1` in the app.

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

## Support

Questions and feedback: <https://onj.me/contact>

Native Serials is free software. If you want to support Andre Louis software,
use `Help` > `Donate` in the app or visit <https://onj.me/donate>.
