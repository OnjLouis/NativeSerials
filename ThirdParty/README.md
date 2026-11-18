# Reused Update Components

`Onj.SignedUpdater` is the MIT-licensed Windows verification and transaction
library from https://github.com/OnjLouis/SignedUpdater, version 0.1.0.
Its license is retained alongside its source. It is built for .NET Framework
4.8 and distributed as `Onj.SignedUpdater.dll`.

The native Mac update adapter retains verification, archive inspection and
bundle-replacement patterns from https://github.com/OnjLouis/phononMac and
https://github.com/OnjLouis/ElevenLabsMusicGenerator, by Andre Louis, under MIT.
Application identity, signature format, package names and limits are specific
to Native Serials. No other application's signing key is reused.
