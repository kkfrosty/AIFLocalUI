using System;
using System.Diagnostics;
using System.Linq;
using System.Timers; // explicit
using System.Management;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AiFoundryUI.Services;

public class AcceleratorMetrics
{
    public string Name { get; set; } = string.Empty; // Friendly Name (GPU0 RTX 4090, NPU0, etc.)
    public string Type { get; set; } = "GPU"; // GPU or NPU (future types possible)
    public float UtilPercent { get; set; }
    public float MemUsedMB { get; set; }
    public float MemTotalMB { get; set; }
    public int? GpuIndex { get; set; } // Task Manager GPU index
    public float MemUsedGB => MemUsedMB / 1024f;
    public float MemTotalGB => MemTotalMB / 1024f;
}

public class SystemMetrics
{
    public float CpuPercent { get; set; }
    public float MemoryPercent { get; set; }
    public float DiskPercent { get; set; }

    // Legacy single-GPU fields retained for existing UI bindings; populated with first GPU if present.
    public float GpuPercent { get; set; }
    public string? GpuName { get; set; }
    public float GpuMemUsedMB { get; set; }
    public float GpuMemTotalMB { get; set; }

    public List<AcceleratorMetrics> Accelerators { get; set; } = new();
}

public class SystemMonitor
{
    public event EventHandler<SystemMetrics>? MetricsUpdated;

    private readonly PerformanceCounter _cpu = new("Processor", "% Processor Time", "_Total", true);
    private readonly PerformanceCounter _memCommitted = new("Memory", "% Committed Bytes In Use");
    private PerformanceCounter? _disk; // PhysicalDisk% Disk Time_Total
    // Raw GPU engine counters (all instances)
    private PerformanceCounter[] _gpuEngines = Array.Empty<PerformanceCounter>(); // GPU Engine Utilization Percentage*
    // Memory counters grouped by instance (Dedicated & Shared). Instance names contain a LUID prefix we can use to group.
    private class GpuMemCounters
    {
        public string Instance = string.Empty;
        public PerformanceCounter? DedicatedUsage;
        public PerformanceCounter? DedicatedLimit;
        public PerformanceCounter? SharedUsage;
        public PerformanceCounter? SharedLimit;
    }
    private GpuMemCounters[] _gpuMem = Array.Empty<GpuMemCounters>();
    private string _gpuPreferredName = "GPU"; // First (largest RAM) GPU name for legacy fields.

    // Attempt detection of NPU engine category (Windows 11 on some hardware). Not guaranteed.
    private PerformanceCounter[] _npuEngines = Array.Empty<PerformanceCounter>();

    // DXGI adapter enumeration (to map Task Manager GPU numbers)
    private class DxgiAdapterInfo
    {
        public int Index; // Task Manager style GPU#
        public string Description = string.Empty;
        public string LuidKey1 = string.Empty; // luid_0xHIGH_0xLOW
        public string LuidKey2 = string.Empty; // luid_0xLOW_0xHIGH (to be safe)
    public ulong DedicatedBytes; // from DXGI
    }
    private List<DxgiAdapterInfo> _dxgiAdapters = new();

    // nvidia-smi fallback cache
    private DateTime _lastNvidiaQueryUtc = DateTime.MinValue;
    private Dictionary<int, (float usedMB, float totalMB)> _nvidiaMemory = new(); // key: local NVIDIA index (nvidia-smi index)
    
    // WMI memory fallback cache
    private DateTime _lastWmiQueryUtc = DateTime.MinValue;
    private Dictionary<int, (float totalMB, string name)> _wmiMemory = new(); // key: adapter index
    
    // Process memory estimation cache
    private DateTime _lastProcessQueryUtc = DateTime.MinValue;
    private float _estimatedGpuUsageMB = 0f;

    private readonly System.Timers.Timer _timer;

