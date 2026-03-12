using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DNFConsoleMemDemo
{
    internal class Program
    {
        // 8 GB cap — enough to prove we've blown past the 2 GB 32-bit ceiling
        // without consuming every byte of RAM + page file on the machine.
        const long MaxAllocationBytes = 8L * 1024 * 1024 * 1024;

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "DNF 4.8 Memory Allocation Limits Demo";
            PrintBanner();
            PrintEnvironmentInfo();

            Console.WriteLine();
            PrintSection("MEMORY ALLOCATION TESTS");
            Console.WriteLine($"  Each test allocates up to {FormatBytes(MaxAllocationBytes)} (or until OOM).");
            Console.WriteLine("  This proves a 64-bit process can blow past the 2 GB 32-bit ceiling.");
            Console.WriteLine();

            // ── Test 1: Single contiguous byte[] ────────────────────────
            // This probes the largest SINGLE contiguous allocation the CLR
            // can satisfy.  .NET arrays are int-indexed, so the natural cap
            // is ~2 GB regardless — but we also honour MaxAllocationBytes.
            RunTest("Single Contiguous byte[] (max array size)", () =>
            {
                int low = 0;
                int high = (int)Math.Min(int.MaxValue, MaxAllocationBytes);
                int maxSize = 0;

                while (low <= high)
                {
                    int mid = low + (high - low) / 2;
                    try
                    {
                        var buffer = new byte[mid];
                        maxSize = mid;
                        buffer = null;
                        GC.Collect();
                        low = mid + 1;
                    }
                    catch (OutOfMemoryException)
                    {
                        high = mid - 1;
                    }
                }

                return $"Max single byte[] = {FormatBytes(maxSize)}";
            });

            // ── Test 2: Many 64 MB chunks ───────────────────────────────
            // Accumulates MANY separate allocations up to the 8 GB cap.
            // 32-bit processes hit OOM well before this; 64-bit processes
            // reach the cap, proving they can exceed the 2 GB ceiling.
            RunTest("List<byte[]> — Accumulate 64 MB Chunks", () =>
            {
                const int chunkSize = 64 * 1024 * 1024;
                var chunks = new List<byte[]>();
                long totalAllocated = 0;

                try
                {
                    while (totalAllocated + chunkSize <= MaxAllocationBytes)
                    {
                        chunks.Add(new byte[chunkSize]);
                        totalAllocated += chunkSize;
                    }
                }
                catch (OutOfMemoryException) { }

                int count = chunks.Count;
                string peakMem = FormatBytes(GetPeakWorkingSet());
                chunks.Clear();
                chunks = null;
                GC.Collect();

                return $"{count} chunks × 64 MB = {FormatBytes(totalAllocated)}  (Peak WS: {peakMem})";
            });

            // ── Test 3: List<int> — single backing array growth ─────────
            // List<T> uses a SINGLE contiguous backing array that doubles
            // in size. Stops at the 8 GB cap or OOM.
            RunTest("List<int> — Single Backing Array Growth", () =>
            {
                var list = new List<int>();
                long count = 0;
                try
                {
                    while (true)
                    {
                        int batch = 50_000_000; // ~200 MB per batch
                        long projectedBytes = ((long)list.Count + batch) * 4L;
                        if (projectedBytes > MaxAllocationBytes)
                        {
                            int remaining = (int)((MaxAllocationBytes / 4L) - list.Count);
                            if (remaining <= 0) break;
                            batch = remaining;
                        }
                        list.Capacity = list.Count + batch;
                        for (int i = 0; i < batch; i++)
                            list.Add(i);
                        count = list.Count;
                        if (count * 4L >= MaxAllocationBytes) break;
                    }
                }
                catch (OutOfMemoryException)
                {
                    count = list.Count;
                }

                long bytesUsed = count * 4L;
                list.Clear();
                list = null;
                GC.Collect();

                return $"{count:N0} items ({FormatBytes(bytesUsed)} backing array)";
            });

            // ── Test 4: Dictionary<int,int> ─────────────────────────────
            // Dictionaries use several internal arrays (buckets + entries).
            // Stops at the 8 GB cap (estimated) or OOM.
            RunTest("Dictionary<int,int> — Grow Until Cap/OOM", () =>
            {
                var dict = new Dictionary<int, int>();
                int count = 0;
                try
                {
                    while (true)
                    {
                        if ((long)count * 24L >= MaxAllocationBytes) break;
                        dict.Add(count, count);
                        count++;
                    }
                }
                catch (OutOfMemoryException) { }

                long estimatedBytes = count * 24L;
                dict.Clear();
                dict = null;
                GC.Collect();

                return $"{count:N0} entries (~{FormatBytes(estimatedBytes)} estimated)";
            });

            // ── Test 5: StringBuilder ────────────────────────────────────
            // StringBuilder uses linked chunks internally. Stops at 8 GB cap.
            RunTest("StringBuilder — Append 10M-char Blocks", () =>
            {
                var sb = new System.Text.StringBuilder();
                string chunk = new string('X', 10_000_000);
                long totalChars = 0;
                try
                {
                    while (true)
                    {
                        long projectedBytes = (totalChars + chunk.Length) * 2L;
                        if (projectedBytes > MaxAllocationBytes) break;
                        sb.Append(chunk);
                        totalChars += chunk.Length;
                    }
                }
                catch (OutOfMemoryException) { }

                long bytesUsed = totalChars * 2L;

                sb = null;
                chunk = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                return $"{totalChars:N0} chars ({FormatBytes(bytesUsed)} in memory)";
            });

            // ── Test 6: Queue with many 1 MB items ──────────────────────
            // Similar to Test 2 but with 1 MB granularity and a Queue.
            RunTest("Queue<byte[]> — Enqueue 1 MB Items", () =>
            {
                const int itemSize = 1 * 1024 * 1024;
                var queue = new Queue<byte[]>();
                long totalAllocated = 0;
                try
                {
                    while (totalAllocated + itemSize <= MaxAllocationBytes)
                    {
                        queue.Enqueue(new byte[itemSize]);
                        totalAllocated += itemSize;
                    }
                }
                catch (OutOfMemoryException) { }

                int count = queue.Count;
                string peakMem = FormatBytes(GetPeakWorkingSet());
                queue.Clear();
                queue = null;
                GC.Collect();

                return $"{count:N0} items × 1 MB = {FormatBytes(totalAllocated)}  (Peak WS: {peakMem})";
            });

            // ── Summary tables ──────────────────────────────────────────
            Console.WriteLine();
            PrintSection("WHY THE NUMBERS DIFFER: CONTIGUOUS vs. FRAGMENTED");
            Console.WriteLine($@"
  All tests are capped at {FormatBytes(MaxAllocationBytes)} to avoid exhausting system resources.
  32-bit builds will still hit OOM well before that cap.

  Tests 1, 3, 4 need a SINGLE large contiguous block and hit OOM sooner.
  Tests 2, 5, 6 scatter many small allocations — they can use fragmented
  address space regions the contiguous tests cannot reach.

  In 32-bit mode, the virtual address space is ~2 GB (or ~4 GB with
  LARGEADDRESSAWARE). The CLR, loaded DLLs, thread stacks, and the GC's
  own bookkeeping all carve out portions of that space, leaving only
  ~1.2–1.7 GB for the largest single managed array.
");

            PrintSection("PLATFORM TARGET CHEAT SHEET");
            Console.WriteLine(@"
  ┌───────────────────────────┬──────────┬───────────────────────────────────┐
  │ Platform Target            │ Bitness  │ Typical Max Managed Heap          │
  ├───────────────────────────┼──────────┼───────────────────────────────────┤
  │ Any CPU + Prefer 32-bit   │ 32-bit   │ ~1.4 – 1.7 GB  (WoW64 limit)    │
  │ x86                       │ 32-bit   │ ~1.4 – 1.7 GB  (native 32-bit)  │
  │ Any CPU  (no Prefer32Bit) │ 64-bit*  │ Limited only by OS / RAM         │
  │ x64                       │ 64-bit   │ Limited only by OS / RAM         │
  └───────────────────────────┴──────────┴───────────────────────────────────┘
  * On a 64-bit OS.  On a 32-bit OS, Any CPU still runs as 32-bit.

  KEY TAKEAWAY
  ────────────
  👉 ""Prefer 32-bit"" is the Visual Studio DEFAULT for new console apps.
    It forces 32-bit execution even on a 64-bit OS, capping the heap.
  👉 OutOfMemoryException is thrown while the machine still has GIGABYTES
    of free physical RAM — the process simply cannot address more.
  👉 To unlock full memory, either:
      1. Uncheck ""Prefer 32-bit"" in Project Properties → Build, or
      2. Set Platform Target to x64 explicitly.
");

            PrintSection("HOW TO BUILD & RUN ALL FOUR CONFIGURATIONS");
            Console.WriteLine(@"
  From a Developer Command Prompt for VS:

    msbuild DNFConsoleMemDemo.csproj /p:Configuration=AnyCPU_Prefer32 /p:Platform=AnyCPU
    bin\AnyCPU_Prefer32\DNFConsoleMemDemo.exe

    msbuild DNFConsoleMemDemo.csproj /p:Configuration=AnyCPU_No32 /p:Platform=AnyCPU
    bin\AnyCPU_No32\DNFConsoleMemDemo.exe

    msbuild DNFConsoleMemDemo.csproj /p:Configuration=Release_x86 /p:Platform=x86
    bin\x86\Release_x86\DNFConsoleMemDemo.exe

    msbuild DNFConsoleMemDemo.csproj /p:Configuration=Release_x64 /p:Platform=x64
    bin\x64\Release_x64\DNFConsoleMemDemo.exe

  Compare the numbers across runs to see the 32-bit ceiling in action.
");

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Press any key to exit...");
            Console.ResetColor();
            Console.ReadKey(true);
        }

        // ──────────────────── Helpers ────────────────────

        static void RunTest(string name, Func<string> test)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  🧪 {name}");
            Console.ResetColor();
            Console.Write(" ... ");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var sw = Stopwatch.StartNew();
            try
            {
                string result = test();
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Done");
                Console.ResetColor();
                Console.WriteLine($"    Result : {result}");
                Console.WriteLine($"    Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
                Console.ResetColor();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Console.WriteLine();
        }

        static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(@"
  ╔═══════════════════════════════════════════════════════════════════╗
  ║    .NET Framework 4.8 — Memory Allocation Limits Demo             ║
  ║    Platform Target & Prefer 32-bit Flag Effects                   ║
  ╚═══════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
        }

        static void PrintSection(string title)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ── {title} {"".PadRight(Math.Max(1, 60 - title.Length), '─')}");
            Console.ResetColor();
        }

        static void PrintEnvironmentInfo()
        {
            Console.WriteLine();
            PrintSection("RUNTIME ENVIRONMENT");

            string processArch = Environment.Is64BitProcess ? "64-bit" : "32-bit";
            string osArch = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
            long totalPhysicalMem = GetTotalPhysicalMemory();

            Console.WriteLine($"  OS              : {Environment.OSVersion} ({osArch})");
            Console.WriteLine($"  CLR Version     : {Environment.Version}");
            Console.WriteLine($"  Process Bitness : {processArch}");
            Console.WriteLine($"  IntPtr.Size     : {IntPtr.Size} bytes ({IntPtr.Size * 8}-bit pointers)");
            Console.WriteLine($"  Physical RAM    : {FormatBytes(totalPhysicalMem)}");
            Console.WriteLine($"  GC Mode         : {(System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation")}");

            using (var proc = Process.GetCurrentProcess())
            {
                Console.WriteLine($"  Working Set     : {FormatBytes(proc.WorkingSet64)}");
                Console.WriteLine($"  Virtual Memory  : {FormatBytes(proc.VirtualMemorySize64)}");
            }

            Console.ForegroundColor = ConsoleColor.White;
            if (Environment.Is64BitProcess)
                Console.WriteLine("\n  ⭐ Running as 64-BIT process — full address space available.");
            else if (Environment.Is64BitOperatingSystem)
                Console.WriteLine("\n  ⚠️ Running as 32-BIT process on 64-bit OS — heap capped at ~1.5 GB!");
            else
                Console.WriteLine("\n  ⚠️ Running as 32-BIT process on 32-bit OS — heap capped at ~1.5 GB!");
            Console.ResetColor();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        static long GetTotalPhysicalMemory()
        {
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref memStatus))
                return (long)memStatus.ullTotalPhys;
            return -1;
        }

        static long GetPeakWorkingSet()
        {
            try
            {
                using (var proc = Process.GetCurrentProcess())
                    return proc.PeakWorkingSet64;
            }
            catch { return -1; }
        }

        static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "N/A";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }
            return $"{value:F2} {units[unitIndex]}";
        }
    }
}
