# .NET Framework 4.8 — Memory Allocation Limits Demo

A console application that demonstrates how **Platform Target** (`AnyCPU`, `x86`, `x64`) and the **Prefer 32-bit** flag affect maximum memory allocation limits in .NET Framework 4.8.

The key insight: Visual Studio defaults new console projects to **Any CPU + Prefer 32-bit**, which silently forces 32-bit execution even on a 64-bit OS — capping the managed heap at roughly **1.5 GB** while the machine may still have tens of gigabytes of free RAM.

---

## What the Demo Shows

The program runs six allocation tests and pushes each to `OutOfMemoryException`:

| # | Test | What It Measures |
|---|------|------------------|
| 1 | **Single contiguous `byte[]`** | Largest single managed array the CLR can allocate |
| 2 | **`List<byte[]>` — 64 MB chunks** | Total heap via many fragmented allocations |
| 3 | **`List<int>` — backing array growth** | Single contiguous array limit (List doubles its internal array) |
| 4 | **`Dictionary<int,int>`** | OOM from internal bucket + entry array resizing |
| 5 | **`StringBuilder`** | Linked char[] chunk allocation limit |
| 6 | **`Queue<byte[]>` — 1 MB items** | Total heap via many small scattered allocations |

Tests 1, 3, and 4 require a **single large contiguous block** and hit OOM much sooner.
Tests 2, 5, and 6 scatter many smaller allocations across fragmented address space and can accumulate more total memory.

Running the same binary under 32-bit vs. 64-bit reveals the ceiling dramatically:

| Platform Target | Bitness | Typical Max Contiguous Array | Total Heap (fragmented) |
|---|---|---|---|
| Any CPU + Prefer 32-bit | 32-bit | ~1.3 – 2.0 GB | ~2.5 – 3.5 GB |
| x86 | 32-bit | ~1.3 – 2.0 GB | ~2.5 – 3.5 GB |
| Any CPU (no Prefer 32-bit) | 64-bit* | Much larger | Limited by OS / RAM |
| x64 | 64-bit | Much larger | Limited by OS / RAM |

\* On a 64-bit OS. On a 32-bit OS, Any CPU without Prefer 32-bit still runs as 32-bit.

---

## Prerequisites

- Windows with .NET Framework 4.8 (included in Windows 10 1903+)
- MSBuild (ships with Visual Studio or the [Build Tools for Visual Studio](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022))

Open a **Developer Command Prompt for VS** (or any shell where `msbuild` is on `PATH`).

---

## Building & Running All Four Configurations

The `.csproj` includes four pre-configured build profiles. Build and run each from the project directory:

### 1. Any CPU + Prefer 32-bit (the VS default)

```
msbuild DNFConsoleMemDemo.csproj /p:Configuration=AnyCPU_Prefer32 /p:Platform=AnyCPU
bin\AnyCPU_Prefer32\DNFConsoleMemDemo.exe
```

### 2. Any CPU — no Prefer 32-bit

```
msbuild DNFConsoleMemDemo.csproj /p:Configuration=AnyCPU_No32 /p:Platform=AnyCPU
bin\AnyCPU_No32\DNFConsoleMemDemo.exe
```

### 3. Explicit x86

```
msbuild DNFConsoleMemDemo.csproj /p:Configuration=Release_x86 /p:Platform=x86
bin\x86\Release_x86\DNFConsoleMemDemo.exe
```

### 4. Explicit x64

```
msbuild DNFConsoleMemDemo.csproj /p:Configuration=Release_x64 /p:Platform=x64
bin\x64\Release_x64\DNFConsoleMemDemo.exe
```

Compare the allocation numbers across runs — particularly configurations 1/3 (32-bit) vs. 2/4 (64-bit) — to see the 32-bit ceiling in action.

---

## How the Build Flags Work

Three MSBuild properties control process bitness:

| MSBuild Property | Values | Effect |
|---|---|---|
| `<PlatformTarget>` | `AnyCPU`, `x86`, `x64` | Sets the PE header target architecture |
| `<Prefer32Bit>` | `true` / `false` | When PlatformTarget is `AnyCPU`, forces the CLR to run as 32-bit even on a 64-bit OS |
| `<Platform>` | `AnyCPU`, `x86`, `x64` | Selects which `PropertyGroup` condition to match (build configuration plumbing) |

### Setting flags in the `.csproj` file

Each build configuration is a `<PropertyGroup>` with a `Condition` attribute. For example, to create a 64-bit release build:

