using System;
using System.Globalization;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.DXGI;

/// <summary>The display and swap-chain color configuration for a player window.</summary>
internal sealed class DisplayHdr
{
    const ColorSpaceType Pq2020 = ColorSpaceType.RgbFullG2084NoneP2020;
    const ColorSpaceType Srgb709 = ColorSpaceType.RgbFullG22NoneP709;
    public string DeviceName { get; private set; }
    public string FriendlyName { get; private set; }
    public bool Enabled { get; private set; }
    public bool WindowsHdrActive { get; private set; }
    public bool? HdrSupported { get; private set; }
    public double RefreshHz { get; private set; }
    public int BitsPerColor { get; private set; }
    public bool HasReportedPeak { get; private set; }
    public float PeakNits { get; private set; }
    public float FullFrameNits { get; private set; }
    public float SdrWhiteNits { get; private set; }
    public string Summary
    {
        get
        {
            string state = WindowsHdrActive ? "Windows HDR on" : HdrSupported==false ? "SDR display" : "Windows HDR off";
            string peak=HasReportedPeak ? PeakNits.ToString("0",CultureInfo.InvariantCulture)+" nit peak" : "peak unavailable";
            return string.Format(CultureInfo.InvariantCulture,"{0} · {1} · {2}-bit · {3}",
                FriendlyName ?? DeviceName,state,BitsPerColor,peak);
        }
    }

    public static Format OutputFormat(bool hdr10)
    {
        return hdr10 ? Format.R10G10B10A2_UNorm : Format.B8G8R8A8_UNorm;
    }

    public static DisplayHdr Probe(IntPtr hwnd)
    {
        IntPtr monitor=MonitorFromWindow(hwnd,2);
        // A fresh factory reflects monitor moves and Windows HDR toggles.
        using (var factory=new Factory1())
        {
            int adapterCount=factory.GetAdapterCount1();
            for (int a=0;a<adapterCount;a++)
                using (var adapter=factory.GetAdapter1(a))
                {
                    int outputCount=adapter.GetOutputCount();
                    for (int i=0;i<outputCount;i++)
                        using (var output=adapter.GetOutput(i))
                        {
                            var basic=output.Description;
                            if (!basic.IsAttachedToDesktop || basic.MonitorHandle!=monitor) continue;
                            var result=new DisplayHdr {
                                DeviceName=basic.DeviceName,PeakNits=1000,FullFrameNits=0,SdrWhiteNits=80
                            };
                            using (var extended=output.QueryInterfaceOrNull<Output6>())
                            {
                                if (extended!=null)
                                {
                                    var info=extended.Description1;
                                    result.WindowsHdrActive=info.ColorSpace==Pq2020;
                                    if (result.WindowsHdrActive) result.HdrSupported=true;
                                    result.BitsPerColor=info.BitsPerColor;
                                    if (info.MaxLuminance>=40 && info.MaxLuminance<=10000)
                                    {
                                        result.PeakNits=info.MaxLuminance;
                                        result.HasReportedPeak=true;
                                    }
                                    if (info.MaxFullFrameLuminance>0 && info.MaxFullFrameLuminance<=10000) result.FullFrameNits=info.MaxFullFrameLuminance;
                                }
                            }
                            ReadDisplayConfig(result);
                            return result;
                        }
                }
        }
        throw new InvalidOperationException("The player window has no active DXGI monitor. Reconnect the display and reopen the player.");
    }

