# Third-party notices

SupernoteBridge's own code is MIT-licensed (see `LICENSE`). It includes or links the components below,
which keep their own licenses.

## Vendored source

| Component | Location | License | Notes |
|---|---|---|---|
| SupernoteSharp 0.9.x, © nelinory (Chavdar Kostov) | `SupernoteSharp/` | MIT | https://github.com/nelinory/SupernoteSharp. Patched here: parsers accept `Stream` instead of `FileStream`; target framework net8.0-windows. |
| CsPotrace, © 2001-2016 Peter Selinger, © 2009-2016 Wolfgang Nagl, modified by nelinory | `SupernoteSharp/Business/CsPotrace.cs` | **GPL-2.0-or-later** | C# port of Potrace. Used for vector (traced) PDF export. |

## NuGet packages

| Package | Version | License |
|---|---|---|
| MediaDevices | 1.10.0 | MIT |
| PDFtoImage | 5.4.0 | MIT |
| SkiaSharp (via PDFtoImage) | 4.x | MIT |
| bblanchon.PDFium (via PDFtoImage) | — | Apache-2.0 (PDFium: BSD-3-Clause/Apache-2.0) |
| SixLabors.ImageSharp | 3.1.4 | Six Labors Split License (Apache-2.0 for qualifying open-source use) |
| VectSharp, VectSharp.PDF, VectSharp.SVG | 2.5.0 / 2.7.0 / 1.9.0 | **LGPL-3.0-only** |
| Microsoft.Extensions.FileProviders.Embedded, ASP.NET Core, .NET runtime | 8.x | MIT |
| xunit, Microsoft.NET.Test.Sdk, coverlet.collector (tests only) | — | Apache-2.0 / MIT |

## What this means for release binaries

The single-file `SupernoteBridge.exe` statically bundles CsPotrace (GPL-2.0-or-later) and VectSharp
(LGPL-3.0-only). A binary that combines them is therefore distributed under the terms of the
**GNU GPL version 3**. The complete corresponding source code is this repository at the tag of the
release. You may use, study, modify and redistribute the binary under the GPL-3.0; the SupernoteBridge
source files themselves remain available to you under MIT.

If you want a binary that is not GPL-covered, remove `CsPotrace.cs` and the vector export path
(`SvgConverter`) and use raster PDF export only; VectSharp would then still need LGPL-compliant
(replaceable, non-single-file) distribution.

This summary is provided for transparency and is not legal advice.
