using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        const double SidebarDefault=312, SidebarMinimum=248, SidebarMaximum=640;
        const double SettingsDefault=220, SettingsMinimum=112, SettingsMaximum=640;
        const double PlaybackMinimumWidth=472, PlaybackMinimumHeight=180;
        double sidebarWidth,settingsHeight;
        bool settingsOpen,updatingSettingsNavigation;
        readonly string[] settingsModes={"Play","Host","Client"};
        int sidebarMotion,settingsMotion;

        static double ReadDrawerSize(Dictionary<string,object> saved,string key,double fallback,double minimum,double maximum)
        {
            object value;
            double size;
            if(!saved.TryGetValue(key,out value) ||
                !double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out size) ||
                double.IsNaN(size) || double.IsInfinity(size)) return fallback;
            return Math.Max(minimum,Math.Min(maximum,size));
        }

        void InitDrawers(Dictionary<string,object> saved)
        {
            sidebarWidth=ReadDrawerSize(saved,"sidebarWidth",SidebarDefault,SidebarMinimum,SidebarMaximum);
            settingsHeight=ReadDrawerSize(saved,"settingsHeight",SettingsDefault,SettingsMinimum,SettingsMaximum);
            settingsOpen=!Field(saved,"settingsOpen").Equals("False",StringComparison.OrdinalIgnoreCase);
            var toggle=UI<ToggleButton>("ShowSettings");
            RoutedEventHandler change=delegate {SetSettingsDrawer(toggle.IsChecked==true);};
            toggle.Checked+=change; toggle.Unchecked+=change;
            BindDrawerGrip(true); BindDrawerGrip(false);
            InitSettingsNavigation();
            UI<Grid>("Workspace").SizeChanged+=delegate {RefreshDrawerSizes();};
            ApplySettingsDrawer();
        }

        void InitSettingsNavigation()
        {
            for(int i=0;i<settingsModes.Length;i++) {
                var group=settingsGroups[i];
                var picker=UI<ComboBox>(settingsModes[i]+"SectionPicker");
                picker.SelectionChanged+=delegate {
                    var choice=picker.SelectedItem as ComboBoxItem;
                    if(!updatingSettingsNavigation && choice!=null) SelectGroup(group,(string)choice.Tag);
                };
                UI<StackPanel>(settingsModes[i]+"Page").IsVisibleChanged+=delegate(object sender,DependencyPropertyChangedEventArgs e) {
                    if((bool)e.NewValue) UI<ScrollViewer>("SettingsContentScroll").ScrollToTop();
                };
            }
            var surface=UI<DockPanel>("SettingsWorkbench");
            surface.SizeChanged+=delegate {
                bool compact=surface.ActualWidth<704 || surface.ActualHeight<172;
                UI<Grid>("CompactSettingsSections").Visibility=compact?Visibility.Visible:Visibility.Collapsed;
                UI<Grid>("SettingsSections").Visibility=compact?Visibility.Collapsed:Visibility.Visible;
                foreach(string mode in settingsModes)
                    UI<TextBlock>(mode+"SettingsHeading").Visibility=compact?Visibility.Collapsed:Visibility.Visible;
            };
            var fields=UI<Grid>("Pages");
            fields.SizeChanged+=delegate {
                int count=Math.Max(1,(int)(fields.ActualWidth/186));
                UI<UniformGrid>("ConsoleFields").Columns=Math.Min(count,2);
                UI<UniformGrid>("HostFields").Columns=Math.Min(count,2);
                UI<UniformGrid>("DisplayFields").Columns=Math.Min(Math.Max(1,(int)(fields.ActualWidth/204)),4);
                foreach(string name in new[]{"StreamFields","PairFields","ClientFields"})
                    UI<UniformGrid>(name).Columns=Math.Min(count,3);
            };
            var transport=UI<DockPanel>("TransportBar");
            var fullscreenButton=UI<Button>("Fullscreen");
            AutomationProperties.SetName(fullscreenButton,"Fullscreen");
            transport.SizeChanged+=delegate {
                // The playback controls remain reachable when the sidebar leaves a narrow viewport.
                bool compact=transport.ActualWidth<525;
                UI<ComboBox>("SourceChoice").Width=compact?122:142;
                UI<TextBlock>("SettingsActionLabel").Visibility=compact?Visibility.Collapsed:Visibility.Visible;
                UI<System.Windows.Shapes.Path>("SettingsActionGlyph").Visibility=compact?Visibility.Visible:Visibility.Collapsed;
            };
        }

        void UpdateSettingsNavigation(string[] group,string selected)
        {
            int index=Array.IndexOf(settingsGroups,group);
            if(index<0) return;
            string mode=settingsModes[index];
            updatingSettingsNavigation=true;
            try {
                var picker=UI<ComboBox>(mode+"SectionPicker");
                var visible=new List<string>();
                foreach(string key in group)
                    if(UI<Button>(key+"Group").Visibility==Visibility.Visible) visible.Add(key);
                // Sony capture and the offline demo omit the Direct-only console and stream pages.
                bool rebuild=visible.Count!=picker.Items.Count;
                for(int i=0;!rebuild && i<visible.Count;i++)
                    rebuild=!Equals(((ComboBoxItem)picker.Items[i]).Tag,visible[i]);
                if(rebuild) {
                    picker.Items.Clear();
                    foreach(string key in visible)
                        picker.Items.Add(new ComboBoxItem {Content=UI<Button>(key+"Group").Content,Tag=key});
                }
                picker.SelectedIndex=visible.IndexOf(selected);
                UI<TextBlock>(mode+"SettingsHeading").Text=Convert.ToString(UI<Button>(selected+"Group").Content);
                UI<ScrollViewer>("SettingsContentScroll").ScrollToTop();
            } finally {updatingSettingsNavigation=false;}
        }

        double DrawerLimit(bool side)
        {
            var workspace=UI<Grid>("Workspace");
            double space=side?workspace.ActualWidth:workspace.ActualHeight;
            double maximum=side?SidebarMaximum:SettingsMaximum;
            if(space<=0) return maximum;
            double reserve=side?PlaybackMinimumWidth:PlaybackMinimumHeight+UI<RowDefinition>("TransportRow").Height.Value;
            return Math.Max(0,Math.Min(maximum,space-reserve));
        }

        double FitDrawer(bool side,double requested)
        {
            return Math.Min(DrawerLimit(side),Math.Max(side?SidebarMinimum:SettingsMinimum,requested));
        }

        void RefreshDrawerSizes()
        {
            UpdateDrawer(true,false,true);
            UpdateDrawer(false,false,true);
        }

        void UpdateDrawer(bool side,bool animate,bool sizeOnly=false)
        {
            var drawer=UI<Border>(side?"SidebarDrawer":"SettingsDrawer");
            var body=(FrameworkElement)drawer.Child;
            var extent=side?FrameworkElement.WidthProperty:FrameworkElement.HeightProperty;
            double fitted=FitDrawer(side,side?sidebarWidth:settingsHeight);
            if(sizeOnly && (double)body.GetValue(extent)==fitted) return;
            bool open=!fullscreen && (side?stats:settingsOpen);
            double target=open?fitted:0;
            double current=drawer.Visibility==Visibility.Visible?(side?drawer.ActualWidth:drawer.ActualHeight):0;
            int revision=side?++sidebarMotion:++settingsMotion;

            // The Auto grid track follows the clipped drawer each frame. Its contents
            // slide in at their final size; the native video gets the remaining space.
            body.SetValue(extent,fitted);
            if(open) drawer.Visibility=Visibility.Visible;
            drawer.IsHitTestVisible=open;
            AutomationProperties.SetItemStatus(UI<Thumb>(side?"SidebarResize":"SettingsResize"),
                Math.Round(target)+(side?" wide":" high"));
            if(animate && window.IsLoaded && !fullscreen && SystemParameters.ClientAreaAnimation && Math.Abs(target-current)>.5) {
                // A layout pass can precede the first animation tick. Its base extent
                // must match the currently visible frame, including during reversal.
                drawer.SetValue(extent,current);
                var motion=Motion(current,target);
                motion.FillBehavior=FillBehavior.HoldEnd;
                motion.Completed+=delegate {
                    if((side?sidebarMotion:settingsMotion)!=revision) return;
                    drawer.SetValue(extent,target);
                    drawer.BeginAnimation(extent,null);
                    if(!open) drawer.Visibility=Visibility.Collapsed;
                };
                drawer.BeginAnimation(extent,motion);
            } else {
                drawer.SetValue(extent,target);
                drawer.BeginAnimation(extent,null);
                if(!open) drawer.Visibility=Visibility.Collapsed;
            }
        }

        void ApplySettingsDrawer(bool animate=false)
        {
            var toggle=UI<ToggleButton>("ShowSettings"); toggle.IsChecked=settingsOpen;
            toggle.ToolTip=settingsOpen?"Hide settings":"Show settings";
            UI<DockPanel>("TransportBar").Visibility=fullscreen?Visibility.Collapsed:Visibility.Visible;
            UpdateDrawer(false,animate);
        }

        void SetSettingsDrawer(bool open)
        {
            if(settingsOpen==open) return;
            settingsOpen=open; ApplySettingsDrawer(true);
            if(window.IsLoaded) SaveSettings();
        }

        void ResizeDrawer(bool side,double size)
        {
            double value=FitDrawer(side,size);
            // A cramped window may temporarily display less than the global minimum.
            // Only the user's preference is saved; normal resizing never overwrites it.
            if(side) sidebarWidth=Math.Max(SidebarMinimum,value);
            else settingsHeight=Math.Max(SettingsMinimum,value);
            RefreshDrawerSizes();
        }

        void BindDrawerGrip(bool side)
        {
            var grip=UI<Thumb>(side?"SidebarResize":"SettingsResize");
            var workspace=UI<Grid>("Workspace");
            double previous=0,initialSize=0;
            Point origin=new Point();
            grip.DragStarted+=delegate {
                previous=side?sidebarWidth:settingsHeight;
                initialSize=FitDrawer(side,previous);
                origin=Mouse.GetPosition(workspace);
                grip.Focus();
            };
            grip.DragDelta+=delegate(object sender,DragDeltaEventArgs e) {
                // The grip itself moves. A fixed workspace coordinate avoids adding
                // the same mouse displacement twice between two WPF layout passes.
                if(grip.IsMouseCaptured) {
                    var delta=Mouse.GetPosition(workspace)-origin;
                    ResizeDrawer(side,initialSize-(side?delta.X:delta.Y));
                } else {
                    double current=FitDrawer(side,side?sidebarWidth:settingsHeight);
                    ResizeDrawer(side,current-(side?e.HorizontalChange:e.VerticalChange));
                }
            };
            grip.DragCompleted+=delegate(object sender,DragCompletedEventArgs e) {
                if(e.Canceled) {
                    if(side) sidebarWidth=previous; else settingsHeight=previous;
                    RefreshDrawerSizes();
                } else if(window.IsLoaded) SaveSettings();
            };
            grip.KeyDown+=delegate(object sender,KeyEventArgs e) {
                double current=FitDrawer(side,side?sidebarWidth:settingsHeight);
                if(e.Key==(side?Key.Left:Key.Up)) ResizeDrawer(side,current+16);
                else if(e.Key==(side?Key.Right:Key.Down)) ResizeDrawer(side,current-16);
                else if(e.Key==Key.Home) ResizeDrawer(side,side?SidebarMinimum:SettingsMinimum);
                else if(e.Key==Key.End) ResizeDrawer(side,DrawerLimit(side));
                else if(e.Key==Key.Enter || e.Key==Key.Space) {
                    var toggle=UI<ToggleButton>(side?"ShowStats":"ShowSettings");
                    toggle.IsChecked=false; toggle.Focus(); e.Handled=true; return;
                } else return;
                e.Handled=true;
                if(window.IsLoaded) SaveSettings();
            };
        }
    }
}
