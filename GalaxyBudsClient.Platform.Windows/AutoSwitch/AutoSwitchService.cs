using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GalaxyBudsClient.Platform.Interfaces;
using Serilog;
using static GalaxyBudsClient.Platform.Windows.AutoSwitch.AutoSwitchNative;

namespace GalaxyBudsClient.Platform.Windows.AutoSwitch;

public sealed class AutoSwitchService : IAutoSwitchService
{
    private static readonly ILogger Logger = Log.ForContext("SourceContext", "AutoSwitch");

    public bool IsSupported => true;
    public bool IsRunning => _loopCts != null;
    public bool IsMdeConnected => _mdeSocket != IntPtr.Zero && _mdeSocket != (IntPtr)(-1);
    public bool AutoSwitchEnabled { get; set; } = true;
    public bool ReleaseA2dpOnPhoneTakeover { get; set; }
    public float AudioPeakThreshold { get; set; } = 0.015f;
    public AutoSwitchMultipointState CurrentMdeState { get; private set; } = AutoSwitchMultipointState.Null;
    public AutoSwitchStreamType CurrentStreamType { get; private set; } = AutoSwitchStreamType.None;
    public float CurrentAudioPeak { get; private set; }
    public string? FallbackAudioEndpointName => _fallbackAudioEndpointName;

    private readonly List<string> _recentLogs = [];
    private readonly object _logLock = new();
    public IReadOnlyList<string> RecentLogs
    {
        get
        {
            lock (_logLock)
            {
                return _recentLogs.ToList();
            }
        }
    }

    public event EventHandler<string>? LogMessageAdded;
    public event EventHandler? StateChanged;

    private ulong _budsMac;
    private string? _budsName;
    private IntPtr _mdeSocket = (IntPtr)(-1);
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private CancellationTokenSource? _loopCts;

    private DateTime _lastPhoneTakeoverUtc = DateTime.MinValue;
    private DateTime _lastAutoSwitchAttemptUtc = DateTime.MinValue;
    private DateTime _lastPcConnectCompletedUtc = DateTime.MinValue;
    private volatile bool _isConnectingToPc;

    private string? _fallbackAudioEndpointId;
    private string? _fallbackAudioEndpointName;
    private string? _cachedMeterDeviceId;
    private IAudioMeterInformation? _cachedMeter;

    private bool _disposed;

    public AutoSwitchService()
    {
        WSAStartup(0x0202, out _);
        EnsureMsgFmtLoaded();
    }

    private void AddLog(string message)
    {
        string timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Logger.Information("{Message}", message);

        lock (_logLock)
        {
            _recentLogs.Add(timestamped);
            if (_recentLogs.Count > 250)
            {
                _recentLogs.RemoveAt(0);
            }
        }

        try
        {
            LogMessageAdded?.Invoke(this, timestamped);
        }
        catch { }
    }

