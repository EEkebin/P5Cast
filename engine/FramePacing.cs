using System;

// One input pair produces intermediate phases followed by phase 1 (the real frame).
internal struct FramePacing
{
    int next, count;
    double due, interval;

    internal bool Pending { get {return next>0;} }
    internal void Reset() {next=0;}

    internal void Begin(double received,double lastDrawStart,double sourcePeriod,int frames)
    {
        next=1; count=frames; interval=sourcePeriod/frames;
        // Time spent rendering the previous submission already counts toward the interval.
        due=frames==1 ? received : Math.Max(received,lastDrawStart+interval);
    }

    internal bool TryTake(double now,out float phase)
    {
        phase=0;
        if(!Pending || now<due) return false;
        // Skip only slots whose entire interval has passed since their scheduled deadline.
        int skipped=(int)Math.Min(count-next,(now-due)/interval);
        int step=next+skipped;
        phase=(float)step/count;
        due+=(skipped+1)*interval;
        next=step==count ? 0 : step+1;
        return true;
    }
}