    public SystemMonitor()
    {
        try { _disk = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true); }
        catch { _disk = null; }

        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            var instances = cat.GetInstanceNames()
                               .Where(n => n.Contains("engtype_", StringComparison.OrdinalIgnoreCase))
                               .ToArray();
            _gpuEngines = instances.Select(inst => new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true)).ToArray();
        }
        catch { _gpuEngines = Array.Empty<PerformanceCounter>(); }

        // Optional NPU engine counters (best-effort; ignore failures)
        try
        {
            if (PerformanceCounterCategory.Exists("NPU Engine"))
            {
                var npuCat = new PerformanceCounterCategory("NPU Engine");
                var npuInstances = npuCat.GetInstanceNames();
                _npuEngines = npuInstances.Select(inst => new PerformanceCounter("NPU Engine", "Utilization Percentage", inst, true)).ToArray();
            }
        }
        catch { _npuEngines = Array.Empty<PerformanceCounter>(); }

        // Try to get GPU adapter name(s)
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            var gpus = searcher.Get().Cast<ManagementObject>()
                .Select(mo => new
                {
                    Name = (mo["Name"] as string) ?? "GPU",
                    Ram = (mo["AdapterRAM"] is null) ? 0L : Convert.ToInt64(mo["AdapterRAM"])
                })
                .OrderByDescending(g => g.Ram)
                .ToList();
            if (gpus.Count > 0)
            {
                _gpuPreferredName = gpus.First().Name;
            }
        }
        catch
        {
            _gpuPreferredName = "GPU";
        }

        // Try to set up GPU memory counters (Dedicated & Shared Usage/Limit)
        try
        {
            var memCat = new PerformanceCounterCategory("GPU Adapter Memory");
            var memInstances = memCat.GetInstanceNames();
            var list = new List<GpuMemCounters>();
            foreach (var inst in memInstances)
            {
                var entry = new GpuMemCounters { Instance = inst };
                try { entry.DedicatedUsage = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, true); _ = entry.DedicatedUsage.NextValue(); } catch { entry.DedicatedUsage = null; }
                try { entry.DedicatedLimit = new PerformanceCounter("GPU Adapter Memory", "Dedicated Limit", inst, true); _ = entry.DedicatedLimit.NextValue(); } catch { entry.DedicatedLimit = null; }
                try { entry.SharedUsage = new PerformanceCounter("GPU Adapter Memory", "Shared Usage", inst, true); _ = entry.SharedUsage.NextValue(); } catch { entry.SharedUsage = null; }
                try { entry.SharedLimit = new PerformanceCounter("GPU Adapter Memory", "Shared Limit", inst, true); _ = entry.SharedLimit.NextValue(); } catch { entry.SharedLimit = null; }
                if (entry.DedicatedUsage != null || entry.SharedUsage != null)
                    list.Add(entry);
            }
            _gpuMem = list.ToArray();
        }
        catch
        {
            _gpuMem = Array.Empty<GpuMemCounters>();
        }

        _ = _cpu.NextValue();
        _ = _memCommitted.NextValue();
        if (_disk != null) _ = _disk.NextValue();
        foreach (var g in _gpuEngines) _ = g.NextValue();

    _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (_, __) => Sample();

    // Enumerate DXGI adapters once (best-effort)
    try { EnumerateDxgiAdapters(); } catch { /* ignore */ }
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Sample()
    {
        var metrics = new SystemMetrics
        {
            CpuPercent = SafeRead(_cpu),
            MemoryPercent = SafeRead(_memCommitted),
            DiskPercent = _disk != null ? SafeRead(_disk) : 0f
        };

        // --- Aggregate GPU metrics ---
        try
        {
            // Group engine counters by LUID prefix (portion before first '_engtype')
            var engineGroups = _gpuEngines
                .GroupBy(pc => ExtractLuid(pc.InstanceName))
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .ToDictionary(g => g.Key!, g => g.ToArray());

            // Memory readings (Dedicated + Shared) by instance name (already contains luid)
            var memReadings = _gpuMem.Select(t =>
            {
                float dedUsed = t.DedicatedUsage != null ? SafeRead(t.DedicatedUsage) : 0f;
                float dedLimit = t.DedicatedLimit != null ? SafeRead(t.DedicatedLimit) : 0f;
                float shUsed = t.SharedUsage != null ? SafeRead(t.SharedUsage) : 0f;
                float shLimit = t.SharedLimit != null ? SafeRead(t.SharedLimit) : 0f;
                var luid = ExtractLuid(t.Instance); // may be null; keep entry
                return new { t.Instance, Luid = luid, dedUsed, dedLimit, shUsed, shLimit };
            }).ToList();

            // GPU names via WMI (ordered by RAM desc); assign sequential GPU indices
            List<string> gpuNames = new();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
                gpuNames = searcher.Get().Cast<ManagementObject>()
                    .Select(mo => new
                    {
                        Name = (mo["Name"] as string) ?? "GPU",
                        Ram = (mo["AdapterRAM"] is null) ? 0L : Convert.ToInt64(mo["AdapterRAM"])
                    })
                    .OrderByDescending(g => g.Ram)
                    .Select((g, i) => $"GPU{i} {g.Name}")
                    .ToList();
            }
            catch { /* ignore */ }

            // Create LUID -> DXGI index map
            var luidToIndex = _dxgiAdapters
                .SelectMany(a => new[] { new { key = a.LuidKey1, idx = a.Index, desc = a.Description }, new { key = a.LuidKey2, idx = a.Index, desc = a.Description } })
                .GroupBy(t => t.key)
                .ToDictionary(g => g.Key, g => g.First());

            // Build accelerator list for GPUs preserving enumeration order; map Task Manager GPU# via LUID
            // Track which DXGI indices already assigned
            var assignedDxgi = new HashSet<int>();

            foreach (var mem in memReadings)
            {
                int? gpuIndex = null;
                string rawDesc = string.Empty;

                // First: LUID match if present
                if (!string.IsNullOrEmpty(mem.Luid))
                {
                    var key = mem.Luid.ToLowerInvariant();
                    if (luidToIndex.TryGetValue(key, out var map))
                    {
                        gpuIndex = map.idx;
                        rawDesc = map.desc;
                        assignedDxgi.Add(map.idx);
                    }
                }

                // If still unknown, try to match by dedicated limit size to unassigned DXGI adapters
                if (gpuIndex == null)
                {
                    // Use dedicated limit if >0, else shared limit (bounded) for sizing
                    ulong sizeGuess = (ulong)(mem.dedLimit > 0 ? mem.dedLimit : 0);
                    if (sizeGuess == 0 && mem.shLimit > 0)
                    {
                        // ignore absurd shared ( >64GB )
                        if ((mem.shLimit / (1024f * 1024f * 1024f)) <= 64f)
                            sizeGuess = (ulong)mem.shLimit;
                    }
                    if (sizeGuess > 0)
                    {
                        var candidate = _dxgiAdapters
                            .Where(a => !assignedDxgi.Contains(a.Index) && a.DedicatedBytes > 0)
                            .OrderBy(a => Math.Abs((long)a.DedicatedBytes - (long)sizeGuess))
                            .FirstOrDefault();
                        if (candidate != null && candidate.DedicatedBytes > 0)
                        {
                            gpuIndex = candidate.Index;
                            rawDesc = candidate.Description;
                            assignedDxgi.Add(candidate.Index);
                        }
                    }
                }

                // Utilization
                string luidKeyLower = !string.IsNullOrEmpty(mem.Luid) ? mem.Luid.ToLowerInvariant() : string.Empty;
                float util = 0f;
                if (!string.IsNullOrEmpty(luidKeyLower) && engineGroups.TryGetValue(luidKeyLower, out var counters))
                {
                    util = counters.Sum(SafeRead);
                    if (util > 100f) util = 100f;
                }
                // Dedicated preferred for totals/usage
                float usedBytes = 0f;
                float limitBytes = 0f;
                if (mem.dedLimit > 0)
                {
                    usedBytes = mem.dedUsed;
                    limitBytes = mem.dedLimit;
                }
                else if (mem.shLimit > 0 && (mem.shLimit / (1024f * 1024f * 1024f)) <= 64f)
                {
                    usedBytes = mem.shUsed;
                    limitBytes = mem.shLimit;
                }
                // Fallback to DXGI dedicated bytes if still zero
                if (limitBytes == 0 && gpuIndex != null)
                {
                    var dx = _dxgiAdapters.FirstOrDefault(a => a.Index == gpuIndex.Value);
                    if (dx != null && dx.DedicatedBytes > 0)
                        limitBytes = dx.DedicatedBytes;
                }
                if (limitBytes > 0 && usedBytes > limitBytes) usedBytes = limitBytes;

                // Choose name
                string baseName;
                if (!string.IsNullOrWhiteSpace(rawDesc))
                    baseName = ShortenAdapterName(rawDesc);
                else
                {
                    baseName = gpuIndex != null ? $"GPU{gpuIndex.Value}" : "GPU";
                }
                if (gpuIndex != null && !baseName.StartsWith($"GPU{gpuIndex.Value}", StringComparison.OrdinalIgnoreCase))
                    baseName = $"GPU{gpuIndex.Value} {baseName}";

                metrics.Accelerators.Add(new AcceleratorMetrics
                {
                    Name = baseName,
                    Type = "GPU",
                    UtilPercent = util,
                    MemUsedMB = usedBytes / (1024f * 1024f),
                    MemTotalMB = limitBytes / (1024f * 1024f),
                    GpuIndex = gpuIndex
                });
            }

            // If we had engines but no memory counters, still create pseudo-entries
            if (metrics.Accelerators.Count == 0 && engineGroups.Count > 0)
            {
                int i = 0;
                foreach (var kvp in engineGroups)
                {
                    var util = kvp.Value.Sum(SafeRead);
                    metrics.Accelerators.Add(new AcceleratorMetrics
                    {
                        Name = $"GPU{i}",
                        Type = "GPU",
                        UtilPercent = Math.Min(100f, util)
                    });
                    i++;
                }
            }

            // --- Enhanced GPU memory correction via multiple sources ---
            TryUpdateNvidiaMemory();
            TryUpdateWmiMemory();
            TryEstimateProcessMemory();
            
            // Apply memory corrections for all GPUs
            foreach (var acc in metrics.Accelerators.Where(a => a.Type == "GPU"))
            {
                // For NVIDIA cards: always prefer nvidia-smi if available
                if (IsNvidiaName(acc.Name) && _nvidiaMemory.Count > 0)
                {
                    var best = _nvidiaMemory
                        .Select(kv => new { kv.Key, kv.Value.usedMB, kv.Value.totalMB, delta = acc.MemTotalMB > 0 ? Math.Abs(acc.MemTotalMB - kv.Value.totalMB) : float.MaxValue })
                        .OrderBy(x => x.delta)
                        .FirstOrDefault();
                    if (best != null && (best.delta < 512f || acc.MemTotalMB == 0))
                    {
                        acc.MemTotalMB = best.totalMB;
                        acc.MemUsedMB = best.usedMB;
                    }
                }
                
                // For all cards: if still zero memory, try WMI fallback
                if (acc.MemTotalMB == 0 && acc.GpuIndex.HasValue && _wmiMemory.ContainsKey(acc.GpuIndex.Value))
                {
                    var wmi = _wmiMemory[acc.GpuIndex.Value];
                    acc.MemTotalMB = wmi.totalMB;
                }
                
                // If we still don't have used memory but have utilization, estimate
                if (acc.MemUsedMB == 0 && acc.UtilPercent > 5f && acc.MemTotalMB > 0)
                {
                    // Use utilization-based estimate or process memory estimate
                    float estimate1 = acc.MemTotalMB * (acc.UtilPercent / 100f) * 0.4f; // Conservative util-based
                    float estimate2 = _estimatedGpuUsageMB; // Process-based estimate
                    acc.MemUsedMB = Math.Max(estimate1, estimate2);
                }
                
                // Final safety: if we have total but no used and it's an active GPU, assume some baseline usage
                if (acc.MemTotalMB > 0 && acc.MemUsedMB == 0 && acc.UtilPercent > 0)
                {
                    acc.MemUsedMB = Math.Min(acc.MemTotalMB * 0.1f, 500f); // At least 10% or 500MB if active
                }
            }

            // Order strictly by GPU index (Task Manager order) then by name; do not reorder vendor first.
            metrics.Accelerators = metrics.Accelerators
                .OrderBy(a => a.Type == "GPU" ? 0 : 1)
                .ThenBy(a => a.GpuIndex ?? int.MaxValue)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Populate legacy single-GPU fields from *first NVIDIA if present otherwise first GPU*
            var firstGpu = metrics.Accelerators.FirstOrDefault(a => a.Type == "GPU");
            if (firstGpu != null)
            {
                metrics.GpuName = firstGpu.Name;
                metrics.GpuPercent = firstGpu.UtilPercent;
                metrics.GpuMemUsedMB = firstGpu.MemUsedMB;
                metrics.GpuMemTotalMB = firstGpu.MemTotalMB;
            }
        }
        catch { /* ignore GPU aggregation errors */ }

        // --- NPU metrics (best-effort) ---
        try
        {
            if (_npuEngines.Length > 0)
            {
                // Aggregate by instance (treat each instance as separate NPU if multiple)
                var grouped = _npuEngines.GroupBy(e => e.InstanceName);
                int idx = 0;
                foreach (var g in grouped)
                {
                    var util = g.Sum(SafeRead);
                    metrics.Accelerators.Add(new AcceleratorMetrics
                    {
                        Name = $"NPU{idx}",
                        Type = "NPU",
                        UtilPercent = Math.Min(100f, util)
                    });
                    idx++;
                }
            }
        }
        catch { /* ignore */ }

        MetricsUpdated?.Invoke(this, metrics);
    }

    private static float SafeRead(PerformanceCounter c)
    {
        try { return c.NextValue(); }
        catch { return 0f; }
    }

    private static string ShortenAdapterName(string full)
    {
        try
        {
            var f = full.Trim();
            // NVIDIA patterns (keep RTX/GTX + number + suffix)
            var nIdx = f.IndexOf("RTX", StringComparison.OrdinalIgnoreCase);
            if (nIdx < 0) nIdx = f.IndexOf("GTX", StringComparison.OrdinalIgnoreCase);
            if (nIdx >= 0)
            {
                var tail = f.Substring(nIdx).Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase).Trim();
                // Remove redundant words
                tail = tail.Replace("GeForce", "", StringComparison.OrdinalIgnoreCase).Trim();
                return tail;
            }
            // Intel patterns
            var intelIdx = f.IndexOf("Intel", StringComparison.OrdinalIgnoreCase);
            if (intelIdx >= 0)
            {
                // Look for Arc / Iris / UHD tokens
                string[] tokens = { "Arc", "Iris Xe", "Iris", "UHD" };
                foreach (var t in tokens)
                {
                    var pos = f.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                    if (pos >= 0)
                    {
                        return ("Intel " + t).Trim();
                    }
                }
                return "Intel GPU";
            }
            // AMD patterns
            var amdIdx = f.IndexOf("AMD", StringComparison.OrdinalIgnoreCase);
            if (amdIdx >= 0)
            {
                var tail = f.Substring(amdIdx).Replace("AMD", "", StringComparison.OrdinalIgnoreCase).Trim();
                tail = tail.Replace("Radeon", "", StringComparison.OrdinalIgnoreCase).Trim();
                return string.IsNullOrWhiteSpace(tail) ? "AMD GPU" : tail;
            }
            // Fallback: last 2 tokens
            var parts = f.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return string.Join(' ', parts.TakeLast(2));
            return f;
        }
        catch { return full; }
    }

    private static string? ExtractLuid(string source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        var match = Regex.Match(source.ToLowerInvariant(), @"luid_0x([0-9a-f]+)_0x([0-9a-f]+)");
        if (!match.Success) return null;
        // Normalize components to 8 hex digits (pad left) to match DXGI formatting
        var hi = match.Groups[1].Value.PadLeft(8,'0');
        var lo = match.Groups[2].Value.PadLeft(8,'0');
        return $"luid_0x{hi}_0x{lo}";
    }

    private static bool IsNvidiaName(string name)
        => name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    private void TryEstimateProcessMemory()
    {
        try
        {
            // Check for GPU-related processes to estimate memory usage
            var processes = System.Diagnostics.Process.GetProcesses();
            float totalEstimate = 0;
            
            foreach (var process in processes)
            {
                try
                {
                    // Look for processes that commonly use GPU memory
                    string name = process.ProcessName.ToLowerInvariant();
                    if (name.Contains("chrome") || name.Contains("firefox") || name.Contains("msedge") ||
                        name.Contains("blender") || name.Contains("unity") || name.Contains("unreal") ||
                        name.Contains("davinci") || name.Contains("premiere") || name.Contains("after") ||
                        name.Contains("obs") || name.Contains("streamlabs") || name.Contains("discord"))
                    {
                        // Estimate GPU memory usage as a fraction of working set
                        long workingSetMB = process.WorkingSet64 / (1024 * 1024);
                        if (workingSetMB > 100) // Only consider significant processes
                        {
                            totalEstimate += workingSetMB * 0.1f; // Assume 10% of working set for GPU
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible processes
                }
            }
            
            _estimatedGpuUsageMB = Math.Min(totalEstimate, 2048f); // Cap at 2GB estimate
            
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        catch
        {
            _estimatedGpuUsageMB = 0;
        }
    }

    private void TryUpdateNvidiaMemory()
    {
        // Limit external calls (every 2 seconds max)
        if (DateTime.UtcNow - _lastNvidiaQueryUtc < TimeSpan.FromSeconds(2)) return;
        _lastNvidiaQueryUtc = DateTime.UtcNow;
        try
        {
            string[] candidateExe = new[]
            {
                "nvidia-smi",
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
                "C\\Program Files\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe",
            };
            string? exe = candidateExe.FirstOrDefault(System.IO.File.Exists) ?? candidateExe[0];
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--query-gpu=index,memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(750);
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var dict = new Dictionary<int, (float usedMB, float totalMB)>();
            foreach (var line in lines)
            {
                // Expected: index, used, total
                var parts = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 3) continue;
                if (int.TryParse(parts[0], out int idx) && float.TryParse(parts[1], out float used) && float.TryParse(parts[2], out float total))
                {
                    dict[idx] = (used, total);
                }
            }
            if (dict.Count > 0)
                _nvidiaMemory = dict;
        }
        catch
        {
            // ignore (likely no NVIDIA card or nvidia-smi not in PATH)
        }
    }

    private void TryUpdateWmiMemory()
    {
        // Update WMI memory info every 5 seconds
        if (DateTime.UtcNow - _lastWmiQueryUtc < TimeSpan.FromSeconds(5)) return;
        _lastWmiQueryUtc = DateTime.UtcNow;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController WHERE AdapterRAM IS NOT NULL");
            var results = searcher.Get().Cast<ManagementObject>().ToList();
            var dict = new Dictionary<int, (float totalMB, string name)>();
            
            for (int i = 0; i < results.Count; i++)
            {
                var mo = results[i];
                var name = (mo["Name"] as string) ?? "GPU";
                var ramObj = mo["AdapterRAM"];
                if (ramObj != null && ulong.TryParse(ramObj.ToString(), out ulong ram) && ram > 0)
                {
                    dict[i] = (ram / (1024f * 1024f), name);
                }
            }
            
            if (dict.Count > 0)
                _wmiMemory = dict;
        }
        catch
        {
            // ignore WMI errors
        }
    }

    #region DXGI Interop
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [System.Runtime.InteropServices.Guid("770aae78-f26f-4dba-a829-253c83d1b387"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // We only need EnumAdapters1; preserve vtable order with placeholders
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent(ref Guid riid, out IntPtr parent);
        int EnumAdapters(uint adapter, out IntPtr ppAdapter); // IDXGIAdapter
        int MakeWindowAssociation(IntPtr hwnd, uint flags);
        int GetWindowAssociation(out IntPtr hwnd);
        int CreateSwapChain();
        int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
        int EnumAdapters1(uint adapter, out IntPtr ppAdapter); // IDXGIAdapter1
        int IsCurrent();
        int IsWindowedStereoEnabled();
        int CreateSwapChainForHwnd();
        int CreateSwapChainForCoreWindow();
        int GetSharedResourceAdapterLuid();
        int RegisterStereoStatusWindow();
        int RegisterStereoStatusEvent();
        int UnregisterStereoStatus();
        int RegisterOcclusionStatusWindow();
        int RegisterOcclusionStatusEvent();
        int UnregisterOcclusionStatus();
        int CreateSwapChainForComposition();
    }

    [System.Runtime.InteropServices.Guid("29038f61-3839-4626-91fd-086879011a05"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent(ref Guid riid, out IntPtr parent);
        int EnumOutputs(uint output, out IntPtr ppOutput);
        int GetDesc(out DXGI_ADAPTER_DESC1 desc); // actually GetDesc for IDXGIAdapter (not used)
        int CheckInterfaceSupport(ref Guid guid, out long umdVersion);
        int GetDesc1(out DXGI_ADAPTER_DESC1 desc1); // IDXGIAdapter1
    }

    [System.Runtime.InteropServices.DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    private void EnumerateDxgiAdapters()
    {
        var guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref guid, out var factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
            return;
        try
        {
            var factory = (IDXGIFactory1)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(factoryPtr);
            uint index = 0;
            while (true)
            {
                if (factory.EnumAdapters1(index, out var adapterPtr) != 0 || adapterPtr == IntPtr.Zero) break;
                try
                {
                    var adapter = (IDXGIAdapter1)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(adapterPtr);
                    if (adapter.GetDesc1(out var desc) == 0)
                    {
                        var key1 = $"luid_0x{desc.AdapterLuid.HighPart:x8}_0x{desc.AdapterLuid.LowPart:x8}".ToLowerInvariant();
                        var key2 = $"luid_0x{desc.AdapterLuid.LowPart:x8}_0x{desc.AdapterLuid.HighPart:x8}".ToLowerInvariant();
                        _dxgiAdapters.Add(new DxgiAdapterInfo
                        {
                            Index = (int)index,
                            Description = desc.Description.Trim(),
                            LuidKey1 = key1,
                            LuidKey2 = key2,
                            DedicatedBytes = (ulong)desc.DedicatedVideoMemory
                        });
                    }
                }
                finally
                {
                    if (adapterPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(adapterPtr);
                }
                index++;
            }
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(factoryPtr);
        }
    }
    #endregion
}