    public Task StartAsync(ulong btMac, string? deviceName)
    {
        if (_disposed) return Task.CompletedTask;

        _budsMac = btMac;
        _budsName = deviceName ?? "Galaxy Buds";

        AddLog($"Starting AutoSwitch service for {_budsName} (MAC: 0x{_budsMac:X12})");

        StopBackgroundLoopsInternal();

        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;

        Task.Run(() => MdeSppMonitorLoopAsync(token), token);
        Task.Run(() => CoreAudioMonitorLoopAsync(token), token);

        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        AddLog("Stopping AutoSwitch service");
        StopBackgroundLoopsInternal();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void StopBackgroundLoopsInternal()
    {
        if (_loopCts != null)
        {
            try
            {
                _loopCts.Cancel();
                _loopCts.Dispose();
            }
            catch { }
            _loopCts = null;
        }

        CloseMdeSppSocket();
        ReleaseCachedMeter();
    }

    private void CloseMdeSppSocket()
    {
        IntPtr s = Interlocked.Exchange(ref _mdeSocket, (IntPtr)(-1));
        if (s != IntPtr.Zero && s != (IntPtr)(-1))
        {
            try
            {
                shutdown(s, 2); // SD_BOTH
                closesocket(s);
            }
            catch { }

            CurrentMdeState = AutoSwitchMultipointState.Null;
            CurrentStreamType = AutoSwitchStreamType.None;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private unsafe bool SendMdePacket(byte[] packet)
    {
        IntPtr s = _mdeSocket;
        if (s == IntPtr.Zero || s == (IntPtr)(-1)) return false;

        fixed (byte* p = packet)
        {
            int sent = send(s, p, packet.Length, 0);
            return sent == packet.Length;
        }
    }

    private unsafe int ReceiveMdePacket(byte[] buf)
    {
        IntPtr s = _mdeSocket;
        if (s == IntPtr.Zero || s == (IntPtr)(-1)) return -1;

        fixed (byte* p = buf)
        {
            return recv(s, p, buf.Length, 0);
        }
    }

    public bool TryConnectMdeSpp(int timeoutMs = 2500)
    {
        CloseMdeSppSocket();

        if (_budsMac == 0) return false;

        IntPtr s = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
        if (s == (IntPtr)(-1))
        {
            AddLog("Failed to create Winsock RFCOMM socket");
            return false;
        }

        int rcvTimeout = 1000;
        setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, ref rcvTimeout, sizeof(int));

        var sa = new SOCKADDR_BTH
        {
            addressFamily = AF_BTH,
            btAddr = _budsMac,
            serviceClassId = MdeServiceUuid,
            port = 0
        };

        bool ok = ConnectWithTimeout(s, ref sa, timeoutMs);
        if (!ok)
        {
            closesocket(s);
            return false;
        }

        _mdeSocket = s;
        AddLog($"Connected MDE RFCOMM socket to 0x{_budsMac:X12}");

        // Send Auto-Switch V3 handshake
        SendMdePacket(PacketSettingInfoBt);
        SendMdePacket(PacketMdeVersionAutoSwitchV3);
        AddLog("Sent MDE AutoSwitch V3 handshake (SettingInfoBt + MdeVersion V3)");

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static List<byte[]> SplitMdeFrames(byte[] buf, int totalRead)
    {
        var frames = new List<byte[]>();
        int offset = 0;
        while (offset < totalRead)
        {
            if (buf[offset] == 0xFC && offset + 4 <= totalRead)
            {
                int frameLen = buf[offset + 1] + 4;
                if (frameLen >= 4 && offset + frameLen <= totalRead && buf[offset + frameLen - 1] == 0xCC)
                {
                    byte[] frame = new byte[frameLen];
                    Buffer.BlockCopy(buf, offset, frame, 0, frameLen);
                    frames.Add(frame);
                    offset += frameLen;
                    continue;
                }
            }
            byte[] rem = new byte[totalRead - offset];
            Buffer.BlockCopy(buf, offset, rem, 0, rem.Length);
            frames.Add(rem);
            break;
        }
        return frames;
    }

    private async Task MdeSppMonitorLoopAsync(CancellationToken token)
    {
        byte[] buf = new byte[512];
        AddLog("MDE SPP monitor loop started");

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IsMdeConnected)
                {
                    // Delay after phone takeover before reconnecting MDE channel
                    if ((DateTime.UtcNow - _lastPhoneTakeoverUtc).TotalSeconds < 3.5)
                    {
                        await Task.Delay(1000, token);
                        continue;
                    }

                    if (AutoSwitchEnabled)
                    {
                        TryConnectMdeSpp(timeoutMs: 2000);
                    }

                    if (!IsMdeConnected)
                    {
                        await Task.Delay(3500, token);
                        continue;
                    }
                }

                int read = ReceiveMdePacket(buf);

                if (read > 0)
                {
                    foreach (byte[] frame in SplitMdeFrames(buf, read))
                    {
                        var (mp, st, rs) = ParseBudsSppPacket(frame, frame.Length);
                        string hex = BitConverter.ToString(frame, 0, frame.Length);

                        if (mp != AutoSwitchMultipointState.Invalid)
                        {
                            var prevState = CurrentMdeState;
                            CurrentMdeState = mp;
                            CurrentStreamType = st;
                            AddLog($"MDE Status: State={mp}, Stream={st}, Reason={rs} [{hex}]");

                            if (mp == AutoSwitchMultipointState.Foreground)
                            {
                                if (AutoSwitchEnabled)
                                {
                                    var status = QueryCoreAudioEndpoints();
                                    if (status.budsActive &&
                                        !string.IsNullOrEmpty(status.budsId) &&
                                        status.defaultName?.Contains("Buds", StringComparison.OrdinalIgnoreCase) != true)
                                    {
                                        AddLog($"OnForeground: Setting '{status.budsName}' as Windows Default Audio Output");
                                        SetDefaultAudioOutput(status.budsId);
                                    }
                                }
                            }
                            else if (mp == AutoSwitchMultipointState.Background)
                            {
                                bool notConnectingToPc = !_isConnectingToPc && (DateTime.UtcNow - _lastPcConnectCompletedUtc).TotalSeconds > 2.0;
                                if (notConnectingToPc &&
                                    (prevState == AutoSwitchMultipointState.Foreground ||
                                     st != AutoSwitchStreamType.None ||
                                     rs == 132))
                                {
                                    HandlePhoneTakeoverOnBackground(st, rs, prevState);
                                }
                            }

                            StateChanged?.Invoke(this, EventArgs.Empty);
                        }
                        else
                        {
                            AddLog($"MDE Raw Frame ({frame.Length}B): {hex}");
                        }
                    }
                }
                else if (read == 0)
                {
                    AddLog("MDE RFCOMM channel closed by remote device");
                    CloseMdeSppSocket();
                    await Task.Delay(2000, token);
                }
                else
                {
                    int err = WSAGetLastError();
                    if (err != WSAETIMEDOUT)
                    {
                        AddLog($"MDE RFCOMM disconnected (WSAError={err})");
                        CloseMdeSppSocket();
                        await Task.Delay(2500, token);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AddLog($"MDE Monitor exception: {ex.Message}");
                await Task.Delay(2500, token);
            }
        }

        AddLog("MDE SPP monitor loop exited");
    }

    private void HandlePhoneTakeoverOnBackground(AutoSwitchStreamType streamType, int reason, AutoSwitchMultipointState prevState)
    {
        _lastPhoneTakeoverUtc = DateTime.UtcNow;
        var status = QueryCoreAudioEndpoints();
        bool isBudsDefault = status.defaultName?.Contains("Buds", StringComparison.OrdinalIgnoreCase) == true;

        if (isBudsDefault && !string.IsNullOrEmpty(_fallbackAudioEndpointId))
        {
            AddLog($"Phone Takeover (Stream={streamType}, Reason={reason}): Switching Windows default output to '{_fallbackAudioEndpointName}'");

            SetEndpointMute(_fallbackAudioEndpointId, true);
            SetDefaultAudioOutput(_fallbackAudioEndpointId);

            string fallbackCopy = _fallbackAudioEndpointId;
            _ = Task.Run(async () =>
            {
                await Task.Delay(700);
                SetEndpointMute(fallbackCopy, false);
            });
        }
        else
        {
            AddLog($"Phone Takeover acknowledged (Cooldown armed)");
        }

        if (ReleaseA2dpOnPhoneTakeover && status.budsActive && !string.IsNullOrEmpty(status.budsId))
        {
            AddLog($"ReleaseA2dpOnPhoneTakeover active: firing KSPROPERTY_ONESHOT_DISCONNECT to '{status.budsName}'");
            TriggerKsBtAudioProperty(status.budsId, KSPROPERTY_ONESHOT_DISCONNECT);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task CoreAudioMonitorLoopAsync(CancellationToken token)
    {
        int activeTicks = 0;
        AddLog("CoreAudio peak monitor loop started");

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(150, token);

                if (!AutoSwitchEnabled)
                {
                    activeTicks = 0;
                    CurrentAudioPeak = 0f;
                    continue;
                }

                float peak = SampleNonBudsAudioPeak();
                CurrentAudioPeak = peak;

                float threshold = AudioPeakThreshold;
                if (threshold <= 0f) threshold = 0.015f;

                if (peak >= threshold)
                {
                    activeTicks++;
                }
                else
                {
                    activeTicks = 0;
                }

                if (activeTicks >= 3)
                {
                    activeTicks = 0;

                    if ((DateTime.UtcNow - _lastPhoneTakeoverUtc).TotalSeconds < 5.0 ||
                        (DateTime.UtcNow - _lastAutoSwitchAttemptUtc).TotalSeconds < 6.0)
                    {
                        continue;
                    }

                    if (CurrentStreamType.HasFlag(AutoSwitchStreamType.HFP) || CurrentStreamType.HasFlag(AutoSwitchStreamType.CIS_Call))
                    {
                        AddLog("PC audio detected, but phone is in active call (HFP/CIS_Call) — skipping switch");
                        _lastAutoSwitchAttemptUtc = DateTime.UtcNow;
                        continue;
                    }

                    var status = QueryCoreAudioEndpoints();
                    bool isBudsDefault = status.defaultName?.Contains("Buds", StringComparison.OrdinalIgnoreCase) == true;
                    if (status.budsActive && isBudsDefault && CurrentMdeState == AutoSwitchMultipointState.Foreground)
                    {
                        continue;
                    }

                    if (IsMdeConnected || status.budsActive)
                    {
                        _lastAutoSwitchAttemptUtc = DateTime.UtcNow;
                        _ = PullToPcAsync($"AutoSwitch: audio peak={peak:F3}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AddLog($"CoreAudio monitor note: {ex.Message}");
            }
        }

        AddLog("CoreAudio peak monitor loop exited");
    }

    private float SampleNonBudsAudioPeak()
    {
        try
        {
            var status = QueryCoreAudioEndpoints();
            if (status.defaultName?.Contains("Buds", StringComparison.OrdinalIgnoreCase) == true)
            {
                return 0f;
            }

            if (_cachedMeter == null || _cachedMeterDeviceId != _fallbackAudioEndpointId)
            {
                ReleaseCachedMeter();

                if (string.IsNullOrEmpty(_fallbackAudioEndpointId)) return 0f;

                var enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                if (enumType == null) return 0f;
                var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType)!;

                if (enumerator.GetDevice(_fallbackAudioEndpointId, out IMMDevice dev) == 0 && dev != null)
                {
                    Guid iidMeter = IID_IAudioMeterInformation;
                    if (dev.Activate(ref iidMeter, 1, IntPtr.Zero, out object meterObj) == 0 && meterObj is IAudioMeterInformation meter)
                    {
                        _cachedMeter = meter;
                        _cachedMeterDeviceId = _fallbackAudioEndpointId;
                    }
                }
            }

            if (_cachedMeter != null && _cachedMeter.GetPeakValue(out float peak) == 0)
            {
                return peak;
            }
        }
        catch
        {
            ReleaseCachedMeter();
        }

        return 0f;
    }

    private void ReleaseCachedMeter()
    {
        _cachedMeter = null;
        _cachedMeterDeviceId = null;
    }

    public async Task<bool> PullToPcAsync(string reason = "Manual UI Request")
    {
        if (!await _opLock.WaitAsync(500))
        {
            AddLog("Operation in progress, skipping duplicate pull request");
            return false;
        }

        _isConnectingToPc = true;
        try
        {
            AddLog($"Pulling Buds to PC ({reason})");
            var status = QueryCoreAudioEndpoints();

            // FAST MULTIPOINT PATH
            if (status.budsActive && !string.IsNullOrEmpty(status.budsId))
            {
                await Task.Run(() =>
                {
                    if (!IsMdeConnected)
                    {
                        TryConnectMdeSpp(timeoutMs: 2000);
                    }
                    if (IsMdeConnected)
                    {
                        AddLog("Fast Multipoint Switch: Sending PacketAutoSwitchRequestComputer (0x71)...");
                        SendMdePacket(PacketAutoSwitchRequestComputer);
                    }
                });

                for (int i = 0; i < 8; i++)
                {
                    await Task.Delay(150);
                    if (CurrentMdeState == AutoSwitchMultipointState.Foreground) break;
                }

                SetDefaultAudioOutput(status.budsId);
                if (!string.IsNullOrEmpty(_fallbackAudioEndpointId))
                {
                    SetEndpointMute(_fallbackAudioEndpointId, false);
                }

                _lastPcConnectCompletedUtc = DateTime.UtcNow;
                AddLog($"Fast Switch Success: '{status.budsName}' set as Windows Default Audio Output");
                StateChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            // COLD-START PATH
            await Task.Run(() =>
            {
                if (!IsMdeConnected)
                {
                    TryConnectMdeSpp(timeoutMs: 2000);
                }
                if (IsMdeConnected)
                {
                    SendMdePacket(PacketAutoSwitchRequestComputer);
                }
            });

            if (!status.budsActive && !string.IsNullOrEmpty(status.budsId))
            {
                AddLog($"Triggering KSPROPERTY_ONESHOT_RECONNECT on '{status.budsName}'...");
                TriggerKsBtAudioProperty(status.budsId, KSPROPERTY_ONESHOT_RECONNECT);
            }

            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(250);
                var st = QueryCoreAudioEndpoints();
                if (st.budsActive && !string.IsNullOrEmpty(st.budsId))
                {
                    EnsureMdeHandshake();
                    SetDefaultAudioOutput(st.budsId);
                    _lastPcConnectCompletedUtc = DateTime.UtcNow;
                    AddLog($"Success: '{st.budsName}' is now Active and set as default audio output");
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
            }

            // Fallback: Gateway socket + BluetoothSetServiceState
            AddLog("Triggering Gateway RFCOMM + BluetoothSetServiceState fallback...");
            await Task.Run(() =>
            {
                ConnectGatewayServiceBriefly();
                ToggleBluetoothAudioServices();
            });

            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(250);
                var st = QueryCoreAudioEndpoints();
                if (st.budsActive && !string.IsNullOrEmpty(st.budsId))
                {
                    EnsureMdeHandshake();
                    SetDefaultAudioOutput(st.budsId);
                    _lastPcConnectCompletedUtc = DateTime.UtcNow;
                    AddLog($"Success: '{st.budsName}' connected via Gateway fallback");
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
            }

            AddLog("Cold-start pull did not complete audio connection");
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        finally
        {
            _isConnectingToPc = false;
            _opLock.Release();
        }
    }

    public async Task<bool> HandbackToPhoneAsync(string reason = "Manual UI Request")
    {
        if (!await _opLock.WaitAsync(500)) return false;

        try
        {
            AddLog($"Handing audio back to Phone ({reason})");
            _lastPhoneTakeoverUtc = DateTime.UtcNow;

            var status = QueryCoreAudioEndpoints();
            if (status.defaultName?.Contains("Buds", StringComparison.OrdinalIgnoreCase) == true &&
                !string.IsNullOrEmpty(_fallbackAudioEndpointId))
            {
                AddLog($"Switching Windows default output back to '{_fallbackAudioEndpointName}'");
                SetDefaultAudioOutput(_fallbackAudioEndpointId);
            }

            if (!string.IsNullOrEmpty(status.budsId) && status.budsActive)
            {
                AddLog($"Sending KSPROPERTY_ONESHOT_DISCONNECT to '{status.budsName}'");
                TriggerKsBtAudioProperty(status.budsId, KSPROPERTY_ONESHOT_DISCONNECT);
            }

            await Task.Delay(300);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _opLock.Release();
        }
    }

    private void EnsureMdeHandshake()
    {
        if (!IsMdeConnected)
        {
            TryConnectMdeSpp(timeoutMs: 2000);
        }
        else
        {
            SendMdePacket(PacketSettingInfoBt);
            SendMdePacket(PacketMdeVersionAutoSwitchV3);
        }

        if (IsMdeConnected)
        {
            SendMdePacket(PacketAutoSwitchRequestComputer);
        }
    }

    private void ConnectGatewayServiceBriefly()
    {
        if (_budsMac == 0) return;
        IntPtr s = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
        if (s == (IntPtr)(-1)) return;

        try
        {
            var sa = new SOCKADDR_BTH
            {
                addressFamily = AF_BTH,
                btAddr = _budsMac,
                serviceClassId = GatewayServiceUuid,
                port = 0
            };
            if (ConnectWithTimeout(s, ref sa, 2000))
            {
                Thread.Sleep(300);
            }
        }
        finally
        {
            closesocket(s);
        }
    }

    private void ToggleBluetoothAudioServices()
    {
        if (_budsMac == 0) return;
        var rp = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr hFind = BluetoothFindFirstRadio(ref rp, out IntPtr hRadio);
        if (hFind == IntPtr.Zero) return;

        try
        {
            var bdi = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(), Address = _budsMac };
            if (BluetoothGetDeviceInfo(hRadio, ref bdi) == 0)
            {
                Guid audioSink = AudioSinkServiceUuid;
                Guid handsfree = HandsfreeServiceUuid;
                BluetoothSetServiceState(hRadio, ref bdi, ref audioSink, BLUETOOTH_SERVICE_ENABLE);
                BluetoothSetServiceState(hRadio, ref bdi, ref handsfree, BLUETOOTH_SERVICE_ENABLE);
            }
        }
        finally
        {
            CloseHandle(hRadio);
            BluetoothFindRadioClose(hFind);
        }
    }

    private (string? budsId, string? budsName, bool budsActive, string? defaultName) QueryCoreAudioEndpoints()
    {
        string? budsId = null;
        string? budsName = null;
        bool budsActive = false;
        string? defaultName = null;

        try
        {
            var enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            if (enumType == null) return (null, null, false, null);
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType)!;

            if (enumerator.GetDefaultAudioEndpoint(0, 0, out IMMDevice defDev) == 0 && defDev != null)
            {
                defaultName = GetDeviceFriendlyName(defDev);
                if (!defaultName.Contains("Buds", StringComparison.OrdinalIgnoreCase))
                {
                    defDev.GetId(out string defId);
                    if (!string.IsNullOrEmpty(defId))
                    {
                        _fallbackAudioEndpointId = defId;
                        _fallbackAudioEndpointName = defaultName;
                    }
                }
            }

            if (enumerator.EnumAudioEndpoints(0, DEVICE_STATE_ACTIVE | DEVICE_STATE_UNPLUGGED, out IMMDeviceCollection col) == 0 && col != null)
            {
                col.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (col.Item(i, out IMMDevice dev) == 0 && dev != null)
                    {
                        string name = GetDeviceFriendlyName(dev);
                        dev.GetState(out uint state);
                        bool isActive = (state & DEVICE_STATE_ACTIVE) != 0;

                        if (name.Contains("Buds", StringComparison.OrdinalIgnoreCase))
                        {
                            if (budsId == null || isActive)
                            {
                                dev.GetId(out budsId);
                                budsName = name;
                                budsActive = isActive;
                            }
                        }
                        else if (isActive && string.IsNullOrEmpty(_fallbackAudioEndpointId))
                        {
                            dev.GetId(out _fallbackAudioEndpointId);
                            _fallbackAudioEndpointName = name;
                        }
                    }
                }
            }
        }
        catch { }

        return (budsId, budsName, budsActive, defaultName);
    }

    private static string GetDeviceFriendlyName(IMMDevice dev)
    {
        try
        {
            if (dev.OpenPropertyStore(0, out IPropertyStore store) == 0 && store != null)
            {
                var key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out PROPVARIANT pv) == 0 && pv.vt == 31 && pv.p != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(pv.p) ?? "";
                }
            }
        }
        catch { }
        return "";
    }

    private bool TriggerKsBtAudioProperty(string endpointId, uint ksPropertyId)
    {
        try
        {
            var enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            if (enumType == null) return false;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType)!;

            if (enumerator.GetDevice(endpointId, out IMMDevice dev) != 0 || dev == null)
                return false;

            Guid iidTopo = IID_IDeviceTopology;
            if (dev.Activate(ref iidTopo, 1, IntPtr.Zero, out object topoObj) != 0 || topoObj is not IDeviceTopology topo)
                return false;

            if (topo.GetConnector(0, out IConnector conn) != 0 || conn == null)
                return false;

            if (conn.GetDeviceIdConnectedTo(out string filterId) != 0 || string.IsNullOrEmpty(filterId))
                return false;

            if (enumerator.GetDevice(filterId, out IMMDevice filterDev) != 0 || filterDev == null)
                return false;

            Guid iidKs = IID_IKsControl;
            if (filterDev.Activate(ref iidKs, 1, IntPtr.Zero, out object ksObj) != 0 || ksObj is not IKsControl ksControl)
                return false;

            var prop = new KSPROPERTY
            {
                Set = KSPROPSETID_BtAudio,
                Id = ksPropertyId,
                Flags = KSPROPERTY_TYPE_GET
            };

            int hr = ksControl.KsProperty(ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
            AddLog($"IKsControl.KsProperty(Id={ksPropertyId}) hr=0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex)
        {
            AddLog($"TriggerKsBtAudioProperty error: {ex.Message}");
            return false;
        }
    }

    private void SetDefaultAudioOutput(string endpointId)
    {
        try
        {
            var pcType = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
            if (pcType == null) return;
            var policyConfig = (IPolicyConfig)Activator.CreateInstance(pcType)!;
            policyConfig.SetDefaultEndpoint(endpointId, 0); // eConsole
            policyConfig.SetDefaultEndpoint(endpointId, 1); // eMultimedia
            policyConfig.SetDefaultEndpoint(endpointId, 2); // eCommunications
            AddLog($"SetDefaultEndpoint: set {endpointId} as default render device");
        }
        catch (Exception ex)
        {
            AddLog($"SetDefaultAudioOutput error: {ex.Message}");
        }
    }

    private void SetEndpointMute(string endpointId, bool mute)
    {
        try
        {
            var enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            if (enumType == null) return;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType)!;
            if (enumerator.GetDevice(endpointId, out IMMDevice dev) != 0 || dev == null) return;

            Guid iidVol = IID_IAudioEndpointVolume;
            if (dev.Activate(ref iidVol, 1, IntPtr.Zero, out object volObj) == 0 && volObj is IAudioEndpointVolume vol)
            {
                Guid ctx = Guid.Empty;
                vol.SetMute(mute, ref ctx);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().Wait();
        _opLock.Dispose();
    }
}
