using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Cya2.Core.Services;
using Microsoft.VSDiagnostics;

[ShortRunJob]
[CPUUsageDiagnoser]
public class DonationFrequencyBenchmarks
{
    private readonly DonorFrequencyService _service = new();
    private DonorGiftRecord[] _history = null!;
    [Params(60, 240)]
    public int Gifts { get; set; }

    [GlobalSetup]
    public void Setup() => _history = Enumerable.Range(0, Gifts).Select(i => new DonorGiftRecord { Date = new DateTime(2000, 1, 5).AddMonths(i), Amount = 25m }).ToArray();
    [Benchmark]
    public object CurrentFrequency() => _service.ClassifyDonor(_history);
    [Benchmark]
    public object LatestGift() => _service.ClassifyGift(_history[^1], _history);
}