[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\engine\GameCadence.cs') -Raw
Add-Type -TypeDefinition ($source + @'

internal sealed class CadenceFrames : IDisposable
{
    readonly int bits,stride;
    readonly byte[] pixels;
    readonly GCHandle pin;
    const int W=96,H=64;
    public CadenceFrames(int depth) {
        bits=depth;stride=W*(depth==10?2:1)+16;
        pixels=new byte[H*stride];pin=GCHandle.Alloc(pixels,GCHandleType.Pinned);
    }
    internal void Submit(GameCadence result,int scene,int sequence,double time,bool hudOnly=false,bool bottomUp=false) {
        uint state=(uint)(17+sequence);
        for(int y=0;y<H;y++) for(int x=0;x<W;x++) {
            // Fast camera pan, with independently changing compression residuals
            // on every decoded picture even when the game image repeats.
            int value=32+(x*3+y*2+(!hudOnly || (x<16 && y<16)?scene*12:0))%176;
            state=unchecked(state*1664525u+1013904223u);
            value+=(int)((state>>24)%5)-2;
            if(bits==8) pixels[y*stride+x]=(byte)value;
            else {
                value=(value<<2)+(int)(state&3);
                pixels[y*stride+x*2]=(byte)value;pixels[y*stride+x*2+1]=(byte)(value>>8);
            }
        }
        var origin=pin.AddrOfPinnedObject();
        if(bottomUp) origin=IntPtr.Add(origin,(H-1)*stride);
        result.Observe(origin,bottomUp?-stride:stride,W,H,bits,time);
    }
    public void Dispose() {pin.Free();}
}
public static class CadenceProbe
{
    static void Near(double actual,double expected,string label) {
        if(Math.Abs(actual-expected)>1) throw new Exception(label+" expected "+expected+", got "+actual);
    }
    static void Unknown(double actual,string label) {
        if(actual!=-1) throw new Exception(label+" expected unknown, got "+actual);
    }
    public static void Verify() {
        foreach(int bits in new[]{8,10}) using(var frames=new CadenceFrames(bits)) {
            foreach(int game in new[]{30,40,60}) {
                var estimate=new GameCadence();double t=0;
                for(int i=0;i<=180;i++) {
                    t=i*1000.0/60+(i%4==1?4:0);
                    frames.Submit(estimate,i*game/60,i,t);
                }
                Near(estimate.GameFps(t),game,bits+"-bit moving scene");
                Unknown(estimate.GameFps(t+500),"stale game");
                frames.Submit(estimate,200,181,t+550);
                Unknown(estimate.GameFps(t+550),"gap reset");
            }
            foreach(bool hudOnly in new[]{false,true}) {
                var estimate=new GameCadence();
                for(int i=0;i<=120;i++) frames.Submit(estimate,hudOnly?i:0,i,i*1000.0/60,hudOnly);
                Unknown(estimate.GameFps(2000),"static/noise or tiny HUD motion");
            }
            var stopped=new GameCadence();
            for(int i=0;i<=72;i++) frames.Submit(stopped,Math.Min(i,60),i,i*1000.0/30);
            Unknown(stopped.GameFps(2400),"stopped camera");
            stopped.Reset();Unknown(stopped.GameFps(2400),"decoder discontinuity");
            for(int i=0;i<=120;i++) frames.Submit(stopped,i/2,i,i*1000.0/60,false,true);
            Near(stopped.GameFps(2000),30,"negative luma stride");
            GameCadence enabled=null;
            GameCadence.SetEnabled(ref enabled,false);
            if(enabled!=null) throw new Exception("Disabled option allocated a sampler");
            GameCadence.SetEnabled(ref enabled,true);
            for(int i=0;i<=90;i++) frames.Submit(enabled,i,i,i*1000.0/60);
            Near(enabled.GameFps(1500),60,"initially enabled");
            var retired=enabled;
            GameCadence.SetEnabled(ref enabled,true);
            if(!Object.ReferenceEquals(enabled,retired)) throw new Exception("Repeated enable reset the live sampler");
            GameCadence.SetEnabled(ref enabled,false);
            if(enabled!=null) throw new Exception("Disable retained the sampler");
            GameCadence.SetEnabled(ref enabled,true);
            frames.Submit(retired,91,91,1517); // A decode that started before the toggle finishes late.
            Unknown(enabled.GameFps(1517),"old estimate returned after re-enable");
            for(int i=0;i<=120;i++) frames.Submit(enabled,i/2,i,1600+i*1000.0/60);
            Near(enabled.GameFps(3600),30,"new estimate after re-enable");
        }
    }
}
'@)
[CadenceProbe]::Verify()
Write-Output 'Verified 30/40/60 game FPS, 8/10-bit padded luma, noise, menus, HUD-only changes, stalls, opt-in sampling and fresh estimates after live toggles.'
