[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\engine\FramePacing.cs') -Raw
Add-Type -TypeDefinition ($source + @'

public static class FramePacingProbe
{
    static void ExpectIdle(FramePacing pacing,double now)
    {
        float phase;
        if(pacing.TryTake(now,out phase))
            throw new Exception("Unexpected phase at "+now+": "+phase);
    }
    static void Take(ref FramePacing pacing,double now,float expected)
    {
        float phase;
        if(!pacing.TryTake(now,out phase) || phase!=expected)
            throw new Exception("Expected phase "+expected+" at "+now+", got "+phase);
    }
    public static void Verify()
    {
        var pacing=new FramePacing();
        // Input arrives at 100 ms. Finishing its predecessor starts at 101 and
        // returns at 107. Its 8-ms slot therefore ends at 109, not 115.
        pacing.Begin(100,101,16,2);
        ExpectIdle(pacing,107);
        Take(ref pacing,109,.5f);
        ExpectIdle(pacing,115);
        Take(ref pacing,117,1);
        ExpectIdle(pacing,200);

        // At 4x, a missed intermediate may be dropped but the real endpoint
        // still has to be submitted if the next input overtakes this pair.
        pacing.Begin(200,180,16,4);
        Take(ref pacing,205,.5f);
        if(!pacing.Pending) throw new Exception("Lost a pending real frame.");
        Take(ref pacing,240,1);
        ExpectIdle(pacing,241);

        pacing.Begin(300,296,16,4);
        Take(ref pacing,300,.25f);
        pacing.Reset();
        ExpectIdle(pacing,310);
        pacing.Begin(311,310,16,1);
        Take(ref pacing,311,1);
    }
}
'@)
[FramePacingProbe]::Verify()
Write-Output 'Verified intermediate deadlines, late-slot skipping, real frames, and reset.'
