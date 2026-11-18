using Microsoft.Extensions.Logging.Abstractions;
using Corelight.Core.Platform;
using Corelight.Core.SystemInfo;
using System.Runtime.InteropServices;

namespace Corelight.Tests;

public sealed class RestorePointTests
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string Frequency = "SystemRestorePointCreationFrequency";

    [Theory]
    [InlineData(unchecked((int)0x80070422), RestorePointResult.ProtectionDisabled)]
    [InlineData(unchecked((int)0x80070005), RestorePointResult.Failed)]
    public async Task Wmi_com_errors_are_classified_and_empty_messages_show_the_windows_code(int code, RestorePointResult expected)
    {
        var registry = new FakeRegistry();
        registry.SetValue(Key, Frequency, RegValue.DWord(120));
        var service = new RestorePointService(registry, NullLogger<RestorePointService>.Instance,
            new RestoreAccess { CreateError = new COMException("", code) });

        Assert.Equal(expected, await service.CreateAsync("Test"));
        Assert.Contains(unchecked((uint)code).ToString("X8"), service.LastError);
        Assert.Equal("120", registry.GetValue(Key, Frequency)!.Data);
    }

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

    [Fact]
    public async Task Point_that_appears_after_wmi_returns_is_confirmed()
    {
        var access = new RestoreAccess { ReadAttemptsBeforeVisible = 4 };
        var service = new RestorePointService(new FakeRegistry(), NullLogger<RestorePointService>.Instance, access);

        Assert.Equal(RestorePointResult.Created, await service.CreateAsync("Test"));
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
        public Exception? CreateError { get; init; }
        public int ReadAttemptsBeforeVisible { get; init; }
        private uint _sequence;
        private bool _created;
        private int _readsAfterCreate;
        public uint LatestSequenceNumber()
        {
            if (_created && ++_readsAfterCreate > ReadAttemptsBeforeVisible)
            {
                _sequence++;
                _created = false;
            }
            return _sequence;
        }
        public uint Create(string description)
        {
            if (CreateError is not null) throw CreateError;
            if (Code == 0 && CreatePoint) _created = true;
            return Code;
        }
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
