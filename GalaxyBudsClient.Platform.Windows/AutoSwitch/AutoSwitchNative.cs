using System;
using System.IO;
using System.Runtime.InteropServices;
using GalaxyBudsClient.Platform.Interfaces;
using Serilog;

namespace GalaxyBudsClient.Platform.Windows.AutoSwitch;

public static unsafe class AutoSwitchNative
{
    // --- Samsung Galaxy Buds Constants ---
    public static readonly Guid MdeServiceUuid = new("f8620674-a1ed-41ab-a8b9-de9ad655729d");
    public static readonly Guid GatewayServiceUuid = new("2e73a4ad-332d-41fc-90e2-16bef06523f2");
    public static readonly Guid AudioSinkServiceUuid = new("0000110B-0000-1000-8000-00805F9B34FB");
    public static readonly Guid HandsfreeServiceUuid = new("0000111E-0000-1000-8000-00805F9B34FB");

    // Pre-built MDE AutoSwitch packets verified against Samsung DllBudsMsgFmt.dll
    public static readonly byte[] PacketSettingInfoBt =
        [0xFC, 0x15, 0x00, 0x01, 0x73, 0x01, 0x01, 0x04, 0x00, 0x01, 0x75, 0x00, 0x2A, 0x41, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x55, 0xC6, 0xCC];

    public static readonly byte[] PacketMdeVersionAutoSwitchV3 =
        [0xFC, 0x0E, 0x00, 0x01, 0x43, 0x04, 0x03, 0x07, 0x00, 0x00, 0x0B, 0x03, 0x00, 0x00, 0x00, 0x10, 0x0F, 0xCC];

    public static readonly byte[] PacketAutoSwitchRequestComputer =
        [0xFC, 0x05, 0x00, 0x01, 0x71, 0x01, 0x79, 0x1C, 0xCC];

