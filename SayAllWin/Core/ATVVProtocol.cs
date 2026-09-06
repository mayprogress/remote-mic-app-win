namespace SayAll.Core;

/// <summary>
/// ATVV 语音通道协议（自 Sources/RemoteMic/ATVVProtocol.swift 移植）。
/// 服务/特征 UUID、命令字节、能力解析、IMA/DVI ADPCM 解码与 PCM 后处理全部保持原逻辑。
/// </summary>
public static class ATVVProtocol
{
    public const string ServiceUUID = "AB5E0001-5A21-4F05-BC7D-AF01F617B664";
    public const string TransmitUUID = "AB5E0002-5A21-4F05-BC7D-AF01F617B664";
    public const string AudioUUID = "AB5E0003-5A21-4F05-BC7D-AF01F617B664";
    public const string ControlUUID = "AB5E0004-5A21-4F05-BC7D-AF01F617B664";

    public static readonly byte[] GetCapabilitiesV10 = [0x0A, 0x01, 0x00, 0x00, 0x03, 0x03];

    public static bool SupportsAudio(double sampleRate) => sampleRate == 16000;

    public static byte[] MicrophoneOpen(ushort version, byte codec) =>
        version >= 0x0100 ? [0x0C, 0x00] : [0x0C, 0x00, codec];

    public static byte[] MicrophoneClose(ushort version, byte sessionID) =>
        version >= 0x0100 ? [0x0D, sessionID] : [0x0D];

    public static byte[]? MicrophoneExtend(ushort version, byte sessionID) =>
        version >= 0x0100 ? [0x0E, sessionID] : null;
}

public sealed class ATVVCapabilities
{
    public ushort Version { get; }
    public byte Codecs { get; }
    public byte Interaction { get; }
    public int FrameSize { get; }
    public byte SelectedCodec { get; }
    public double SampleRate { get; }

    public ATVVCapabilities(ushort version, byte codecs, byte interaction, int frameSize, byte selectedCodec, double sampleRate)
    {
        Version = version;
        Codecs = codecs;
        Interaction = interaction;
        FrameSize = frameSize;
        SelectedCodec = selectedCodec;
        SampleRate = sampleRate;
    }

    /// <summary>自 0x0B 开头的 Control 通知解析能力。与 Swift ATVVCapabilities.parse 逻辑一致。</summary>
    public static ATVVCapabilities? Parse(byte[] bytes)
    {
        if (bytes.Length < 7 || bytes[0] != 0x0B) return null;

        var version = (ushort)((bytes[1] << 8) | bytes[2]);
        byte codecs;
        byte interaction;

        if (version >= 0x0100)
        {
            codecs = bytes[3];
            interaction = bytes[4];
            if (codecs == 0 && bytes.Length >= 9 && (bytes[4] & 0x03) != 0)
            {
                codecs = bytes[4];
                interaction = 0x03;
            }
        }
        else
        {
            if (bytes.Length < 9) return null;
            codecs = bytes[4];
            interaction = 0;
        }

        var frameSize = (bytes[5] << 8) | bytes[6];
        var selectedCodec = (codecs & 0x02) != 0 ? (byte)0x02 : (byte)0x01;
        return new ATVVCapabilities(
            version,
            codecs,
            interaction,
            frameSize == 0 ? 120 : frameSize,
            selectedCodec,
            selectedCodec == 0x02 ? 16000 : 8000);
    }
}

/// <summary>IMA/DVI ADPCM 解码器（高半字节优先为默认；ARN9 固件低半字节优先）。</summary>
public sealed class IMAADPCMDecoder
{
    private static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
        34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130,
        143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449,
        494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411,
        1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026,
        4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
        11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623,
        27086, 29794, 32767,
    ];

    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8];

    public int Predictor { get; private set; }
    public int StepIndex { get; private set; }

    /// <summary>ARN9 固件编码为低半字节优先；默认高半字节优先。</summary>
    public bool LowNibbleFirst { get; set; }

    public void Reset(int predictor = 0, int stepIndex = 0)
    {
        Predictor = Math.Clamp(predictor, -32768, 32767);
        StepIndex = Math.Clamp(stepIndex, 0, 88);
    }

    public short[] Decode(byte[] data)
    {
        var samples = new short[data.Length * 2];
        var index = 0;
        foreach (var b in data)
        {
            if (LowNibbleFirst)
            {
                samples[index++] = DecodeNibble(b & 0x0F);
                samples[index++] = DecodeNibble(b >> 4);
            }
            else
            {
                samples[index++] = DecodeNibble(b >> 4);
                samples[index++] = DecodeNibble(b & 0x0F);
            }
        }
        return samples;
    }

    private short DecodeNibble(int nibble)
    {
        var step = StepTable[StepIndex];
        var difference = step >> 3;
        if ((nibble & 1) != 0) difference += step >> 2;
        if ((nibble & 2) != 0) difference += step >> 1;
        if ((nibble & 4) != 0) difference += step;

        Predictor += (nibble & 8) != 0 ? -difference : difference;
        Predictor = Math.Clamp(Predictor, -32768, 32767);
        StepIndex = Math.Clamp(StepIndex + IndexTable[nibble & 7], 0, 88);
        return (short)Predictor;
    }
}

