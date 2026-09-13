<p align="center">
  <img src="Assets/ShowFilesAsList.png" width="128" alt="ShowFilesAsList icon">
</p>

<h1 align="center">ShowFilesAsList</h1>

<p align="center">
  A Windows console program that scans a folder and saves every file and subfolder in it, ordered by size, as a JSON tree.
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/github/v/release/muhammetozeski/ShowFilesAsList?label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
  <img src="https://img.shields.io/badge/C%23-14-239120" alt="C# 14">
  <img src="https://img.shields.io/badge/platform-Windows%20x64-0078D4" alt="Windows x64">
  <img src="https://img.shields.io/badge/build-Native%20AOT-2ea44f" alt="Native AOT">
</p>

---

## What it does

You enter a folder path. The program reads the whole tree below that folder, adds up how much space every
folder takes and writes the result to `result.json` next to the executable. The file is then opened with the
program Windows uses for `.json` files; a browser or an editor with a JSON tree view lets you fold and unfold
the folders.

When the folder is on a local NTFS drive, the program can read the drive's Master File Table directly instead
of opening every folder one by one — a handful of large sequential reads instead of one small operation per
folder, on the order of a hundred times faster for a folder with many files. This needs administrator rights,
since reading a volume's raw bytes is only granted to an elevated process; the program offers to restart
elevated when it would help, and falls back to the ordinary, slower walk when declined, when the drive is not
NTFS, or if the fast read fails for any reason. Both paths produce identical results — the same names, the
same sizes, the same tree — so which one ran does not change what ends up in `result.json`.

```json
{
  "> notes": "Scan time: 1.24 s\nScanned size: 7.49 GB\nFree space on disk: 120.35 GB\nDisk size: 476.94 GB",
  "Videos : 6.20 GB": {
    "holiday.mp4": "4.10 GB",
    "birthday.mp4": "2.10 GB"
  },
  "Photos : 1.29 GB": {
    "2024 : 1.20 GB": {
      "IMG_0412.dng": "620.00 MB",
      "IMG_0413.dng": "608.80 MB"
    },
    "cover.jpg": "92.16 MB"
  },
  "Empty folder : 0 B": {},
  "Locked : 0 B": {
    "Error: Access to the path 'D:\\Archive\\Locked' is denied.": "D:\\Archive\\Locked"
  },
  "notes.txt": "12.50 KB",
  "desktop.ini": "282 B",
  "Old Photos : junction": "D:\\Archive\\2019-Photos"
}
```

## Features

- Inside every folder, subfolders come first and files after them, each group ordered from the largest to the smallest.
- A folder's size includes everything below it. Units are binary (1 KB = 1024 B) and the decimal separator follows the Windows region settings.
- Hidden and system files are counted.
- A folder that cannot be read does not stop the scan. It gets an `Error:` entry with the reason and its path.
- A junction, a symbolic link, or any other kind of folder redirect is listed as `"name : junction": "target"` and
  never opened: its target is often reachable under its own, real location elsewhere in the tree too, so counting
  its content here as well would count it twice.
- The notes at the top show how long the scan took, the scanned size, and the free and total space of the drive.
- A progress line shows how much has been counted while the scan runs.
- Quotes around the entered path are removed, so a path copied with Explorer's **Copy as path** can be pasted as it is.
- Falling back to the ordinary walk (declined elevation, a non-NTFS drive, network path or removable media) still
  scans several folders at once on a solid-state drive; a spinning hard drive is scanned one folder at a time, since
  concurrent reads from scattered folders on a spinning disk add seeks instead of hiding them.
- The portable build is compiled with Native AOT: one executable of about 2.5 MB that needs no .NET installation and
  starts without loading a runtime or compiling code first.

## Security architecture

- **Reads file and folder metadata only.** The ordinary walk enumerates folders with the Windows directory API,
  which returns names, sizes and attributes; file contents are never opened. The fast path opens the NTFS volume
  itself for raw reading and parses its Master File Table directly — still only names, sizes and folder structure,
  the same information the ordinary walk reads, just read differently.