```xml
<PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'MyRelease|AnyCPU' ">
  <PlatformTarget>AnyCPU</PlatformTarget>
  <Prefer32Bit>false</Prefer32Bit>       <!-- This is the critical flag -->
  <Optimize>true</Optimize>
  <OutputPath>bin\MyRelease\</OutputPath>
</PropertyGroup>
```

Or to force x64:

```xml
<PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'MyRelease|x64' ">
  <PlatformTarget>x64</PlatformTarget>
  <Prefer32Bit>false</Prefer32Bit>
  <Optimize>true</Optimize>
  <OutputPath>bin\x64\MyRelease\</OutputPath>
</PropertyGroup>
```

### Overriding flags from the MSBuild command line

You can override **any** MSBuild property at build time with `/p:Property=Value`, without modifying the `.csproj` at all. This is useful for CI pipelines or quick experiments.

**Override PlatformTarget:**
```
msbuild DNFConsoleMemDemo.csproj /p:PlatformTarget=x64
msbuild DNFConsoleMemDemo.csproj /p:PlatformTarget=x86
msbuild DNFConsoleMemDemo.csproj /p:PlatformTarget=AnyCPU
```

**Override Prefer32Bit:**
```
msbuild DNFConsoleMemDemo.csproj /p:Prefer32Bit=false
msbuild DNFConsoleMemDemo.csproj /p:Prefer32Bit=true
```

**Combine both (e.g., force Any CPU + 64-bit):**
```
msbuild DNFConsoleMemDemo.csproj /p:PlatformTarget=AnyCPU /p:Prefer32Bit=false /p:OutputPath=bin\test64\
```

**Select a named configuration AND override:**
```
msbuild DNFConsoleMemDemo.csproj /p:Configuration=AnyCPU_Prefer32 /p:Platform=AnyCPU /p:Prefer32Bit=false
```

> **Note:** Command-line `/p:` values take precedence over values in the `.csproj` file. This makes it easy to test different bitness settings without editing the project.

### Setting flags in Visual Studio

1. Right-click the project → **Properties** → **Build** tab
2. **Platform target** dropdown: select `Any CPU`, `x86`, or `x64`
3. **Prefer 32-bit** checkbox: check or uncheck
4. These correspond directly to `<PlatformTarget>` and `<Prefer32Bit>` in the `.csproj`

---

## Verifying the Output Binary

Use the `corflags` tool (ships with the .NET Framework SDK) to inspect the compiled `.exe`:

```
corflags bin\AnyCPU_Prefer32\DNFConsoleMemDemo.exe
```

Key fields to look at:

| Field | Meaning |
|---|---|
| `PE` | `PE32` = 32-bit compatible, `PE32+` = 64-bit only |
| `32BITREQ` | `1` = always runs 32-bit (set by `PlatformTarget=x86`) |
| `32BITPREF` | `1` = prefers 32-bit (set by `Prefer32Bit=true` with `AnyCPU`) |

Expected output for each configuration:

```
AnyCPU_Prefer32:  PE=PE32   32BITREQ=0  32BITPREF=1  → runs 32-bit
AnyCPU_No32:      PE=PE32   32BITREQ=0  32BITPREF=0  → runs 64-bit on 64-bit OS
Release_x86:      PE=PE32   32BITREQ=1  32BITPREF=0  → always 32-bit
Release_x64:      PE=PE32+  32BITREQ=0  32BITPREF=0  → always 64-bit
```

---

## Why This Matters

- **Visual Studio defaults new console apps to Prefer 32-bit = true.** This is the single most common reason .NET developers hit unexpected `OutOfMemoryException` on machines with plenty of RAM.
- A 32-bit .NET process on Windows is limited to a **~2 GB virtual address space** (up to ~4 GB with `LARGEADDRESSAWARE`). After the CLR, loaded assemblies, thread stacks, and GC bookkeeping take their share, the largest single managed array tops out around **1.3–2.0 GB**.
- Switching to 64-bit (either `AnyCPU` with `Prefer32Bit=false`, or explicit `x64`) removes this ceiling entirely — the managed heap is limited only by available OS memory.

---

## Project Structure

```
DNFConsoleMemDemo/
├── DNFConsoleMemDemo.csproj   # 4 build configs: AnyCPU_Prefer32, AnyCPU_No32, Release_x86, Release_x64
├── DNFConsoleMemDemo.slnx     # Solution file
├── Program.cs                 # All demo logic — 6 allocation tests + environment info
├── App.config                 # Standard .NET Framework app config
├── Properties/
│   └── AssemblyInfo.cs
└── README.md                  # This file
```
