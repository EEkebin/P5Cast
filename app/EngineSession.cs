using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Cadrivect
{
    internal sealed class EngineSession
    {
        readonly Func<string[],int> run;
        internal readonly Action Stop, InvalidateDisplay;
        internal readonly Action<int,int> Configure;
        internal readonly Action<string> SetComparison;
        internal readonly Action<float> SetWipePosition;
        internal readonly Action<bool> SetWipeHorizontal;
        internal readonly Action<int,float> SetWipeGrip;
        internal readonly Action<string,string> ConfigureOutput;
        internal readonly Func<string> Snapshot, Error;
        static T Method<T>(Type engine,string name) where T:class
        {
            return (T)(object)Delegate.CreateDelegate(typeof(T),engine.GetMethod(name,BindingFlags.Public|BindingFlags.Static));
        }
        internal EngineSession()
        {
            var assembly=Assembly.LoadFrom(Path.Combine(Program.Root,"engine","bin","CadrivectPlayer.exe"));
            var engine=assembly.GetType("Cadrivect.Engine.EmbeddedPlayback",true);
            run=Method<Func<string[],int>>(engine,"Run"); Stop=Method<Action>(engine,"Stop");
            Configure=Method<Action<int,int>>(engine,"Configure"); SetComparison=Method<Action<string>>(engine,"SetComparison");
            SetWipeHorizontal=Method<Action<bool>>(engine,"SetWipeHorizontal");
            SetWipeGrip=Method<Action<int,float>>(engine,"SetWipeGrip");
            SetWipePosition=Method<Action<float>>(engine,"SetWipePosition");
            ConfigureOutput=Method<Action<string,string>>(engine,"ConfigureOutput");
            Snapshot=Method<Func<string>>(engine,"Snapshot"); Error=Method<Func<string>>(engine,"get_Error");
            InvalidateDisplay=Method<Action>(engine,"InvalidateDisplay");
        }
        internal Task<int> Start(string[] args)
        {
            var completion=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker=new Thread(delegate() {
                try {completion.SetResult(run(args));} catch(Exception ex) {completion.SetException(ex);}
            });
            worker.Name="P5Cast video"; worker.IsBackground=true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
            return completion.Task;
        }
    }
}
