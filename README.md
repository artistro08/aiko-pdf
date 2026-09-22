<p align="center">
    <img src="docs/aiko.png" width="128" alt="Aiko" />
</p>

<h1 align="center">
    Aiko
</h1>

<p align="center">
    A native WinUI 3 PDF reader for Windows. Open, read, select, copy.
</p>

![Aiko with a document open](docs/screenshot.png)

I built this because I wanted a simple PDF viewer that looked good. There is a
[sample document](docs/sample.pdf) in this repo if you want something to open.

## Installing

Download `Aiko-<version>-x64.msix` and `Aiko.cer` from [Releases](https://github.com/artistro08/aiko-pdf/releases),
then run:

```powershell
pwsh -File tools/install.ps1 -Package Aiko-1.0.0-x64.msix
```

The package is signed with my own certificate, so Windows needs it trusted once, which the script does. Prefer not
to install anything? The zip in the same release is the whole app in a folder: unzip it and run `AikoPdf.exe`.

## Building

You need the .NET 9 SDK and the Visual Studio 2022 build tools for the XAML compiler.

```bash
dotnet build AikoPdf/AikoPdf.csproj -p:Platform=x64
dotnet test tests/AikoPdf.Tests/AikoPdf.Tests.csproj
```

The build is unpackaged and self-contained, so the output folder runs as-is. To cut a release zip and installer:

```bash
pwsh -File tools/build-release.ps1
```

## Shortcuts

| Keys | What it does |
| --- | --- |
| Ctrl+O / Ctrl+P | Open, print |
| Ctrl+C / Ctrl+A | Copy the selection, select the page |
| Ctrl+wheel, Ctrl+Plus / Minus | Zoom |
| Ctrl+0 / Ctrl+1 / Ctrl+2 | Fit page, 100%, fit width |
