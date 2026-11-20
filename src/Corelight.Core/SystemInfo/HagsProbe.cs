using System.Runtime.InteropServices;

namespace Corelight.Core.SystemInfo;

/// <summary>
/// Определяет, поддерживает ли видеодрайвер аппаратное планирование GPU (HAGS),
/// тем же запросом к ядру графики, что и окно «Параметры графики» Windows.
/// </summary>
internal static partial class HagsProbe
{
    private const int KmtqaitypeWddm27Caps = 70;
    private const uint HwSchSupportedBit = 0x1;
    private const uint HwSchEnabledBit = 0x2;

    public static bool? IsSupported() => Read()?.Supported;

    /// <returns>Поддержка и текущее состояние HAGS либо null, если драйвер не ответил.</returns>
    public static unsafe (bool Supported, bool Enabled)? Read()
    {
        try
        {
            var enumArgs = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.NumAdapters == 0)
                return null;

            var adapters = new D3DKMT_ADAPTERINFO[enumArgs.NumAdapters];
            fixed (D3DKMT_ADAPTERINFO* p = adapters)
            {
                enumArgs.pAdapters = p;
                if (D3DKMTEnumAdapters2(ref enumArgs) != 0)
                    return null;
            }

            (bool Supported, bool Enabled)? result = null;
            for (var i = 0; i < enumArgs.NumAdapters; i++)
            {
                uint caps = 0;
                var query = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = adapters[i].hAdapter,
                    Type = KmtqaitypeWddm27Caps,
                    pPrivateDriverData = &caps,
                    PrivateDriverDataSize = sizeof(uint),
                };
                if (D3DKMTQueryAdapterInfo(ref query) == 0)
                {
                    var supported = (caps & HwSchSupportedBit) != 0;
                    result = (result?.Supported == true || supported,
                        result?.Enabled == true || (supported && (caps & HwSchEnabledBit) != 0));
                }

                var close = new D3DKMT_CLOSEADAPTER { hAdapter = adapters[i].hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
            return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public D3DKMT_ADAPTERINFO* pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public void* pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);
}
