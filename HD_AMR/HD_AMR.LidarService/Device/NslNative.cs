using System.Runtime.InteropServices;

namespace HD_AMR.LidarService.Device;

/// <summary>
/// nanolib(libnanolib.so, NSL SDK 1.13) P/Invoke 선언.
///
/// 라이브러리는 클로즈드 바이너리이고 헤더(<c>nanolib.h</c>)가 유일한 명세다. 헤더의
/// 선언은 <c>extern "C"</c> 로 감싸여 있어 심볼이 맹글링되지 않는다(aarch64 빌드에서
/// <c>nm -D</c> 로 <c>T nsl_open</c> 형태 확인 완료).
///
/// ⚠ 구조체 레이아웃은 헤더의 <c>#pragma pack(push, 1)</c> 을 그대로 옮긴 것이며,
///   실장비에서 sizeof/offsetof 로 <b>아직 검증되지 않았다</b>. 검증 전에는
///   <see cref="NslPcdLayout"/> 의 상수들을 신뢰하지 말 것 — 어긋나면 엉뚱한
///   메모리를 읽고도 그럴듯한 숫자가 나온다. <see cref="NslPcdLayout.Verify"/> 로
///   런타임에 대조한다.
/// </summary>
internal static partial class NslNative
{
    private const string Lib = "nanolib";

    // ── 헤더 상수 ──────────────────────────────────────────────────────────
    public const int TypeAWidth = 320;
    public const int TypeAHeight = 240;
    public const int TypeBWidth = 800;
    public const int TypeBHeight = 600;

    /// <summary>이 값 미만이어야 유효한 거리값이다. 이상은 전부 무효 픽셀 코드.</summary>
    public const int LimitForValidData = 64000;

    public const int LowAmplitude = 64001;
    public const int AdcOverflow = 64002;
    public const int Saturation = 64003;
    public const int BadPixel = 64004;
    public const int LowDcs = 64005;
    public const int Interference = 64007;
    public const int EdgeDetected = 64008;

    /// <summary><c>distance3D</c> 의 첫 번째 차원 인덱스.</summary>
    public const int OutX = 0;
    public const int OutY = 1;
    public const int OutZ = 2;
    public const int MaxOut = 3;

    // ── 열거형 (nanolib.h 의 enum class, 기저 타입 int) ────────────────────

    public enum FunctionOption { Off = 0, On = 1 }

    public enum HdrOption { None = 0, Spatial = 1, Temporal = 2 }

    public enum UdpSpeedOption { Net100Mbps = 0, Net1000Mbps = 1 }

    public enum DualBeamModOption { Off = 0, Mhz6 = 1, Mhz3 = 2 }

    public enum DualBeamOpsOption { Avoidance = 0, Correction = 1, FullCorrection = 2 }

    public enum ModulationOption { Mhz12 = 0, Mhz24 = 1, Mhz6 = 2, Mhz3 = 3, Mhz1p5 = 4 }

    public enum ModulationChOption { Ch0 = 0 }

    public enum FrameRateOption { Fps5 = 5, Fps10 = 10, Fps15 = 15, Fps20 = 20, Fps25 = 25, Fps30 = 30 }

    public enum LensType { NarrowField = 0, StandardField = 1, WideField = 2 }

    public enum LidarTypeOption { TypeA = 0, TypeB = 1 }

    public enum OperationMode
    {
        None = 0,
        Distance = 1,
        Grayscale = 2,
        DistanceAmplitude = 3,
        DistanceGrayscale = 4,
        Rgb = 5,
        RgbDistance = 6,
        RgbDistanceAmplitude = 7,
        RgbDistanceGrayscale = 8,
    }

    public enum NslError
    {
        Success = 0,
        InvalidHandle = -1,
        NotOpened = -2,
        NotReady = -3,
        IpDuplicated = -4,
        HandleOverflow = -5,
        DisconnectedSocket = -6,
        AnswerError = -7,
        InvalidParameter = -8,
    }

    // ── 구조체 ────────────────────────────────────────────────────────────