- **Administrator rights are optional and requested explicitly.** They are only needed for the fast path on a
  local NTFS drive; the program asks before restarting itself elevated for this, and scans normally without it.
  Without elevation, folders the current user cannot read are reported as such, not bypassed. With elevation, the
  volume's raw structure is visible regardless of a folder's own permissions, the same as any administrator tool
  that reads a disk directly.
- **Writes one file.** `result.json` is created next to the executable and overwritten on every run. It contains the
  names of all scanned files and folders, so treat it like the listing it is.
- **Starts one process.** `explorer.exe` is started with the path of `result.json` to open it, and — only when
  restarting elevated — a second copy of this same executable, with the folder to scan as its one argument.
- **No network access, no registry changes.**
- **No third-party packages.** Only the .NET base class library is used.
- **Signed executables.** Release executables carry an Authenticode signature.

## Installation

1. Download `ShowFilesAsList.exe` from the [latest release](../../releases/latest), or
   `ShowFilesAsList-FrameworkDependent-RequiresNET10.exe` if the .NET 10 Runtime is installed.
2. Put it in a folder of its own that you can write to, because `result.json` is saved next to it.
3. Run it, enter the folder to scan, check the path it shows and press any key to start. On a local NTFS drive it
   then asks whether to restart elevated for the fast scan; declining just uses the ordinary, slower walk instead.

The executables are signed with a self-issued certificate: run `Install-Certificate.cmd` from `SignatureTrust.zip`
in the release once so Windows can verify the signature (the program runs without it, and the signature does not
remove the SmartScreen warning for downloaded files).

## Building from source

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The Native AOT build also needs
Visual Studio with the **Desktop development with C++** workload, which provides the linker.

```powershell
# Run from source
dotnet run

# Portable build (Native AOT)
dotnet publish -c Release -r win-x64 -o publish/portable

# Single-file build that uses the installed .NET 10 runtime
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishAot=false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish/framework-dependent
```

The Native AOT build finds the linker through `vswhere`. If Visual Studio is not registered with its installer,
publish from a command prompt prepared by `vcvars64.bat` and let the build take the linker from that environment:

```bat
call "<Visual Studio folder>\VC\Auxiliary\Build\vcvars64.bat"
dotnet publish -c Release -r win-x64 -p:IlcUseEnvironmentalTools=true -o publish\portable
```

## Project structure

| File | Contents |
| --- | --- |
| `Program.cs` | Reading the folder path, choosing the scan strategy, the elevation prompt, saving and opening `result.json` |
| `DirectoryScanner.cs` | The ordinary walk: reads each folder once through the Windows directory API, several at once when the drive allows it |
| `StorageMediaDetector.cs` | Finds whether a path's drive is solid-state or spinning, to size `DirectoryScanner`'s parallelism |
| `NativeStorageApi.cs` | The raw `CreateFile` / `DeviceIoControl` calls `StorageMediaDetector` and the NTFS reader share |
| `ScannedDirectory.cs` | One scanned folder: files, subfolders, junctions, total size and read error — the shared result of either scan strategy |
| `ResultJsonWriter.cs` | Writes the notes and the tree to the JSON file |
| `SizeTextExtensions.cs` | Turns byte counts into text such as `512 B` or `1.50 GB` |
| `Ntfs/MftVolumeScanner.cs` | The fast path: builds the same tree as `DirectoryScanner` from one read of the volume's Master File Table |
| `Ntfs/NtfsVolumeAccessor.cs` | Opens an NTFS volume and reads its whole Master File Table in a few large sequential reads |
| `Ntfs/MftRecordParser.cs` | Parses one MFT record: the update sequence fixup, its attributes, its name and size |
| `Ntfs/MftFileRecord.cs` | One parsed MFT record and its name(s) under its parent folder(s) |
| `Ntfs/NtfsRunListParser.cs` | Decodes the data run list of a non-resident attribute |
| `Ntfs/NtfsDataRun.cs` | One contiguous extent of a non-resident attribute |