    // --- Winsock AF_BTH RFCOMM ---
    public const int AF_BTH = 32;
    public const int SOCK_STREAM = 1;
    public const int BTHPROTO_RFCOMM = 3;
    public const int SOL_SOCKET = 0xFFFF;
    public const int SO_RCVTIMEO = 0x1006;
    public const int SO_SNDTIMEO = 0x1005;
    public const int SO_ERROR = 0x1007;
    public const uint FIONBIO = 0x8004667E;
    public const int WSAEWOULDBLOCK = 10035;
    public const int WSAETIMEDOUT = 10060;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SOCKADDR_BTH
    {
        public ushort addressFamily;
        public ulong btAddr;
        public Guid serviceClassId;
        public uint port;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WSADATA
    {
        public ushort wVersion;
        public ushort wHighVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 257)]
        public byte[] szDescription;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 129)]
        public byte[] szSystemStatus;
        public ushort iMaxSockets;
        public ushort iMaxUdpDg;
        public IntPtr lpVendorInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TIMEVAL
    {
        public int tv_sec;
        public int tv_usec;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FD_SET
    {
        public uint fd_count;
        public IntPtr fd_0;
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int WSAStartup(ushort wVersionRequested, out WSADATA lpWSAData);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern IntPtr socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int connect(IntPtr s, ref SOCKADDR_BTH name, int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int send(IntPtr s, byte* buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int recv(IntPtr s, byte* buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int closesocket(IntPtr s);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int shutdown(IntPtr s, int how);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int setsockopt(IntPtr s, int level, int optname, ref int optval, int optlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int getsockopt(IntPtr s, int level, int optname, ref int optval, ref int optlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int ioctlsocket(IntPtr s, uint cmd, ref uint argp);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int select(int nfds, IntPtr readfds, ref FD_SET writefds, ref FD_SET exceptfds, ref TIMEVAL timeout);

    [DllImport("ws2_32.dll")]
    public static extern int WSAGetLastError();

    public static bool ConnectWithTimeout(IntPtr s, ref SOCKADDR_BTH sa, int timeoutMs)
    {
        uint nonBlock = 1;
        ioctlsocket(s, FIONBIO, ref nonBlock);

        int rc = connect(s, ref sa, Marshal.SizeOf<SOCKADDR_BTH>());
        if (rc == 0)
        {
            uint block = 0;
            ioctlsocket(s, FIONBIO, ref block);
            return true;
        }

        int err = WSAGetLastError();
        if (err != WSAEWOULDBLOCK)
        {
            return false;
        }

        FD_SET writeFds = new() { fd_count = 1, fd_0 = s };
        FD_SET exceptFds = new() { fd_count = 1, fd_0 = s };
        TIMEVAL tv = new()
        {
            tv_sec = timeoutMs / 1000,
            tv_usec = (timeoutMs % 1000) * 1000
        };

        int sel = select(0, IntPtr.Zero, ref writeFds, ref exceptFds, ref tv);
        if (sel > 0 && writeFds.fd_count > 0)
        {
            int soError = 0;
            int len = sizeof(int);
            getsockopt(s, SOL_SOCKET, SO_ERROR, ref soError, ref len);
            if (soError == 0)
            {
                uint block = 0;
                ioctlsocket(s, FIONBIO, ref block);
                return true;
            }
        }

        return false;
    }

    // --- Bluetooth Win32 APIs (bthprops.cpl / kernel32.dll) ---
    public const uint BLUETOOTH_SERVICE_ENABLE = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
        public ushort stLastSeen_wYear, stLastSeen_wMonth, stLastSeen_wDayOfWeek, stLastSeen_wDay, stLastSeen_wHour, stLastSeen_wMinute, stLastSeen_wSecond, stLastSeen_wMilliseconds;
        public ushort stLastUsed_wYear, stLastUsed_wMonth, stLastUsed_wDayOfWeek, stLastUsed_wDay, stLastUsed_wHour, stLastUsed_wMinute, stLastUsed_wSecond, stLastUsed_wMilliseconds;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    public static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService, uint dwServiceFlags);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    // --- Samsung DllBudsMsgFmt.dll Parser & Fallback ---
    private static delegate* unmanaged[Cdecl]<byte*, uint, int> _getMultipointState;
    private static delegate* unmanaged[Cdecl]<byte*, uint, int> _getStreamingType;
    private static delegate* unmanaged[Cdecl]<byte*, uint, int> _getStreamingReason;
    private static bool _msgFmtLoaded;
    private static readonly object _dllLock = new();

    public static void EnsureMsgFmtLoaded()
    {
        if (_msgFmtLoaded) return;
        lock (_dllLock)
        {
            if (_msgFmtLoaded) return;
            _msgFmtLoaded = true;

            try
            {
                string localDir = AppContext.BaseDirectory;
                string dynPath = Path.Combine(localDir, "BudsMsgFmtDynamic.dll");
                string dllPath = Path.Combine(localDir, "DllBudsMsgFmt.dll");

                if (!File.Exists(dynPath) || !File.Exists(dllPath))
                {
                    string samsungDir = @"C:\Program Files\WindowsApps\SAMSUNGELECTRONICSCoLtd.SamsungContinuityService_4.6.1327.0_x64__wyx1vj98g3asy\GalaxyConnectedExperience";
                    if (!File.Exists(dynPath) && File.Exists(Path.Combine(samsungDir, "BudsMsgFmtDynamic.dll")))
                        try { File.Copy(Path.Combine(samsungDir, "BudsMsgFmtDynamic.dll"), dynPath, true); } catch { }
                    if (!File.Exists(dllPath) && File.Exists(Path.Combine(samsungDir, "DllBudsMsgFmt.dll")))
                        try { File.Copy(Path.Combine(samsungDir, "DllBudsMsgFmt.dll"), dllPath, true); } catch { }
                }

                if (File.Exists(dynPath) && File.Exists(dllPath))
                {
                    NativeLibrary.Load(dynPath);
                    IntPtr hDll = NativeLibrary.Load(dllPath);
                    _getMultipointState = (delegate* unmanaged[Cdecl]<byte*, uint, int>)NativeLibrary.GetExport(hDll, "GetMultipointState");
                    _getStreamingType = (delegate* unmanaged[Cdecl]<byte*, uint, int>)NativeLibrary.GetExport(hDll, "GetStreamingType");
                    _getStreamingReason = (delegate* unmanaged[Cdecl]<byte*, uint, int>)NativeLibrary.GetExport(hDll, "GetStreamingReason");
                    Log.Information("AutoSwitchNative: Samsung DllBudsMsgFmt.dll loaded successfully");
                }
                else
                {
                    Log.Warning("AutoSwitchNative: DllBudsMsgFmt.dll not found in {Dir}, using built-in C# frame parser", localDir);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AutoSwitchNative: Could not load DllBudsMsgFmt.dll, using built-in C# frame parser");
            }
        }
    }

    public static (AutoSwitchMultipointState state, AutoSwitchStreamType streamType, int reason) ParseBudsSppPacket(byte[] data, int length)
    {
        EnsureMsgFmtLoaded();
        if (_getMultipointState != null && _getStreamingType != null && _getStreamingReason != null)
        {
            fixed (byte* p = data)
            {
                var mp = (AutoSwitchMultipointState)_getMultipointState(p, (uint)length);
                var st = (AutoSwitchStreamType)_getStreamingType(p, (uint)length);
                int rs = _getStreamingReason(p, (uint)length);
                return (mp, st, rs);
            }
        }

        // Fallback pure C# parser for standard MDE status notification frames:
        // Format: [0xFC, Len, Flags, MsgId(0x01), Opcode(0x4C/0x71), ...]
        if (length >= 6 && data[0] == 0xFC && data[length - 1] == 0xCC)
        {
            byte msgId = data[3];
            if (msgId == 0x01) // UNK_SPP_ALT / SMEP
            {
                byte op = data[4];
                if (op == 0x4C) // NOTIFY_PROPERTY
                {
                    // Search for multipoint state indicator in payload
                    for (int i = 5; i < length - 3; i++)
                    {
                        if (data[i] == 0x0B) // MDE opcode
                        {
                            if (i + 1 < length - 2)
                            {
                                byte val = data[i + 1];
                                if (val == 0) return (AutoSwitchMultipointState.Foreground, AutoSwitchStreamType.None, 0);
                                if (val == 1) return (AutoSwitchMultipointState.Background, AutoSwitchStreamType.None, 0);
                            }
                        }
                    }
                }
            }
        }

        return (AutoSwitchMultipointState.Invalid, AutoSwitchStreamType.None, 0);
    }

    // --- CoreAudio COM Interop ---
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    public static readonly Guid IID_IKsControl = new("28F54685-06FD-11D2-B27A-00A0C9223196");
    public static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    public static readonly Guid CLSID_PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    public const uint DEVICE_STATE_ACTIVE = 0x00000001;
    public const uint DEVICE_STATE_UNPLUGGED = 0x00000008;
    public const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
    public const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;
    public const uint KSPROPERTY_TYPE_GET = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct KSPROPERTY
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public int p2;
    }

    public static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        int GetCount(out uint pcDevices);
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out uint pdwState);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDeviceTopology
    {
        int GetConnectorCount(out uint pCount);
        int GetConnector(uint nIndex, out IConnector ppConnector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IConnector
    {
        int GetType(out int pType);
        int GetDataFlow(out int pFlow);
        int ConnectTo(IConnector pConnectTo);
        int Disconnect();
        int IsConnected(out int pbConnected);
        int GetConnectedTo(out IConnector ppConTo);
        int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrConnectorId);
        int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrDeviceId);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IKsControl
    {
        int KsProperty(ref KSPROPERTY Property, uint PropertyLength, IntPtr PropertyData, uint DataLength, out uint BytesReturned);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPolicyConfig
    {
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }
}
