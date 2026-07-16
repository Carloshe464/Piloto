using System.Runtime.InteropServices;

namespace Piloto.Core.Services;

/// <summary>
/// Memória disponível da máquina (física e de commit) via GlobalMemoryStatusEx.
/// Usada para escolher qual modelo (Whisper/LLM) cabe em cada máquina e para o guard
/// que evita crash nativo por falta de memória na carga.
/// </summary>
public static class MemoriaDisponivel
{
    public static bool TentarObter(out long fisicaBytes, out long commitBytes)
    {
        fisicaBytes = 0;
        commitBytes = 0;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return false;

        fisicaBytes = (long)status.ullAvailPhys;
        commitBytes = (long)status.ullAvailPageFile;
        return true;
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
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
