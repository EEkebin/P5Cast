using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        long nextOperation,activeOperation;
        Action operationCancel;
        UIElement operationReturnFocus;
        bool OperationOpen { get { return activeOperation!=0; } }
        bool HistoryOpen { get { return UI<Grid>("StatusHistoryLayer").Visibility==Visibility.Visible; } }

        async Task<Dictionary<string,object>> PrepareComponent(CancellationToken token,string script,params string[] args)
        {
            // Package installation can finish publishing verified files independently.
            // Cancelling the waiter prevents the subsequent host or player launch.
            var setup=Script(script,args);
            var cancelled=new TaskCompletionSource<bool>();
            using(token.Register(()=>cancelled.TrySetResult(true))) {
                if(await Task.WhenAny(setup,cancelled.Task)!=setup) ObservePreparation(setup);
            }
            token.ThrowIfCancellationRequested();
            return await setup;
        }
        static void ObservePreparation(Task setup)
        {
            setup.ContinueWith(t=>{var ignored=t.Exception;},CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }

        async Task ChangeSharing(bool start,params string[] args)
        {
            if(OperationOpen) return;
            using(var cancellation=new CancellationTokenSource()) {
                long id=BeginOperation(start?"Starting sharing":"Stopping sharing",
                    start?"Preparing the host software…":"The host is still sharing. Cancel to keep it running.",cancellation.Cancel);
                try {
                    if(start) await PrepareComponent(cancellation.Token,"streaming/Relay.ps1","-Action","InstallHost");
                    else await Task.Delay(1000,cancellation.Token);
                    if(closing) return;
                    CommitOperation(id,start?"Waiting for the sharing host to become ready…":"Shutting down the sharing host…");
                    await Relay(start?"StartHost":"StopHost",args);
                } catch(OperationCanceledException) {
                    Show(start?"Host startup cancelled.":"Host shutdown cancelled.");
                } finally {EndOperation(id);}
            }
        }

        long BeginOperation(string title,string detail,Action cancel)
        {
            bool opening=!OperationOpen;
            if(opening) operationReturnFocus=Keyboard.FocusedElement as UIElement;
            activeOperation=++nextOperation;
            operationCancel=cancel;
            UI<TextBlock>("OperationTitle").Text=title;
            UI<TextBlock>("OperationDetail").Text=detail;
            var action=UI<Button>("CancelOperation");
            action.IsEnabled=cancel!=null;
            UI<Grid>("OperationOverlay").Visibility=Visibility.Visible;
            UpdateTransport();
            if(opening && SystemParameters.ClientAreaAnimation)
                UI<RotateTransform>("OperationRotation").BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0,360,TimeSpan.FromMilliseconds(900)) {RepeatBehavior=RepeatBehavior.Forever});
            return activeOperation;
        }

        void UpdateOperation(long id,string detail)
        {
            if(id!=0 && id==activeOperation) UI<TextBlock>("OperationDetail").Text=detail;
        }

        void CommitOperation(long id,string detail)
        {
            if(id==0 || id!=activeOperation) return;
            operationCancel=null;
            UpdateOperation(id,detail);
            UI<Button>("CancelOperation").IsEnabled=false;
        }

        void CancelCurrentOperation()
        {
            var cancel=operationCancel;
            if(cancel==null || !OperationOpen) return;
            operationCancel=null;
            UI<Button>("CancelOperation").IsEnabled=false;
            cancel();
        }

        void EndOperation(long id)
        {
            if(id==0 || id!=activeOperation) return;
            bool hadFocus=UI<Grid>("OperationOverlay").IsKeyboardFocusWithin;
            activeOperation=0;
            operationCancel=null;
            UI<Grid>("OperationOverlay").Visibility=Visibility.Collapsed;
            UI<RotateTransform>("OperationRotation").BeginAnimation(RotateTransform.AngleProperty,null);
            surface.ClipForProgress(null);
            UpdateTransport();
            var previous=operationReturnFocus;
            operationReturnFocus=null;
            if(!hadFocus || closing || !window.IsVisible || HistoryOpen || StopPromptOpen || SetupOpen) return;
            if(previous!=null && previous.IsVisible && previous.IsEnabled && previous.Focus()) return;
            if(!SetupOpen) UI<Button>("OpenSource").Focus();
        }
    }
}
