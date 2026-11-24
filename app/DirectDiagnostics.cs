using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Cadrivect
{
    // Keep Direct timing and counters off the rendering and audio threads.
    internal sealed class DirectDiagnostics
    {
        static readonly string[] columns=("sampleId elapsedMs targetFps captured presented generated decodedFrames replacedFrames decoderCorruptUnits " +
            "sourceFps submittedFps decodeMs lastPcSubmitMs requestedVideoKbps bitrateMbps videoFramesLost slowVideoHandoffs " +
            "audioQueueMs audioTargetMs audioUnderruns audioConcealments audioQueueTrims audioFrameGapMaxMs audioCallbackGapMaxMs").Split(' ');
        readonly BlockingCollection<string> samples=new BlockingCollection<string>(32);
        readonly string directory;
        double lastId=-1;
        volatile bool failed;

        internal DirectDiagnostics(string directory)
        {
            this.directory=directory;
            new Thread(Write) {Name="P5Cast Direct diagnostics",IsBackground=true}.Start();
        }

        internal void Record(Dictionary<string,object> snapshot)
        {
            double id=PlaybackMeter.Number(snapshot,"sampleId");
            if(failed || !(id>lastId)) return;
            lastId=id;
            var fields=new string[columns.Length+1];
            object rawUtc;
            DateTime sampleUtc;
            if(!snapshot.TryGetValue("sampleUtc",out rawUtc) ||
                !DateTime.TryParseExact(rawUtc as string,"o",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out sampleUtc) ||
                sampleUtc.Kind!=DateTimeKind.Utc) sampleUtc=DateTime.UtcNow;
            fields[0]=sampleUtc.ToString("o",CultureInfo.InvariantCulture);
            for(int i=0;i<columns.Length;i++) {
                double value=PlaybackMeter.Number(snapshot,columns[i]);
                if(!double.IsNaN(value)) fields[i+1]=value.ToString("R",CultureInfo.InvariantCulture);
            }
            samples.TryAdd(string.Join(",",fields));
        }

        internal void Finish() {samples.CompleteAdding();}

        static bool IsTraceFile(string path)
        {
            string[] parts=Path.GetFileNameWithoutExtension(path).Split('-');
            Guid id;
            DateTime created;
            return parts.Length==3 && parts[0]=="direct" &&
                DateTime.TryParseExact(parts[1],"yyyyMMdd'T'HHmmssfff",CultureInfo.InvariantCulture,DateTimeStyles.None,out created) &&
                Guid.TryParseExact(parts[2],"N",out id);
        }

        void Write()
        {
            StreamWriter file=null;
            int rows=0;
            try {
                foreach(string row in samples.GetConsumingEnumerable()) {
                    if(rows%3600==0) {
                        if(file!=null) file.Dispose();
                        Directory.CreateDirectory(directory);
                        var name="direct-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".csv";
                        file=new StreamWriter(new FileStream(Path.Combine(directory,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read));
                        file.AutoFlush=true;
                        file.WriteLine("utc,"+string.Join(",",columns));
                        foreach(string old in Directory.GetFiles(directory,"direct-????????T?????????-????????????????????????????????.csv")
                            .Where(IsTraceFile).OrderByDescending(p=>p,StringComparer.Ordinal).Skip(4)) {
                            try {File.Delete(old);} catch(IOException) {} catch(UnauthorizedAccessException) {}
                        }
                    }
                    file.WriteLine(row); rows++;
                }
            } catch(IOException) {failed=true;} catch(UnauthorizedAccessException) {failed=true;}
            finally {
                if(file!=null) try {file.Dispose();} catch(IOException) {}
            }
        }
    }
}
