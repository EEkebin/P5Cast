using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

internal static partial class WindowChromeProbe
{
    static T SettingsDescendant<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T) return (T)root;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);++i) {
            var match=SettingsDescendant<T>(VisualTreeHelper.GetChild(root,i));
            if(match!=null) return match;
        }
        return null;
    }

    static ScrollBar SettingsBar(ScrollViewer viewer)
    {
        Require(viewer!=null, "Missing scroll viewer.");
        viewer.ApplyTemplate();
        var bar=(ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar",viewer);
        Require(bar!=null,"Missing vertical scroll part.");
        bar.ApplyTemplate();
        var track=(Track)bar.Template.FindName("PART_Track",bar);
        Require(track!=null && track.Thumb!=null,"Missing scroll thumb.");
        track.Thumb.ApplyTemplate();
        Require(track.Thumb.Template.FindName("Pill",track.Thumb)!=null,"Viewer did not inherit the rounded scrollbar.");
        return bar;
    }

    static void SettingsKey(FrameworkElement element,Key key)
    {
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(element),Environment.TickCount,key) {
            RoutedEvent=Keyboard.KeyDownEvent
        });
    }

    static async Task CheckSettingsControls()
    {
        Click("DisplayGroup");
        var graphics=Find<Button>("DisplayGroup");
        Require(Find<ComboBox>("VideoRenderer").IsVisible &&
                System.Windows.Automation.AutomationProperties.GetItemStatus(graphics)=="Selected",
                "Graphics & display did not expose the graphics API selector.");
        var selector=Find<ComboBox>("RenderResolution");
        int selected=selector.SelectedIndex;
        double dropHeight=selector.MaxDropDownHeight;
        try {
            selector.Focus();
            SettingsKey(selector,selected==selector.Items.Count-1?Key.Up:Key.Down);
            await Until(delegate{return selector.SelectedIndex!=selected;},"Arrow keys did not select a different resolution.");
            selector.MaxDropDownHeight=58;
            SettingsKey(selector,Key.F4);
            await Until(delegate{return selector.IsDropDownOpen;},"F4 did not open the selector.");
            var popup=(Popup)selector.Template.FindName("PART_Popup",selector);
            var menu=SettingsDescendant<ScrollViewer>(popup.Child);
            menu.UpdateLayout();
            Require(SettingsBar(menu).IsVisible,"A short dropdown did not expose a scrollbar.");
            SettingsKey(selector,Key.Escape);
            await Until(delegate{return !selector.IsDropDownOpen;},"Escape did not close the selector.");
        } finally {
            selector.IsDropDownOpen=false;
            selector.MaxDropDownHeight=dropHeight;
            selector.SelectedIndex=selected;
        }

        DependencyObject ancestor=Find<Grid>("Pages");
        while(ancestor!=null && !(ancestor is ScrollViewer)) ancestor=VisualTreeHelper.GetParent(ancestor);
        var settings=(ScrollViewer)ancestor;
        Require(settings!=null,"Missing settings scroll viewer.");
        double originalHeight=settings.MaxHeight;
        try {
            settings.MaxHeight=70; settings.ScrollToTop(); window.UpdateLayout();
            await Until(delegate{return settings.ScrollableHeight>15;},"Settings content did not become scrollable.");
            var bar=SettingsBar(settings);
            var track=(Track)bar.Template.FindName("PART_Track",bar);
            settings.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120) {RoutedEvent=Mouse.MouseWheelEvent});
            await Until(delegate{return settings.VerticalOffset>0;},"The mouse wheel did not scroll the settings.");
            settings.ScrollToTop();
            await Until(delegate{return settings.VerticalOffset==0;},"Settings did not scroll back to the top.");
            var page=(RoutedCommand)track.IncreaseRepeatButton.Command;
            page.Execute(null,track.IncreaseRepeatButton);
            await Until(delegate{return settings.VerticalOffset>0;},"The scrollbar page target did not scroll the settings.");
        } finally {settings.MaxHeight=originalHeight; settings.ScrollToTop(); window.UpdateLayout();}

        var sidebar=SettingsDescendant<ScrollViewer>(Find<Border>("MeterHost"));
        Require(sidebar!=null,"Missing sidebar scroll viewer.");
        double oldHeight=sidebar.MaxHeight;
        try {
            sidebar.MaxHeight=85; window.UpdateLayout();
            Require(SettingsBar(sidebar).IsVisible && sidebar.ScrollableHeight>0,"The sidebar did not display the rounded scrollbar.");
        } finally {sidebar.MaxHeight=oldHeight; sidebar.ScrollToTop(); window.UpdateLayout();}
        Note("Settings navigation and dropdown keyboard controls passed. Settings, popup and sidebar use the same scrollbar. Wheel and page clicks scrolled the tray.");
    }
}
