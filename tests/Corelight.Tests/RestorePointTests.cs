using Microsoft.Extensions.Logging.Abstractions;
using Corelight.Core.Platform;
using Corelight.Core.SystemInfo;

namespace Corelight.Tests;

public sealed class RestorePointTests
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string Frequency = "SystemRestorePointCreationFrequency";

    [Theory]
    [InlineData(0u, RestorePointResult.Created)]
    [InlineData(5u, RestorePointResult.Failed)]
    [InlineData(1058u, RestorePointResult.ProtectionDisabled)]
    public async Task Frequency_is_restored_after_success_or_windows_error(uint code, RestorePointResult expected)
    {
        var registry = new FakeRegistry();
        registry.SetValue(Key, Frequency, RegValue.DWord(120));
        var access = new RestoreAccess { Code = code };
        var service = new RestorePointService(registry, NullLogger<RestorePointService>.Instance, access);
        Assert.Equal(expected, await service.CreateAsync("Test"));
        Assert.Equal("120", registry.GetValue(Key, Frequency)!.Data);
        if (code == 5) Assert.Contains("00000005", service.LastError);
    }

    [Fact]
    public async Task Successful_wmi_call_without_new_point_does_not_claim_disabled_protection()
    {
        var service = new RestorePointService(new FakeRegistry(), NullLogger<RestorePointService>.Instance, new RestoreAccess { CreatePoint = false });
        Assert.Equal(RestorePointResult.NotCreated, await service.CreateAsync("Test"));
        Assert.NotNull(service.LastError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Registry_failures_are_reported_or_logged_without_masking_success(bool readFailure)
    {
        var service = new RestorePointService(new FailingRegistry(readFailure), NullLogger<RestorePointService>.Instance, new RestoreAccess());
        Assert.Equal(readFailure ? RestorePointResult.Failed : RestorePointResult.Created, await service.CreateAsync("Test"));
    }

    private sealed class RestoreAccess : ISystemRestoreAccess
    {
        public uint Code { get; init; }
        public bool CreatePoint { get; init; } = true;
        private uint _sequence;
        public uint LatestSequenceNumber() => _sequence;
        public uint Create(string description) { if (Code == 0 && CreatePoint) _sequence++; return Code; }
        public uint Enable(string drive) => 0;
    }

    private sealed class FailingRegistry(bool readFailure) : IRegistryAccess
    {
        public RegValue? GetValue(string keyPath, string name) => readFailure ? throw new UnauthorizedAccessException("Read denied") : null;
        public void SetValue(string keyPath, string name, RegValue value) { }
        public void DeleteValue(string keyPath, string name) => throw new UnauthorizedAccessException("Cleanup denied");
        public IReadOnlyList<string> GetValueNames(string keyPath) => [];
        public IReadOnlyList<string> GetSubKeyNames(string keyPath) => [];
    }
}
