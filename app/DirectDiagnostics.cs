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
            "audioQueueMs audioTargetMs audioUnderruns audioConcealments audioQueueTrims audioFrameGapMaxMs audioCallbackGapMaxMs " +
            "multiplier targetPresentedFps generatedFps displayRefreshHz inputWidth inputHeight outputWidth outputHeight " +
            "displayedWidth displayedHeight nvidiaEvaluations openNrEvaluations nisEvaluations fsrEvaluations frucEvaluations frucMidpoints frucPresented frucPresentedFps streamFps estimatedGameFps proxyGenerated proxyGeneratedFps").Split(' ');
        // Only renderer identifiers belong in a trace. Unknown values and free-form messages stay out.
        static readonly string[] providers={"p5cast","amd","fruc","dlss-fg","xess-fg"};
        static readonly KeyValuePair<string,string[]>[] identifiers={
            new KeyValuePair<string,string[]>("mode",PictureLabels.Ids),
            new KeyValuePair<string,string[]>("effectivePictureMode",PictureLabels.Ids),
            new KeyValuePair<string,string[]>("renderer",new[]{"d3d11","d3d12","vulkan","opengl","metal"}),
            new KeyValuePair<string,string[]>("comparisonView",new[]{"original","enhanced","side-by-side","wipe","hdr-sdr"}),
            new KeyValuePair<string,string[]>("frameGenerationProvider",providers),
            new KeyValuePair<string,string[]>("activeFrameGenerationProvider",providers)
        };
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
            var fields=new string[2+columns.Length+identifiers.Length];
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
            for(int i=0;i<identifiers.Length;i++) {
                var field=identifiers[i];
                object raw;
                if(snapshot.TryGetValue(field.Key,out raw) && Array.IndexOf(field.Value,raw as string)>=0)
                    fields[columns.Length+1+i]=(string)raw;
            }
            object hdr;
            if(snapshot.TryGetValue("hdrEnabled",out hdr) && hdr is bool)
                fields[fields.Length-1]=(bool)hdr ? "1" : "0";
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
                        file.WriteLine("utc,"+string.Join(",",columns.Concat(identifiers.Select(field=>field.Key)))+",hdrEnabled");
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
