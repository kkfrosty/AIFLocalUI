using System;
using System.Diagnostics;
using System.Linq;
using System.Timers; // explicit

namespace AiFoundryUI.Services;

public class SystemMetrics
{
    public float CpuPercent { get; set; }
    public float MemoryPercent { get; set; }
    public float DiskPercent { get; set; }
    public float GpuPercent { get; set; }
}

public class SystemMonitor
{
    public event EventHandler<SystemMetrics>? MetricsUpdated;

    private readonly PerformanceCounter _cpu = new("Processor", "% Processor Time", "_Total", true);
    private readonly PerformanceCounter _memCommitted = new("Memory", "% Committed Bytes In Use");
    private PerformanceCounter? _disk; // PhysicalDisk% Disk Time_Total
    private PerformanceCounter[] _gpuEngines = Array.Empty<PerformanceCounter>(); // GPU EngineUtilization Percentage*

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
            GpuPercent = _gpuEngines.Length > 0 ? Math.Min(100f, _gpuEngines.Sum(SafeRead)) : 0f
        };
        MetricsUpdated?.Invoke(this, metrics);
    }

    private static float SafeRead(PerformanceCounter c)
    {
        try { return c.NextValue(); }
        catch { return 0f; }
    }
}
