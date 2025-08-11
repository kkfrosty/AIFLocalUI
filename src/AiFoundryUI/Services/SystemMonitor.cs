using System;
using System.Diagnostics;
using System.Linq;
using System.Timers; // explicit
using System.Management;

namespace AiFoundryUI.Services;

public class SystemMetrics
{
    public float CpuPercent { get; set; }
    public float MemoryPercent { get; set; }
    public float DiskPercent { get; set; }
    public float GpuPercent { get; set; }
    public string? GpuName { get; set; }
    public float GpuMemUsedMB { get; set; }
    public float GpuMemTotalMB { get; set; }
}

public class SystemMonitor
{
    public event EventHandler<SystemMetrics>? MetricsUpdated;

    private readonly PerformanceCounter _cpu = new("Processor", "% Processor Time", "_Total", true);
    private readonly PerformanceCounter _memCommitted = new("Memory", "% Committed Bytes In Use");
    private PerformanceCounter? _disk; // PhysicalDisk% Disk Time_Total
    private PerformanceCounter[] _gpuEngines = Array.Empty<PerformanceCounter>(); // GPU EngineUtilization Percentage*
    private (string instance, PerformanceCounter usage, PerformanceCounter limit)[] _gpuMem = Array.Empty<(string, PerformanceCounter, PerformanceCounter)>();
    private string _gpuPreferredName = "GPU";

    private readonly System.Timers.Timer _timer;

    public SystemMonitor()
    {
        try { _disk = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true); }
        catch { _disk = null; }

        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            var instances = cat.GetInstanceNames()
                               .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase) ||
                                           n.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase) ||
                                           n.Contains("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase))
                               .ToArray();
            _gpuEngines = instances.Select(inst => new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true)).ToArray();
        }
        catch
        {
            _gpuEngines = Array.Empty<PerformanceCounter>();
        }

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

        // Try to set up GPU memory counters (Dedicated Usage/Limit)
        try
        {
            var memCat = new PerformanceCounterCategory("GPU Adapter Memory");
            var memInstances = memCat.GetInstanceNames();
            _gpuMem = memInstances
                .Select(inst =>
                {
                    try
                    {
                        var usage = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, true);
                        var limit = new PerformanceCounter("GPU Adapter Memory", "Dedicated Limit", inst, true);
                        // Prime
                        _ = usage.NextValue();
                        _ = limit.NextValue();
            return (instance: inst, usage: usage, limit: limit);
                    }
                    catch
                    {
            return default((string instance, PerformanceCounter usage, PerformanceCounter limit));
                    }
                })
        .Where(t => t.usage != null && t.limit != null && !string.IsNullOrEmpty(t.instance))
                .ToArray();
        }
        catch
        {
            _gpuMem = Array.Empty<(string, PerformanceCounter, PerformanceCounter)>();
        }

        _ = _cpu.NextValue();
        _ = _memCommitted.NextValue();
        if (_disk != null) _ = _disk.NextValue();
        foreach (var g in _gpuEngines) _ = g.NextValue();

        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (_, __) => Sample();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Sample()
    {
        var metrics = new SystemMetrics
        {
            CpuPercent = SafeRead(_cpu),
            MemoryPercent = SafeRead(_memCommitted),
            DiskPercent = _disk != null ? SafeRead(_disk) : 0f,
            GpuPercent = _gpuEngines.Length > 0 ? Math.Min(100f, _gpuEngines.Sum(SafeRead)) : 0f,
            GpuName = _gpuPreferredName
        };

        // Read GPU memory usage if counters are available
        if (_gpuMem.Length > 0)
        {
            try
            {
                // Prefer the instance with the largest limit (likely discrete GPU)
                var readings = _gpuMem.Select(t => new
                {
                    t.instance,
                    used = SafeRead(t.usage),
                    limit = SafeRead(t.limit)
                }).ToList();

                var best = readings
                    .OrderByDescending(r => r.limit)
                    .FirstOrDefault();
                if (best != null && best.limit > 0)
                {
                    metrics.GpuMemUsedMB = best.used / (1024f * 1024f);
                    metrics.GpuMemTotalMB = best.limit / (1024f * 1024f);
                    // If engine counters are missing, compute GPU % from memory usage ratio (approx)
                    if (_gpuEngines.Length == 0)
                    {
                        metrics.GpuPercent = Math.Clamp((metrics.GpuMemUsedMB / Math.Max(1f, metrics.GpuMemTotalMB)) * 100f, 0f, 100f);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
        MetricsUpdated?.Invoke(this, metrics);
    }

    private static float SafeRead(PerformanceCounter c)
    {
        try { return c.NextValue(); }
        catch { return 0f; }
    }
}