/// <summary>PCM 后处理：三点平滑（1,2,1)>>2）与 -24…24 dB 安全限幅增益。</summary>
public static class PCMPostprocessor
{
    public static short[] Process(short[] input, double gainDB)
    {
        if (input.Length == 0) return [];

        Span<int> filtered = stackalloc int[input.Length];
        for (var i = 0; i < input.Length; i++) filtered[i] = input[i];

        if (input.Length >= 3)
        {
            for (var i = 1; i < input.Length - 1; i++)
            {
                filtered[i] = (input[i - 1] + 2 * input[i] + input[i + 1]) >> 2;
            }
        }

        var finiteGain = double.IsFinite(gainDB) ? gainDB : 0;
        var safeGain = Math.Clamp(finiteGain, -24.0, 24.0);
        var gain = Math.Pow(10.0, safeGain / 20.0);

        var result = new short[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var scaled = (int)Math.Round(filtered[i] * gain);
            result[i] = (short)Math.Clamp(scaled, -32768, 32767);
        }
        return result;
    }
}

/// <summary>按遥控器声明的帧长累积音频通知，产出完整帧。</summary>
public sealed class FrameAccumulator
{
    public byte[] Pending { get; private set; } = [];

    public List<byte[]> Append(byte[] data, int frameSize)
    {
        var frames = new List<byte[]>();
        if (frameSize <= 0) return frames;

        var merged = new byte[Pending.Length + data.Length];
        Buffer.BlockCopy(Pending, 0, merged, 0, Pending.Length);
        Buffer.BlockCopy(data, 0, merged, Pending.Length, data.Length);

        var offset = 0;
        var available = merged.Length;
        while (available >= frameSize)
        {
            var frame = new byte[frameSize];
            Buffer.BlockCopy(merged, offset, frame, 0, frameSize);
            frames.Add(frame);
            offset += frameSize;
            available -= frameSize;
        }
        Pending = new byte[available];
        if (available > 0) Buffer.BlockCopy(merged, offset, Pending, 0, available);
        return frames;
    }

    public void Reset() => Pending = [];
}

/// <summary>电池电量状态解码（自 RemoteDeviceProfile.decodeBatteryLevelStatus 移植）。</summary>
public enum RemotePowerState
{
    OnBattery,
    ExternalPower,
    Charging,
    Unknown,
}

public static class RemotePowerStateParser
{
    public static RemotePowerState Decode(byte[] data)
    {
        if (data.Length < 3) return RemotePowerState.Unknown;
        var powerState = (ushort)(data[1] | (data[2] << 8));
        var batteryPresent = (powerState & 0x0001) == 0x0001;
        if (!batteryPresent) return RemotePowerState.Unknown;

        var wiredExternalPower = (powerState >> 1) & 0x0003;
        var wirelessExternalPower = (powerState >> 3) & 0x0003;
        var chargeState = (powerState >> 5) & 0x0003;

        if (chargeState == 0x0001) return RemotePowerState.Charging;
        if (wiredExternalPower == 0x0001 || wirelessExternalPower == 0x0001) return RemotePowerState.ExternalPower;
        if (wiredExternalPower == 0 && wirelessExternalPower == 0) return RemotePowerState.OnBattery;
        return RemotePowerState.Unknown;
    }
}

public enum XiaomiRemoteModel
{
    Rc001,
    Rc003,
    Unknown,
}

public static class XiaomiRemoteModelHelper
{
    /// <summary>"RC001" / "RC003" / 包含 "ARN9" 的型号字符串识别。</summary>
    public static XiaomiRemoteModel? Identified(string? modelNumber)
    {
        if (string.IsNullOrWhiteSpace(modelNumber)) return null;
        var normalized = modelNumber.Trim().ToUpperInvariant();
        return normalized switch
        {
            "RC001" => XiaomiRemoteModel.Rc001,
            "RC003" => XiaomiRemoteModel.Rc003,
            _ when normalized.Contains("ARN9") => XiaomiRemoteModel.Rc003,
            _ => null,
        };
    }
}

/// <summary>遥控器名称匹配（自 XiaomiVoiceRemoteNameMatcher 移植，含 ARN9 广告名）。</summary>
public static class XiaomiVoiceRemoteNameMatcher
{
    private static readonly HashSet<string> ApprovedNames =
    [
        "mi rc",
        "xiaomi bluetooth remote 2",
        "xiaomi bluetooth remote 2 pro",
        "小米蓝牙语音遥控器",
        "小米蓝牙遥控器2",
        "小米蓝牙遥控器2 pro",
        "arn9",
    ];

    public static bool Matches(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return false;
        var normalized = rawName.Trim().ToLowerInvariant();
        return ApprovedNames.Contains(normalized);
    }
}

/// <summary>蓝牙重连策略：3s 起、指数退避 ×2 封顶 60s、±10% 抖动（自 BluetoothReconnectPolicy 移植）。</summary>
public sealed class BluetoothReconnectPolicy
{
    private const double BaseDelay = 3.0;
    private const double MaximumDelay = 60.0;
    private const double JitterRatio = 0.1;

    public int ConsecutiveFailureCount { get; private set; }
    public bool AllowsCachedTargetRetrieval { get; private set; } = true;

    public double NextAutomaticDelay(bool bypassCachedTarget, double jitterUnit)
    {
        ConsecutiveFailureCount += 1;
        if (bypassCachedTarget) AllowsCachedTargetRetrieval = false;

        var exponent = Math.Min(ConsecutiveFailureCount - 1, 5);
        var nominal = Math.Min(MaximumDelay, BaseDelay * Math.Pow(2, exponent));
        var normalizedJitter = Math.Clamp(jitterUnit, 0, 1);
        var jitterFactor = (1 - JitterRatio) + (normalizedJitter * JitterRatio * 2);
        return Math.Min(MaximumDelay, nominal * jitterFactor);
    }

    public void Reset()
    {
        ConsecutiveFailureCount = 0;
        AllowsCachedTargetRetrieval = true;
    }
}