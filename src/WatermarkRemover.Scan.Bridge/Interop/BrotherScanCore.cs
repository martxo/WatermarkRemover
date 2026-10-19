// P/Invoke surface of the native ScanCoreApi.dll shipped with Brother iPrint&Scan.
// Transcribed field-for-field from the managed facade the target itself uses
// (ScanCore.NetStandard.dll / BRAppDevKitScan.dll, decompiled in research/brother-ipscan).
// Struct sizes must match the native layout exactly, so every field of every nested
// struct is kept even when this helper only sets a handful of them.

using System.Runtime.InteropServices;

namespace WatermarkRemover.Scan.Bridge.Interop;

internal static partial class BrotherScanCore
{
    private const string DllName = "ScanCoreApi";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "Initialize")]
    internal static extern uint Initialize(string bundleId, [MarshalAs(UnmanagedType.U1)] bool isStiAccess);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnInitialize")]
    internal static extern void UnInitialize();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SelectDevice")]
    internal static extern uint SelectDevice(ref BS_SELECT_DEVICE_INFO deviceInfo);

    // The native capability struct is hundreds of fields long and size-critical; this helper only
    // needs the model name for diagnostics, so the engine fills an oversized scratch buffer and
    // the first fields (commandVersion, modelName) are read at their stable offsets.
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "QueryDeviceCapabilities")]
    internal static extern uint QueryDeviceCapabilities(IntPtr capabilityBuffer);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "QueryAdfState")]
    internal static extern AdfState QueryAdfState();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetPushScanParameter")]
    internal static extern uint GetPushScanParameter(ref BS_PUSHSCAN_PARAMETERS pushScanParameters);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "StartScanning")]
    internal static extern uint StartScanning(ref BS_SCANNER_PARAMETERS parameters);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ReadImageData")]
    internal static extern uint ReadImageData(ref BS_SCAN_IMAGEDATA imageData, ref int pageNo);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "AbortScanning")]
    internal static extern byte AbortScanning();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FinishAutoContinuousScan")]
    internal static extern byte FinishAutoContinuousScan();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FreeImageData")]
    internal static extern void FreeImageData(int pageNo);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetScanStatus")]
    internal static extern ScanApiStatus GetScanStatus();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "CheckConnection")]
    internal static extern byte CheckConnection();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadLibraryEx(string fileName, IntPtr fileHandle, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool SetDllDirectory(string lpPathName);

    internal const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

    // --- enums (zero value = feature off; only zero matters for fields we leave default) ---

    internal enum devif_type
    {
        DEVIF_TYPE_UNKNOWN = 0,
        DEVIF_TYPE_USB = 1,
        DEVIF_TYPE_NETWORK = 2,
        DEVIF_TYPE_WINSTI = 3,
    }

    internal enum devid_type
    {
        DEVID_TYPE_UNKNOWN = 0,
        DEVID_TYPE_DEVICENAME = 1,
        DEVID_TYPE_USBID = 2,
        DEVID_TYPE_NETADDR = 3,
        DEVID_TYPE_LOCATIONID = 4,
        DEVID_TYPE_USBPORTNAME = 5,
    }

    internal enum COLORTYPE
    {
        AUTOCOLOR = 0,
        BLACKWHITE = 1,
        ERRDIFFUSION = 2,
        TRUEGRAY = 3,
        COLOR256 = 4,
        COLOR24BIT = 5,
        COLORTYPENUM = 6,
    }

    internal enum DUPLEXTYPE
    {
        SIMPLEX = 0,
        DUPLEX_LONG = 1,
        DUPLEX_SHORT = 2,
    }

    internal enum PAPERSOURCESELECT
    {
        SOURCEAUTO = 0,
        FLATBED = 1,
        ADF = 2,
        CARDSLOT = 3,
    }

    internal enum PAPERTYPE
    {
        AUTOSIZE = 0,
        A4 = 1,
        LETTER = 2,
        LEGAL = 3,
        A3 = 4,
        LEDGER = 5,
        JISB4 = 6,
        JISB5 = 7,
        A5 = 8,
        HALFLETTER = 9,
        JISB6 = 10,
        A6 = 11,
        EXECUTIVE = 12,
        PHOTO = 13,
        INDEXCARD = 14,
        PHOTOL = 15,
        PHOTO2L = 16,
        POSTCARD = 17,
        POSTCARD2 = 18,
        BCARD90X60 = 19,
        BCARD60X90 = 20,
        FOLIO = 21,
        MEXICANLEGAL = 22,
        INDIALEGAL = 23,
        LONGPAPER_NARROW = 24,
        LONGPAPER_WIDE = 25,
        DEVICEMAX = 26,
        CUSTOMSIZE = 27,
        PAPERTYPENUM = 28,
    }

    internal enum UNITTYPE
    {
        INCH = 0,
        MM = 1,
        PIXEL = 2,
    }

    internal enum ScanApiStatus
    {
        StandBy = 0,
        WaitingNextPage = 1,
        Receiving = 2,
        XferReady = 3,
        WaitingDocumentInsertion = 4,
        AllEnd = 5,
    }

    internal enum AdfState
    {
        ConnectionError = 0,
        DeviceError = 1,
        Exists = 2,
        NotExists = 3,
        NoADF = 4,
        Cancel = 5,
        NonSeparableMode = 6,
    }

    internal enum IMAGEORIENTATION
    {
        ROT0 = 0,
        ROT90 = 1,
        ROT180 = 2,
        ROT270 = 3,
    }

    internal enum IMAGEMERGETYPE
    {
        MERGE_NONE = 0,
    }

    internal enum IMAGEDIVIDETYPE
    {
        DIVIDE_NONE = 0,
    }

    internal enum IMAGEQUALITYTYPE
    {
        DEFAULT = 0,
    }

    internal enum APPTYPE
    {
        UNKNOWN_APP = 0,
    }

    internal enum ColorToneAdjustmentMethod
    {
        COLOR_TONE_NONE = 0,
    }

    internal enum ColorToneMode
    {
        DOCUMENT_TONE = 0,
    }

    internal enum LINEDIRECTION
    {
        LINE_VERTICAL = 0,
        LINE_HORIZONTAL = 1,
        LINE_VERTICAL_HORIZONAL = 2,
    }

    internal enum SELECTCOLOR
    {
        BLACK = 0,
        WHITE = 1,
        NEARBYCOLOR = 2,
    }

    internal enum BARCODE_DIRECTION
    {
        BARCODE_AUTO = 0,
    }

    internal enum BARCODE_TYPE
    {
        CODE39 = 0,
    }

    internal enum PATCHCODE_TYPE
    {
        PATCH_T = 0,
    }

    // --- structs ---

    internal struct UsbId
    {
        public ushort idVendor;
        public ushort idProduct;
    }

    internal struct BS_SELECT_DEVICE_INFO
    {
        public devif_type typeDeviceIf;
        public devid_type typeDeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string nameDevice;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string addrNetwork;

        public UsbId usbId;
        public uint idLocation;
    }

    internal struct POSSIBLERANGE
    {
        public int min;
        public int max;
    }

    internal struct ADJUSTMENTPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool execute;

        public int level;
    }

    internal struct ERRORDIFFUSIONTONEPARAM
    {
        public int brightness;
        public int contrast;
    }

    internal struct COLORTONEPARAM
    {
        public ColorToneAdjustmentMethod method;
        public ColorToneMode mode;
        public int manualBrightness;
        public int manualContrast;
        public int manualShadow;
        public int manualHighlight;
        public int manualGamma;
    }

    internal struct AUTOCOLORLEVELPARAM
    {
        public int monoColorDetectLevel;
        public int monoGrayDetectLevel;
    }

    internal struct SMOOTHINGPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool bleedThrough;

        [MarshalAs(UnmanagedType.U1)]
        public bool backgroundRemoval;

        public int level;
    }

    internal struct COLORDROP_CUSTOMCOLOR
    {
        public byte redVal;
        public byte greenVal;
        public byte blueVal;
        public uint level;

        [MarshalAs(UnmanagedType.U1)]
        public bool keepDarkColor;
    }

    internal struct COLORDROPPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool drop;

        [MarshalAs(UnmanagedType.U1)]
        public bool chromaticColor;

        [MarshalAs(UnmanagedType.U1)]
        public bool dropRed;

        [MarshalAs(UnmanagedType.U1)]
        public bool dropGreen;

        [MarshalAs(UnmanagedType.U1)]
        public bool dropBlue;

        [MarshalAs(UnmanagedType.U1)]
        public bool custom;

        public COLORDROP_CUSTOMCOLOR customColor;
    }

    internal struct REMOVELINEPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool lineRemove;

        public LINEDIRECTION direction;
        public uint minLength;
        public uint dashedLength;
        public UNITTYPE unit;
    }

    internal struct PUNCHHOLEPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool remove;

        public SELECTCOLOR fillColor;
    }

    internal struct COMBINEIMAGESPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool execute;

        public uint width;
        public uint height;
        public uint space;
        public UNITTYPE unit;
    }

    internal struct CARDMODEPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool execute;

        public IMAGEMERGETYPE margeType;

        [MarshalAs(UnmanagedType.U1)]
        public bool invertBack;

        public IMAGEORIENTATION rotationAngle;
    }

    internal struct RECT
    {
        public int top;
        public int bottom;
        public int left;
        public int right;
    }

    internal struct EDGECUTPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool adjust;

        public RECT rect;
        public UNITTYPE unit;
    }

    internal struct TEARFILLPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool execute;

        public SELECTCOLOR color;
        public uint fillArea;
        public UNITTYPE unit;
    }

    internal struct MARGINSIZE
    {
        public UNITTYPE unit;
        public int top;
        public int bottom;
        public int left;
        public int right;
    }

    internal struct EDGEFILLPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool specifiedArea;

        public SELECTCOLOR color;
        public MARGINSIZE margin;
    }

    internal struct AUTOCONTINUOUSSCANPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool execute;

        public byte timeout;
        public byte feedDelay;
    }

    internal struct SCANAREA
    {
        public int offsetX;
        public int offsetY;
        public int width;
        public int height;
        public UNITTYPE unit;
    }

    internal struct AXIS
    {
        public uint x;
        public uint y;
    }

    internal struct PAPERSIZE
    {
        public AXIS size;
        public UNITTYPE unit;
    }

    internal struct SCANAREAINFO
    {
        public PAPERTYPE papertype;
        public PAPERSIZE customPaperSize;

        [MarshalAs(UnmanagedType.U1)]
        public bool areaSelected;

        public SCANAREA selectArea;
    }

    internal struct BARCODEPARAMETER
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool execute;

        public byte maxNum;
        public BARCODE_DIRECTION searchDirection;
        public byte retryNum;

        [MarshalAs(UnmanagedType.U1)]
        public bool specifyArea;

        public SCANAREA area;

        [MarshalAs(UnmanagedType.U1)]
        public bool addStartStopToCodabar;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
        public BARCODE_TYPE[] types;

        public ushort typesNum;
    }

    internal struct PATCHCODEPARAMETER
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool execute;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
        public PATCHCODE_TYPE[] types;

        public ushort typesNum;
    }

    internal struct LOWSPEEDMODEPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool execute;

        public uint delayTime;
    }

    internal struct USERAUTHENTICATIONINFO
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 513)]
        public byte[] userName;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
        public byte[] userPassword;
    }

    internal struct OUTPUT_FORMAT
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool alignTo4Bytes;

        [MarshalAs(UnmanagedType.U1)]
        public bool noDecodeForJpeg;
    }

    internal struct BS_ADVANCEDSCAN
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool autocrop;

        [MarshalAs(UnmanagedType.U1)]
        public bool deskew;

        [MarshalAs(UnmanagedType.U1)]
        public bool autoRotate;

        public int autoRotateLangId;

        [MarshalAs(UnmanagedType.U1)]
        public bool detectEndOfPage;

        public IMAGEORIENTATION rotateImageAngle;

        [MarshalAs(UnmanagedType.U1)]
        public bool LetterLegalFixedSize;

        public int monoThreshold;

        public ERRORDIFFUSIONTONEPARAM diffusionTone;
        public COLORTONEPARAM colorTone;
        public AUTOCOLORLEVELPARAM autoColorLevel;
        public ADJUSTMENTPARAM backgroundRemoval;
        public SMOOTHINGPARAM smoothing;
        public COLORDROPPARAM colorDrop;
        public ADJUSTMENTPARAM thickText;

        [MarshalAs(UnmanagedType.U1)]
        public bool dotTextCombine;

        public ADJUSTMENTPARAM edgeEnhance;

        [MarshalAs(UnmanagedType.U1)]
        public bool reduceNoise;

        [MarshalAs(UnmanagedType.U1)]
        public bool moireRemoval;

        public ADJUSTMENTPARAM moireFilter;

        [MarshalAs(UnmanagedType.U1)]
        public bool verticalStreaksReduction;

        public ADJUSTMENTPARAM dustRemoval;
        public REMOVELINEPARAM lineRemove;
        public PUNCHHOLEPARAM punchHoleRemove;
        public IMAGEMERGETYPE imageMerge;
        public IMAGEDIVIDETYPE imageDivide;
        public COMBINEIMAGESPARAM combineImages;

        [MarshalAs(UnmanagedType.U1)]
        public bool carriersheet;

        public CARDMODEPARAM cardMode;
        public EDGECUTPARAM edgeCut;
        public TEARFILLPARAM tearFill;
        public EDGEFILLPARAM edgeFill;
        public ADJUSTMENTPARAM blankRemoval;
        public int xferCount;

        [MarshalAs(UnmanagedType.U1)]
        public bool quietMode;

        public AUTOCONTINUOUSSCANPARAM autoContinuousScan;

        [MarshalAs(UnmanagedType.U1)]
        public bool multifeedDetect;

        public BARCODEPARAMETER detectBarcode;
        public PATCHCODEPARAMETER detectPatchcode;
        public LOWSPEEDMODEPARAM lowSpeedMode;
        public IMAGEQUALITYTYPE imageQualityType;

        [MarshalAs(UnmanagedType.U1)]
        public bool iccProfile;

        public uint imageProcessingThreadMaxNum;

        [MarshalAs(UnmanagedType.U1)]
        public bool isRestartScan;
    }

    internal struct BS_SCANNER_PARAMETERS
    {
        public uint resolutionX;
        public uint resolutionY;
        public COLORTYPE colortype;
        public SCANAREAINFO scanAreaInfo;
        public int brightness;
        public int contrast;
        public DUPLEXTYPE duplextype;
        public PAPERSOURCESELECT paperSource;

        [MarshalAs(UnmanagedType.U1)]
        public bool landscapeMode;

        public BS_ADVANCEDSCAN enhanced;
        public OUTPUT_FORMAT output;
        public USERAUTHENTICATIONINFO userAuthenticationInfo;
        public APPTYPE appType;
    }

    internal struct BS_SCAN_IMAGEDATA
    {
        public IntPtr pDataPtr;
        public uint dataSize;
        public int resolutionX;
        public int resolutionY;
        public int bpp;
        public int width;
        public int height;
        public int widthbytes;

        [MarshalAs(UnmanagedType.U1)]
        public bool isJpegCompression;

        [MarshalAs(UnmanagedType.U1)]
        public bool isBlankPage;

        [MarshalAs(UnmanagedType.U1)]
        public bool isScanTerminated;

        public bool shouldDxRotate;

        public int isImageEnhanceTerminated;

        public PAPERSOURCESELECT paperSource;
    }

    // ---- Push-scan (panel key event) structures, transcribed from the target's wrapper ----
    internal enum SELECTEDSCANEVENT
    {
        SCANTOIMAGE = 0,
        SCANTOOCR = 1,
        SCANTOEMAIL = 2,
        SCANTOFILE = 3,
    }

    internal enum SCANTIMING
    {
        NORMAL = 0,
        PRESCANNING = 1,
    }

    internal enum FILETYPE
    {
        BMP = 0,
        JPEG = 1,
        TIFF = 2,
        PNG = 3,
        IMAGE_PDF = 4,
        SECURE_PDF = 5,
        SEARCHABLE_PDF = 6,
        HIGHCOMPRESSION_PDF = 7,
        XPS = 8,
        TXT = 9,
        HTML = 10,
        RTF = 11,
    }

    internal enum COMPRESSIONFORMAT
    {
        COMPRESS_NONE = 0,
        COMPRESS_CCITT3 = 1,
        COMPRESS_CCITT4 = 2,
        COMPRESS_LZW = 3,
        COMPRESS_JPEG = 4,
    }

    internal enum SPLITFILETYPE
    {
        MULTIPAGE_FORMAT = 0,
        ONE_PAGE = 1,
        TWO_PAGE = 2,
        BLANK_PAGE = 3,
        PAGE_NUM = 4,
        UNSUPPORTED_MULTIPAGE = 5,
    }

    internal struct AXIS_BOOL
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool x;

        [MarshalAs(UnmanagedType.U1)]
        public bool y;
    }

    internal struct SKEYINFO_SCANEVENT
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        public SELECTEDSCANEVENT scanButtonType;
    }

    internal struct SKEYINFO_SCANTIMING
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        public SCANTIMING scanTiming;
    }

    internal struct SKEYINFO_DUPLEXTYPE
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        public DUPLEXTYPE duplexType;
    }

    internal struct SKEYINFO_RESOLUTION
    {
        public AXIS_BOOL changed;

        public AXIS_BOOL autoDpi;

        public AXIS dpi;
    }

    internal struct SKEYINFO_COLORTYPE
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        public COLORTYPE colorType;
    }

    internal struct SKEYINFO_PAPERTYPE
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        public PAPERTYPE paperType;
    }

    internal struct SKEYINFO_SETTINGVALUE
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        public int value;
    }

    internal struct SKEYINFO_SETTINGSWITCH
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        [MarshalAs(UnmanagedType.U1)]
        public bool switchState;
    }

    internal struct SKEYINFO_FILETYPE
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        public FILETYPE fileType;

        public SKEYINFO_SETTINGVALUE jpgSizeLevel;

        public SKEYINFO_SETTINGSWITCH tiffCompress;

        public COMPRESSIONFORMAT compressionFormat;

        public SKEYINFO_SPLITFILE splitFile;
    }

    internal struct SKEYINFO_SETTINGSWITCHVALUE
    {
        public SKEYINFO_SETTINGSWITCH status;

        public SKEYINFO_SETTINGVALUE level;
    }

    internal struct SKEYINFO_SPLITFILE
    {
        public SPLITFILETYPE splitFileType;

        public int splitPageNum;
    }

    internal struct SKEYINFO_DOCROTATEPARAM
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool autoRotate;

        public int degree;
    }

    internal struct SKEYINFO_DOCROTATE
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        public SKEYINFO_DOCROTATEPARAM rotateParam;
    }

    internal struct SKEYINFO_ADJUSTMARGIN
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool changed;

        public MARGINSIZE margin;
    }

    internal struct BS_PUSHSCAN_PARAMETERS
    {
        public SKEYINFO_SCANEVENT scanButtonType;

        public SKEYINFO_SCANTIMING scanTiming;

        public SKEYINFO_DUPLEXTYPE duplexType;

        public SKEYINFO_RESOLUTION resolution;

        public SKEYINFO_COLORTYPE colorType;

        public SKEYINFO_PAPERTYPE paperType;

        public SKEYINFO_SETTINGVALUE brightness;

        public SKEYINFO_SETTINGVALUE contrast;

        public SKEYINFO_SETTINGSWITCH autocrop;

        public SKEYINFO_FILETYPE fileType;

        public SKEYINFO_SETTINGSWITCHVALUE blankRemoval;

        public SKEYINFO_SETTINGSWITCH bleedThroughtRemove;

        public SKEYINFO_DOCROTATE orient;

        public SKEYINFO_SETTINGSWITCH deskew;

        public SKEYINFO_SETTINGSWITCHVALUE backgroundRemove;

        public SKEYINFO_SETTINGSWITCH moireRemoval;

        public SKEYINFO_ADJUSTMARGIN adjustMargin;
    }

    // BS_SCANNER_CAPABILITY is received through an unmanaged scratch buffer (see
    // QueryDeviceCapabilities above); the model name sits at byte offset 4 as the first
    // two native fields are `uint commandVersion; char modelName[256];`.
    internal static class CapabilityLayout
    {
        internal const int CommandVersionOffset = 0;
        internal const int ModelNameOffset = 4;
        internal const int ModelNameLength = 256;
        internal const int ScratchBufferSize = 64 * 1024;
    }
}