    public static DisplayHdr Configure(SwapChain chain,bool hdr10,DisplayHdr knownOutput=null)
    {
        if (chain==null) throw new ArgumentNullException("chain");
        var desc=chain.Description;
        DisplayHdr output=knownOutput ?? Probe(desc.OutputHandle);
        if (hdr10 && !output.WindowsHdrActive)
        {
            if(output.HdrSupported==false)
                throw new InvalidOperationException((output.FriendlyName ?? output.DeviceName)+" does not report HDR support. Move the player to an HDR display or select SDR.");
            throw new InvalidOperationException("Enable Use HDR in Windows Display settings for "+(output.FriendlyName ?? output.DeviceName)+" or select SDR.");
        }
        if (hdr10 && desc.ModeDescription.Format!=OutputFormat(true))
            throw new InvalidOperationException("HDR10 presentation requires a 10-bit output buffer.");
        using (var swap3=chain.QueryInterfaceOrNull<SwapChain3>())
        {
            if (swap3==null && hdr10) throw new InvalidOperationException("This DXGI swap chain does not support HDR color spaces.");
            if(swap3!=null)
            {
                ColorSpaceType space=hdr10 ? Pq2020 : Srgb709;
                bool supported=(swap3.CheckColorSpaceSupport(space) & SwapChainColorSpaceSupportFlags.Present)!=0;
                if(hdr10 && !supported) throw new InvalidOperationException("Windows cannot present HDR10 to this output in its current display mode.");
                if(supported) swap3.ColorSpace1=space;
            }
        }
        // WGC supplies scRGB pixels without source mastering metadata. Windows recommends
        // display-referred tone mapping rather than synthesizing SetHDRMetaData records.
        output.Enabled=hdr10;
        return output;
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags,ref uint pathCount,
        [Out] DisplayPath[] paths,ref uint modeCount,[Out] DisplayMode[] modes,IntPtr topology);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetSourceName(ref SourceName data);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetTargetName(ref TargetName data);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetAdvancedColor(ref AdvancedColor data);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetSdrWhite(ref SdrWhite data);

    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    // DISPLAYCONFIG_PATH_INFO contains 20-byte source and 48-byte target parts and flags.
    [StructLayout(LayoutKind.Explicit,Size=72)] struct DisplayPath
    {
        [FieldOffset(0)] public Luid SourceAdapter;
        [FieldOffset(8)] public uint SourceId;
        [FieldOffset(20)] public Luid TargetAdapter;
        [FieldOffset(28)] public uint TargetId;
        [FieldOffset(48)] public uint RefreshNumerator;
        [FieldOffset(52)] public uint RefreshDenominator;
    }
    [StructLayout(LayoutKind.Sequential,Size=64)] struct DisplayMode { public uint InfoType; }
    [StructLayout(LayoutKind.Sequential)] struct Header
    {
        public uint Type,Size;
        public Luid Adapter;
        public uint Id;
        public static Header For<T>(uint type,Luid adapter,uint id)
        {
            return new Header { Type=type,Size=(uint)Marshal.SizeOf(typeof(T)),Adapter=adapter,Id=id };
        }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string GdiName;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct TargetName
    {
        public Header Header;
        public uint Flags,OutputTechnology;
        public ushort Manufacturer,Product;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Friendly;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string DevicePath;
    }
    [StructLayout(LayoutKind.Sequential)] struct AdvancedColor
    {
        public Header Header;
        public uint Flags,Encoding,BitsPerColor,ActiveColorMode;
    }
    [StructLayout(LayoutKind.Sequential)] struct SdrWhite { public Header Header; public uint Level; }

    static void ReadDisplayConfig(DisplayHdr output)
    {
        const uint onlyActivePaths=2;
        for(int attempt=0;attempt<3;attempt++)
        {
            uint pathCount,modeCount;
            if(GetDisplayConfigBufferSizes(onlyActivePaths,out pathCount,out modeCount)!=0) return;
            var paths=new DisplayPath[pathCount];
            var modes=new DisplayMode[modeCount];
            int status=QueryDisplayConfig(onlyActivePaths,ref pathCount,paths,ref modeCount,modes,IntPtr.Zero);
            if(status==122) continue; // topology changed between the two calls
            if(status!=0) return;
            for(int i=0;i<pathCount;i++)
            {
                var p=paths[i];
                var source=new SourceName { Header=Header.For<SourceName>(1,p.SourceAdapter,p.SourceId) };
                if(GetSourceName(ref source)!=0 || !string.Equals(source.GdiName,output.DeviceName,StringComparison.OrdinalIgnoreCase)) continue;
                if(p.RefreshDenominator!=0) output.RefreshHz=(double)p.RefreshNumerator/p.RefreshDenominator;
                var target=new TargetName { Header=Header.For<TargetName>(2,p.TargetAdapter,p.TargetId) };
                if(GetTargetName(ref target)==0 && !string.IsNullOrWhiteSpace(target.Friendly))
                    output.FriendlyName=target.Friendly;
                // GET_ADVANCED_COLOR_INFO_2 separates HDR from Windows wide-color SDR.
                var color=new AdvancedColor { Header=Header.For<AdvancedColor>(15,p.TargetAdapter,p.TargetId) };
                if(GetAdvancedColor(ref color)==0)
                {
                    output.HdrSupported=(color.Flags & (1u<<4))!=0;
                    output.WindowsHdrActive=color.ActiveColorMode==2;
                    output.BitsPerColor=(int)color.BitsPerColor;
                }
                var white=new SdrWhite { Header=Header.For<SdrWhite>(11,p.TargetAdapter,p.TargetId) };
                if(GetSdrWhite(ref white)==0 && white.Level>=500 && white.Level<=100000)
                    output.SdrWhiteNits=80f*white.Level/1000f;
                return;
            }
            return;
        }
    }
}