    /// <summary>BGR 순서. 헤더의 <c>NslVec3b</c>.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NslVec3b
    {
        public byte B;
        public byte G;
        public byte R;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Point3D
    {
        public double X;
        public double Y;
        public double Z;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NslBitInfo
    {
        public int Current;
        public double Voltage;
        public double Temperature;
    }

    /// <summary>
    /// 헤더의 <c>NslConfig</c>. 필드 순서를 절대 바꾸지 말 것 — 순서가 곧 메모리 레이아웃이다.
    /// 예상 크기 180 바이트(pack=1). 실장비 sizeof 로 검증 필요.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NslConfig
    {
        public int IntegrationTime3D;
        public int IntegrationTime3DHdr1;
        public int IntegrationTime3DHdr2;
        public int IntegrationTimeGrayScale;

        public int RoiXMin;
        public int RoiXMax;
        public int RoiYMin;
        public int RoiYMax;

        public int CurrentOffset;
        public int MinAmplitude;

        public int FirmwareRelease;
        public int ChipId;
        public int WaferId;

        public int UdpDataPort;
        public int LedMask;

        /// <summary>
        /// 설치 각도(도). ⚠ 이 값이 0이 아니면 nanolib 이 <c>distance3D</c> 에 회전을
        /// 미리 적용할 가능성이 있다. 좌표 변환은 전부 HD_AMR 의 hand-eye 캘리브레이션이
        /// 담당하므로, 이중 적용을 막기 위해 서비스는 항상 0으로 고정한다.
        /// </summary>
        public double LidarAngle;

        public LidarTypeOption LidarType;
        public LensType LensType;
        public OperationMode OperationModeOpt;
        public HdrOption HdrOpt;

        public ModulationOption ModFrequencyOpt;
        public ModulationChOption ModChannelOpt;
        public FunctionOption ModEnabledAutoChannelOpt;

        public DualBeamModOption DbModOpt;
        public DualBeamOpsOption DbOpsOpt;

        public FunctionOption VerBinningOpt;
        public FunctionOption HorizBinningOpt;

        public FunctionOption OverflowOpt;
        public FunctionOption SaturationOpt;

        public FunctionOption DrnuOpt;
        public FunctionOption TemperatureOpt;
        public FunctionOption GrayscaleOpt;
        public FunctionOption AmbientLightOpt;

        public FunctionOption MedianOpt;
        public FunctionOption GaussOpt;
        public int TemporalFactorValue;
        public int TemporalThresholdValue;
        public int EdgeThresholdValue;
        public int InterferenceDetectionLimitValue;
        public FunctionOption InterferenceDetectionLastValueOpt;
        public int EdgeThresholdValue3D;

        public UdpSpeedOption UdpSpeedOpt;
        public FrameRateOption FrameRateOpt;
        public FunctionOption GrayscaleIlluminationOpt;
    }

    // ── 수명주기 / 스트리밍 ───────────────────────────────────────────────

    /// <summary>
    /// 핸들 생성. 반환값이 음수면 <see cref="NslError"/> 코드다(예: -2 = NotOpened).
    ///
    /// nanolib 은 network(TCP) → CDC USB → vendor USB 순으로 자동 폴백한다. LAN 구성에서는
    /// IP 문자열을 넘기면 첫 시도에서 붙고, USB 구성에서는 이 문자열이 무시된다.
    /// ⚠ USB 와 LAN 을 동시에 물리 연결하면 연결이 끊긴다 — 반드시 한쪽만 연결할 것.
    /// </summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int nsl_open(string ipaddr, ref NslConfig config, FunctionOption enabledDebug);

    [LibraryImport(Lib)]
    public static partial NslError nsl_closeHandle(int handle);

    [LibraryImport(Lib)]
    public static partial NslError nsl_close();

    [LibraryImport(Lib)]
    public static partial NslError nsl_streamingOn(int handle, OperationMode type);

    [LibraryImport(Lib)]
    public static partial NslError nsl_streamingOff(int handle);

    [LibraryImport(Lib)]
    public static partial NslError nsl_requestSingleFrame(int handle, OperationMode type);

    /// <summary>
    /// 포인트클라우드 수신. <paramref name="pcdData"/> 는 <see cref="NslPcdLayout.SizeBytes"/>
    /// 바이트의 언매니지드 버퍼여야 한다(약 15MB).
    ///
    /// ⚠ 이 구조체를 값으로 마샬링하면 호출마다 15MB 를 복사한다. 반드시 한 번 할당해
    ///   재사용하고 <see cref="IntPtr"/> 로 넘길 것.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial NslError nsl_getPointCloudData(int handle, IntPtr pcdData, int waitTimeMs);

    // ── 설정 ──────────────────────────────────────────────────────────────

    [LibraryImport(Lib)]
    public static partial NslError nsl_setIntegrationTime(int handle, int intTime, int intTimeHdr1, int intTimeHdr2, int intGray);

    [LibraryImport(Lib)]
    public static partial NslError nsl_getIntegrationTime(int handle, out int intTime, out int intTimeHdr1, out int intTimeHdr2, out int intGray);

    [LibraryImport(Lib)]
    public static partial NslError nsl_setMinAmplitude(int handle, int minAmplitude);

    [LibraryImport(Lib)]
    public static partial NslError nsl_getMinAmplitude(int handle, out int minAmplitude);

    [LibraryImport(Lib)]
    public static partial NslError nsl_setHdrMode(int handle, HdrOption mode);

    [LibraryImport(Lib)]
    public static partial NslError nsl_getHdrMode(int handle, out HdrOption mode);

    [LibraryImport(Lib)]
    public static partial NslError nsl_setModulation(int handle, ModulationOption modulation, ModulationChOption ch, FunctionOption enableAutoChannel);

    [LibraryImport(Lib)]
    public static partial NslError nsl_getModulation(int handle, out ModulationOption modulation, out ModulationChOption ch, out FunctionOption enableAutoChannel);

    [LibraryImport(Lib)]
    public static partial NslError nsl_setFilter(int handle, FunctionOption enableMedian, FunctionOption enableGauss,
        int temporalFactor, int temporalThreshold, int edgeThreshold, int interferenceDetectionLimit,
        FunctionOption enableInterferenceDetectionLastValue);

    [LibraryImport(Lib)]
    public static partial NslError nsl_setRoi(int handle, int minX, int minY, int maxX, int maxY);

    [LibraryImport(Lib)]
    public static partial NslError nsl_getRoi(int handle, out int minX, out int minY, out int maxX, out int maxY);

    [LibraryImport(Lib)]
    public static partial NslError nsl_setFrameRate(int handle, FrameRateOption rate);

    [LibraryImport(Lib)]
    public static partial NslError nsl_setUdpSpeed(int handle, UdpSpeedOption speed);

    [LibraryImport(Lib)]
    public static partial NslError nsl_getUdpSpeed(int handle, out UdpSpeedOption speed);

    [LibraryImport(Lib)]
    public static partial NslError nsl_setGrayscaleillumination(int handle, FunctionOption onOff);

    [LibraryImport(Lib)]
    public static partial NslError nsl_setLidarAngle(int handle, double angle);

    [LibraryImport(Lib)]
    public static partial NslError nsl_getLidarAngle(int handle, out double angle);

    [LibraryImport(Lib)]
    public static partial NslError nsl_getBitInfo(int handle, out NslBitInfo bitInfo);

    [LibraryImport(Lib)]
    public static partial NslError nsl_getCurrentConfig(int handle, ref NslConfig config);

    [LibraryImport(Lib)]
    public static partial uint nsl_getSdkVersion(int handle);

    // ⚠ nsl_saveConfiguration 은 장비 EEPROM 에 영구 기록한다. 튜닝 때마다 호출하면
    //   플래시 쓰기 수명을 깎으므로, 명시적인 persist 요청에서만 쓰도록 의도적으로
    //   별도 선언해 두었다.
    [LibraryImport(Lib)]
    public static partial NslError nsl_saveConfiguration(int handle);
}

/// <summary>
/// <c>NslPCD</c> 의 메모리 레이아웃. 15MB 짜리 구조체라 C# 구조체로 선언해 마샬링하지 않고,
/// 언매니지드 버퍼를 직접 오프셋으로 읽는다.
///
/// 헤더의 <c>#pragma pack(1)</c> 기준이며, <c>bool</c> 은 1바이트, <c>enum class</c> 는
/// 4바이트다. 배열은 항상 TYPE_B(800x600) 최대 크기로 정적 할당되어 있으므로,
/// TYPE_A(320x240) 장비라도 <b>row stride 는 언제나 800</b>이다. <c>width</c> 로 나누면 틀린다.
///
/// ✅ <b>실장비 검증 완료</b>(2026-08-07, Jetson Orin Nano / aarch64 / nanolib 1.13):
/// sizeof 15,360,104 / width 72 / amplitude 104 / distance2D 1,920,104 /
/// distance3D 3,840,104 가 C++ <c>offsetof</c> 실측치와 정확히 일치했다.
/// Pack=1 은 <c>NslBitInfo.voltage</c> 오프셋이 8(자연 정렬)이 아니라 4인 것으로 확증됐다.
/// 펌웨어나 SDK 가 바뀌면 <see cref="Verify"/> 로 다시 대조할 것.
/// </summary>
internal static class NslPcdLayout
{
    public const int MaxWidth = NslNative.TypeBWidth;    // 800
    public const int MaxHeight = NslNative.TypeBHeight;  // 600
    public const int PixelCount = MaxWidth * MaxHeight;  // 480,000

    public const int OperationMode = 0;    // int
    public const int LidarType = 4;        // int
    public const int Temperature = 8;      // double
    public const int IncludeRgb = 16;      // bool(1)
    public const int IncludeLidar = 17;    // bool(1)
    public const int IncludeImu = 18;      // bool(1)
    public const int IncludeYml = 19;      // bool(1)
    public const int ImuData = 20;         // float x 13 = 52
    public const int Width = 72;           // int
    public const int Height = 76;
    public const int RoiXMin = 80;
    public const int RoiYMin = 84;
    public const int RoiXMax = 88;
    public const int RoiYMax = 92;
    public const int BinningH = 96;
    public const int BinningV = 100;

    /// <summary>int[600][800] — 1,920,000 바이트.</summary>
    public const int Amplitude = 104;

    /// <summary>int[600][800] — 1,920,000 바이트.</summary>
    public const int Distance2D = Amplitude + PixelCount * sizeof(int);

    /// <summary>double[3][600][800] — 11,520,000 바이트. X 전체 → Y 전체 → Z 전체 순.</summary>
    public const int Distance3D = Distance2D + PixelCount * sizeof(int);

    /// <summary>한 축(X 또는 Y 또는 Z)의 바이트 크기.</summary>
    public const int Distance3DPlaneBytes = PixelCount * sizeof(double);

    public const int SizeBytes = Distance3D + NslNative.MaxOut * Distance3DPlaneBytes; // 15,360,104

    /// <summary>
    /// 실장비에서 얻은 sizeof/offsetof 값과 대조한다. 젯슨 진단 보고서(항목 5-1, 5-2)의
    /// 수치를 여기에 넣어 호출하면, 레이아웃이 어긋난 상태로 조용히 동작하는 것을 막는다.
    /// </summary>
    public static void Verify(int actualSizeBytes, int actualWidthOffset, int actualAmplitudeOffset,
        int actualDistance2DOffset, int actualDistance3DOffset)
    {
        static void Check(string name, int expected, int actual)
        {
            if (expected != actual)
                throw new InvalidOperationException(
                    $"NslPCD 레이아웃 불일치: {name} 기대값 {expected}, 실제 {actual}. " +
                    "nanolib 헤더가 바뀌었거나 pack 규약이 다르다. 오프셋 상수를 갱신할 것.");
        }

        Check(nameof(SizeBytes), SizeBytes, actualSizeBytes);
        Check(nameof(Width), Width, actualWidthOffset);
        Check(nameof(Amplitude), Amplitude, actualAmplitudeOffset);
        Check(nameof(Distance2D), Distance2D, actualDistance2DOffset);
        Check(nameof(Distance3D), Distance3D, actualDistance3DOffset);
    }
}
