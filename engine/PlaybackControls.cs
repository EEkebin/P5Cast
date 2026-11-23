using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// An owned tool window keeps desktop composition, keyboard navigation, and hit-testing
// independent of the SDR/HDR video swap chain. It has no taskbar or Alt+Tab entry.
internal sealed class PlaybackControls : Form
{
    internal static readonly string[] Modes={"original","sharp","fsr","rtxvideo","dlss","dlaa"};
    static readonly string[] Names={"Original","Sharpen","AMD FSR 1","RTX Video SR","DLSS","DLAA"};
    static readonly Color Surface=Color.FromArgb(19,30,35), Idle=Color.FromArgb(31,45,51),
        Edge=Color.FromArgb(63,81,87), TextColor=Color.FromArgb(235,244,243),
        Muted=Color.FromArgb(174,193,195), Mint=Color.FromArgb(146,232,209);
    readonly PlayerWindow player;
    readonly IntPtr source;
    readonly Panel panel=new Panel();
    readonly List<Control> children=new List<Control>();
    readonly List<Rectangle> layout=new List<Rectangle>();
    readonly List<int> sizes=new List<int>();
    readonly Dictionary<int,Font> fonts=new Dictionary<int,Font>();
    readonly Button chip, pin;
    readonly Button[] modes=new Button[Modes.Length], factors=new Button[4];
    readonly Label target,input,output,generated,format,timing,network,notice;
    readonly PlaybackGraph graph=new PlaybackGraph(), miniGraph=new PlaybackGraph();
    readonly TimingBar videoTime=new TimingBar(PlaybackGraph.InputColor), audioTime=new TimingBar(PlaybackGraph.GeneratedColor);
    readonly int targetFps;
    readonly ToolTip tips=new ToolTip();
    bool expanded,pinned;
    int lastDpi;
    double chipUntil=5200, nextPlacement, lastSample;
    int captured,presented,created;
    string selection, lastNotice="", compactRates="";
    int multiplier;
    bool probing;
    string displayName; double refreshHz;
    public event Action<string,int> SettingChanged;

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attr,ref int value,int size);

    public PlaybackControls(PlayerWindow player,IntPtr source,string mode,int multiplier,int targetFps)
    {
        this.player=player; this.source=source;
        selection=mode; this.multiplier=multiplier; this.targetFps=targetFps;
        Text="P5Cast playback controls"; AccessibleName=Text;
        FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual;
        AutoScaleMode=AutoScaleMode.None; ShowInTaskbar=false; KeyPreview=true;
        BackColor=Surface; ForeColor=TextColor; Owner=player; DoubleBuffered=true;
        chip=MakeButton("Playback controls    Ctrl+Alt+O",delegate { Open(); });
        chip.AccessibleName="Open playback controls"; tips.SetToolTip(chip,"Picture, frame generation and live stats");
        Controls.Add(chip); Controls.Add(panel); Controls.Add(miniGraph); panel.BackColor=Surface;
        miniGraph.Cursor=Cursors.Hand; miniGraph.Click+=delegate { Open(); };
        Add(Label("Playback controls",TextColor),16,10,228,29,20);
        var close=MakeButton("Close",delegate { Collapse(true); });
        close.AccessibleName="Close playback controls"; Add(close,282,11,58,27,12);
        target=Metric("Target fps",16,PlaybackGraph.TargetColor); input=Metric("Input fps",99,PlaybackGraph.InputColor);
        output=Metric("Presented fps",182,PlaybackGraph.PresentColor); generated=Metric("Generated fps",265,PlaybackGraph.GeneratedColor);
        tips.SetToolTip(input,"Real source frames accepted by the renderer per second");
        tips.SetToolTip(output,"Frames submitted for presentation per second");
        tips.SetToolTip(generated,"Interpolated frames submitted per second");
        Add(graph,16,109,324,63,12); tips.SetToolTip(graph,"Last 30 seconds · Dashed line shows target frames per second");
        format=Label("Waiting for video",Muted); Add(format,16,178,324,20,12);
        timing=Label("Video timing unavailable",TextColor); Add(timing,16,200,324,20,12);
        network=Label("Audio and network timing unavailable",Muted); Add(network,16,221,324,20,12);
        Add(videoTime,16,218,324,2,12); Add(audioTime,16,241,324,2,12);
        tips.SetToolTip(videoTime,"Video timing · 0–100 ms"); tips.SetToolTip(audioTime,"Audio queue · 0–100 ms");
        Add(Label("Picture",TextColor),16,255,185,20,14);
        for(int i=0;i<modes.Length;i++) {
            int index=i; var button=MakeButton(Names[i],delegate { Request(Modes[index],-1); });
            button.AccessibleName="Picture: "+Names[i];
            Add(button,16+(i%3)*110,283+(i/3)*35,104,30,13); modes[i]=button;
        }
        Add(Label("Frame generation",TextColor),16,361,220,20,14);
        for(int i=0;i<4;i++) {
            int factor=i+1; var button=MakeButton(i==0 ? "Off":factor+"×",delegate { Request(null,factor); });
            button.AccessibleName=i==0 ? "Frame generation off":"Frame generation "+factor+" times";
            Add(button,16+i*83,388,77,30,13); factors[i]=button;
        }
        pin=MakeButton("Pin stats",delegate { pinned=!pinned; Mark(pin,pinned); pin.Text=pinned ? "Unpin stats":"Pin stats"; });
        pin.AccessibleName="Pin live stats"; Add(pin,16,432,105,29,12);
        var hint=Label("Ctrl+Alt+O   ·   Show or hide",Muted); hint.TextAlign=ContentAlignment.MiddleRight;
        Add(hint,128,432,212,29,12);
        notice=Label("",Color.FromArgb(255,214,149)); notice.AutoEllipsis=true; Add(notice,16,465,324,43,12);
        Sync(mode,multiplier);
    }
    Label Metric(string caption,int x,Color color)
    {
        Add(Label(caption,Muted),x,52,81,18,12);
        var value=Label("—",color); Add(value,x,70,79,30,20); return value;
    }
    static Label Label(string text,Color color)
    {
        return new Label { Text=text,ForeColor=color,TextAlign=ContentAlignment.MiddleLeft,UseMnemonic=false,AutoEllipsis=true };
    }
    static Button MakeButton(string text,Action action)
    {
        var button=new Button { Text=text,FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false,BackColor=Idle,
            ForeColor=TextColor,Cursor=Cursors.Hand,Margin=Padding.Empty,Padding=Padding.Empty };
        button.FlatAppearance.BorderColor=Edge; button.FlatAppearance.BorderSize=1;
        button.Click+=delegate { action(); }; return button;
    }
    void Add(Control control,int x,int y,int w,int h,int fontSize)
    {
        panel.Controls.Add(control); children.Add(control); sizes.Add(fontSize); layout.Add(new Rectangle(x,y,w,h));
    }
    static void Mark(Button b,bool active)
    {
        b.BackColor=active ? Mint : Idle; b.ForeColor=active ? Surface : TextColor;
        b.FlatAppearance.BorderColor=active ? Mint : Edge;
        b.AccessibleDescription=active ? "Selected":"Available";
    }
    void Request(string mode,int factor)
    {
        if(SettingChanged!=null) SettingChanged(mode,factor);
    }
    public void Sync(string mode,int factor)
    {
        selection=mode; multiplier=factor;
        for(int i=0;i<modes.Length;i++) Mark(modes[i],Modes[i]==mode);
        for(int i=0;i<factors.Length;i++) Mark(factors[i],i+1==factor);
    }
    public void SetNotice(string message)
    {
        string next=message ?? ""; if(next==lastNotice) return;
        lastNotice=next; notice.Text=next; tips.SetToolTip(notice,next);
        if(expanded) Position();
    }
    public void Toggle() { if(expanded) Collapse(true); else Open(); }
    public void Open()
    {
        expanded=true; panel.Visible=true; chip.Visible=false; Position();
        if(!Visible) Show();
        Activate();
        int i=Array.IndexOf(Modes,selection); if(i>=0) modes[i].Focus();
        if(!probing) { probing=true; ThreadPool.QueueUserWorkItem(delegate { ProbeCapabilities(); }); }
    }
    void ProbeCapabilities()
    {
        // NGX has process-wide SDK state. A separate windowless process can test adapters
        // while the player continues using its current device and enhancement resources.
        try {
            var start=new ProcessStartInfo(Application.ExecutablePath,"--capabilities --quiet") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,
                WorkingDirectory=Path.GetDirectoryName(Application.ExecutablePath)
            };
            using(var process=Process.Start(start)) {
                var output=process.StandardOutput.ReadToEndAsync();
                if(!process.WaitForExit(30000)) { process.Kill(); return; }
                if(process.ExitCode!=0 || !output.Wait(2000)) return;
                var values=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(output.Result);
                if(IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate {
                    if(IsDisposed) return;
                    for(int i=3;i<modes.Length;i++) {
                        string mode=Modes[i]; object supported,reason;
                        if(values.TryGetValue(mode,out supported) && supported is bool && !(bool)supported) {
                            modes[i].Enabled=false;
                            if(values.TryGetValue(mode+"Reason",out reason)) {
                                modes[i].AccessibleDescription=Convert.ToString(reason); tips.SetToolTip(modes[i],Convert.ToString(reason));
                            }
                        }
                    }
                });
            }
        } catch(InvalidOperationException) { }
          catch(IOException) { }
          catch(ArgumentException) { }
          catch(System.ComponentModel.Win32Exception) { }
          catch(AggregateException) { }
    }
    void Collapse(bool restoreInput)
    {
        bool hadFocus=IsHandleCreated && GetForegroundWindow()==Handle;
        expanded=false; chip.Visible=true; panel.Visible=false; chipUntil=0;
        Hide();
        if(restoreInput && hadFocus) WindowsApi.SetForegroundWindow(source==IntPtr.Zero ? player.Handle:source);
        nextPlacement=0;
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if(e.CloseReason==CloseReason.UserClosing && !player.Ended) { e.Cancel=true; Collapse(true); }
        base.OnFormClosing(e);
    }
    protected override bool ProcessCmdKey(ref Message message,Keys key)
    {
        if(key==Keys.Escape) { Collapse(true); return true; }
        return base.ProcessCmdKey(ref message,key);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get { var cp=base.CreateParams; cp.ExStyle|=0x80; return cp; }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e); int rounded=2; DwmSetWindowAttribute(Handle,33,ref rounded,4);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using(var pen=new Pen(Edge)) e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);
    }
    void Position()
    {
        var monitor=Screen.FromHandle(player.Handle).Bounds;
        int dpi=(int)GetDpiForWindow(player.Handle); if(dpi<72) dpi=96;
        int logicalHeight=expanded ? lastNotice.Length>0 ? 515 : 475 : pinned ? 86 : 36;
        int logicalWidth=expanded ? 356 : pinned ? 255 : 222;
        dpi=Math.Max(48,Math.Min(dpi,Math.Min(96*(monitor.Width-4)/logicalWidth,96*(monitor.Height-4)/logicalHeight)));
        float scale=dpi/96f;
        if(dpi!=lastDpi) {
            lastDpi=dpi;
            var old=new List<Font>(fonts.Values); fonts.Clear();
            foreach(int size in new [] {12,13,14,20}) fonts[size]=new Font("Segoe UI",size*scale,size==14 || size==20 ? FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);
            chip.Font=fonts[13]; miniGraph.Font=fonts[12];
            for(int i=0;i<children.Count;i++) {
                Rectangle r=layout[i]; children[i].Bounds=Rectangle.Round(new RectangleF(r.X*scale,r.Y*scale,r.Width*scale,r.Height*scale));
                children[i].Font=fonts[sizes[i]];
            }
            foreach(var font in old) font.Dispose();
        }
        int width=(int)Math.Round(logicalWidth*scale),height=(int)Math.Round(logicalHeight*scale);
        Rectangle ownerRect=player.RectangleToScreen(player.ClientRectangle);
        int gap=(int)Math.Round(14*scale);
        int x=Math.Max(monitor.Left,Math.Min(monitor.Right-width,ownerRect.Right-width-gap));
        int y=Math.Max(monitor.Top,Math.Min(monitor.Bottom-height,ownerRect.Top+gap));
        Rectangle bounds=new Rectangle(x,y,width,height); if(Bounds!=bounds) Bounds=bounds;
        var inside=new Rectangle(1,1,width-2,height-2);
        if(panel.Bounds!=inside) panel.Bounds=inside;
        var chipBounds=new Rectangle(1,1,width-2,pinned ? (int)(35*scale) : height-2);
        if(chip.Bounds!=chipBounds) chip.Bounds=chipBounds;
        miniGraph.Bounds=new Rectangle(3,chipBounds.Bottom,width-6,Math.Max(1,height-chipBounds.Bottom-3));
        panel.Visible=expanded; chip.Visible=!expanded; miniGraph.Visible=!expanded && pinned;
    }
    static string Ms(double value) { return value<0 || double.IsNaN(value) ? "—":value.ToString("0.0")+" ms"; }
    public void UpdateLive(double now,Renderer renderer,bool compare,double lastAge,DirectVideo direct)
    {
        if(now-lastSample>=1000) {
            double interval=(now-lastSample)/1000;
            float inRate=(float)((renderer.Captured-captured)/interval),outRate=(float)((renderer.Presented-presented)/interval),
                  genRate=(float)((renderer.Generated-created)/interval);
            string a=inRate.ToString("0"),b=outRate.ToString("0"),c=genRate.ToString("0");
            captured=renderer.Captured; presented=renderer.Presented; created=renderer.Generated; lastSample=now;
            if(displayName!=renderer.DisplayName) {
                displayName=renderer.DisplayName;
                foreach(var screen in DisplayTarget.Enumerate()) if(screen.DeviceName==displayName) { refreshHz=screen.RefreshHz; break; }
            }
            int desired=targetFps*(compare ? 1:multiplier);
            target.Text=desired>0 ? desired.ToString():"—";
            tips.SetToolTip(target,desired>0 ? targetFps+" fps input × "+(compare ? 1:multiplier)+"; display "+refreshHz.ToString("0.##")+" Hz" : "Source did not provide a target frame rate");
            graph.Sample(inRate,outRate,genRate,desired); miniGraph.Sample(inRate,outRate,genRate,desired);
            input.Text=a; output.Text=b; generated.Text=c;
            compactRates=a+" in   ·   "+b+" out   ·   "+c+" gen";
            var size=renderer.RenderSize; string effective=compare ? "original":renderer.EffectivePictureMode;
            int index=Array.IndexOf(Modes,effective);
            format.Text=string.Format("{0}×{1}  ·  {2}  ·  {3}  ·  {4:0.#} Hz",size.Width,size.Height,renderer.HdrEnabled ? "HDR10":"SDR",
                                      compare ? "Original comparison":index>=0 ? Names[index]:effective,refreshHz);
            var src=renderer.InputSize;
            string detail=string.Format("Captured {0}×{1}  ·  Video output {2}×{3}",src.Width,src.Height,size.Width,size.Height);
            if(direct!=null) detail+="  ·  "+direct.CodecName+"  ·  "+direct.BitrateMbps.ToString("0.0")+" Mb/s";
            tips.SetToolTip(format,detail);
            timing.Text=(direct==null ? "Capture → submit  ":"Receive → submit  ")+Ms(lastAge)+(direct==null ? "" : "     Decode  "+Ms(direct.DecodeMs));
            network.Text=direct==null ? "Audio queue and handshake RTT unavailable" : "Audio queue  "+Ms(direct.AudioQueueMs)+"     Handshake RTT  "+Ms(direct.NetworkRttMs);
            videoTime.Sample(lastAge); audioTime.Sample(direct==null ? -1 : direct.AudioQueueMs);
            if(direct!=null) tips.SetToolTip(network,"Audio target  "+Ms(direct.AudioTargetMs)+"  ·  "+direct.AudioUnderruns+" underruns");
        }
        if(now<nextPlacement) return;
        nextPlacement=now+120;
        if(player.WindowState==FormWindowState.Minimized || !player.Visible) { if(Visible) Hide(); return; }
        IntPtr foreground=GetForegroundWindow();
        bool active=foreground==source || foreground==player.Handle || (IsHandleCreated && foreground==Handle);
        var rect=player.RectangleToScreen(player.ClientRectangle);
        var cursor=Cursor.Position; float dpi=Math.Max(96u,GetDpiForWindow(player.Handle))/96f;
        bool hover=rect.Contains(cursor) && cursor.X>=rect.Right-(int)(252*dpi) && cursor.Y<=rect.Top+(int)(58*dpi);
        if(!expanded && (!active || (!pinned && !hover && now>chipUntil))) { if(Visible) Hide(); return; }
        if(expanded && !active && player.Fullscreen) { if(Visible) Hide(); return; }
        chip.Text=pinned && compactRates.Length>0 ? compactRates : "Playback controls    Ctrl+Alt+O";
        Position(); if(!Visible) Show();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing) { tips.Dispose(); foreach(var f in fonts.Values) f.Dispose(); fonts.Clear(); }
        base.Dispose(disposing);
    }
}
