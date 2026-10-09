# qpdf runtime for Transfer ASE

- Version: qpdf 12.4.2 (Windows MSVC 64-bit)
- Official source: https://github.com/qpdf/qpdf/releases/tag/v12.4.2
- Archive: `qpdf-12.4.2-msvc64.zip`
- Verified SHA-256: `db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d`
- License: Apache License 2.0 or Artistic License 2.0, at the user's option.

The application launches `bin/qpdf.exe` out of process. The runtime is copied beside
the client and server during build. Do not replace these binaries without verifying the
new release against the checksum file published with the official qpdf release.
