using Microsoft.Extensions.Logging.Abstractions;
using Corelight.Core.SystemInfo;
using System.Runtime.InteropServices;

namespace Corelight.Tests;

public sealed class RestorePointTests
{

    [Theory]
    [InlineData(unchecked((int)0x80070422), RestorePointResult.ProtectionDisabled)]
    [InlineData(unchecked((int)0x80070005), RestorePointResult.Failed)]
    public async Task Wmi_com_errors_are_classified_and_empty_messages_show_the_windows_code(int code, RestorePointResult expected)
    {
        var service = new RestorePointService(NullLogger<RestorePointService>.Instance,
            new RestoreAccess { CreateError = new COMException("", code) });

        Assert.Equal(expected, await service.CreateAsync("Test"));
        Assert.Contains(unchecked((uint)code).ToString("X8"), service.LastError);
    }

    [Theory]
    [InlineData(0u, RestorePointResult.Created)]
    [InlineData(5u, RestorePointResult.Failed)]
    [InlineData(1058u, RestorePointResult.ProtectionDisabled)]
    public async Task Reports_success_or_windows_error(uint code, RestorePointResult expected)
    {
        var access = new RestoreAccess { Code = code };
        var service = new RestorePointService(NullLogger<RestorePointService>.Instance, access);
        Assert.Equal(expected, await service.CreateAsync("Test"));
        if (code == 5) Assert.Contains("00000005", service.LastError);
    }

    [Fact]
    public async Task Successful_wmi_call_without_new_point_does_not_claim_disabled_protection()
    {
        var service = new RestorePointService(NullLogger<RestorePointService>.Instance, new RestoreAccess { CreatePoint = false });
        Assert.Equal(RestorePointResult.NotCreated, await service.CreateAsync("Test"));
        Assert.NotNull(service.LastError);
    }

    [Fact]
    public async Task Point_that_appears_after_wmi_returns_is_confirmed()
    {
        var access = new RestoreAccess { ReadAttemptsBeforeVisible = 4 };
        var service = new RestorePointService(NullLogger<RestorePointService>.Instance, access);

        Assert.Equal(RestorePointResult.Created, await service.CreateAsync("Test"));
    }

    [Fact]
    public async Task Another_restore_point_does_not_confirm_the_corelight_request()
    {
        var access = new RestoreAccess { CreatePoint = false, ExistingPoint = "Other application" };
        var service = new RestorePointService(NullLogger<RestorePointService>.Instance, access);

        Assert.Equal(RestorePointResult.NotCreated, await service.CreateAsync("Test"));
        Assert.NotEqual("Test", access.Request);
        Assert.NotEqual(access.ExistingPoint, access.Request);
    }

    private sealed class RestoreAccess : ISystemRestoreAccess
    {
        public uint Code { get; init; }
        public bool CreatePoint { get; init; } = true;
        public Exception? CreateError { get; init; }
        public int ReadAttemptsBeforeVisible { get; init; }
        public string? ExistingPoint { get; init; }
        public string? Request { get; private set; }
        private bool _created;
        private int _readsAfterCreate;
        public bool ContainsPoint(string description) =>
            string.Equals(ExistingPoint, description, StringComparison.Ordinal)
            || (_created && ++_readsAfterCreate > ReadAttemptsBeforeVisible && description == Request);
        public uint Create(string description)
        {
            if (CreateError is not null) throw CreateError;
            Request = description;
            if (Code == 0 && CreatePoint) _created = true;
            return Code;
        }
        public uint Enable(string drive) => 0;
    }

}
