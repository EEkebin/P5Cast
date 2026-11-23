using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        static readonly string[] themeIds={"mint","ember","daylight","custom"};
        // Each row is a semantic resource, followed by Mint, Ember and Daylight.
        static readonly string[,] themeColors={
            {"Background", "#10171C", "#211A1B", "#F1F4F8"},
            {"Surface",    "#172127", "#302528", "#FFFFFF"},
            {"Panel",      "#111B20", "#271D20", "#E9EEF4"},
            {"Control",    "#243239", "#413035", "#E0E7EF"},
            {"Hover",      "#2C3F45", "#553C42", "#D3DFEB"},
            {"Selected",   "#243D38", "#573631", "#D3E8F5"},
            {"Border",     "#35474E", "#584248", "#BAC8D6"},
            {"StrongBorder","#647D81","#9B777A", "#687E92"},
            {"Text",       "#E8F0F1", "#F9EEE8", "#192A38"},
            {"Muted",      "#B3C1C4", "#D6B9AF", "#485F72"},
            {"Accent",     "#8FE6D0", "#FFB489", "#145E87"},
            {"AccentText", "#102725", "#351D13", "#FFFFFF"},
            {"Source",     "#8FE6D0", "#FFB489", "#145E87"},
            {"Generated",  "#EABC8E", "#E0B4F2", "#864588"},
            {"Track",      "#2B3D43", "#533C40", "#DBE4ED"},
            {"Error",      "#FFBAA8", "#FFACA2", "#A52E38"},
            {"Danger",     "#A63E47", "#AB4147", "#F3B7B9"}
        };
        readonly string themeFile=Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "P5Cast","themes","custom.json");
        ResourceDictionary activeColors;
        string themeId="mint";
        bool syncingTheme;

        void InitTheme(Dictionary<string,object> saved)
        {
            var picker=UI<ComboBox>("ColorTheme");
            picker.ItemsSource=new[]{"Mint","Ember","Daylight","Custom JSON"};
            string selected=Field(saved,"theme");
            int index=Array.IndexOf(themeIds,selected);
            picker.SelectedIndex=index<0?0:index;
            if(picker.SelectedIndex==3) ApplyTheme("mint");
            SelectTheme(themeIds[picker.SelectedIndex],true);
            picker.SelectionChanged+=delegate {if(!syncingTheme && picker.SelectedIndex>=0) SelectTheme(themeIds[picker.SelectedIndex],false);};
            UI<Button>("ReloadTheme").Click+=delegate {SelectTheme("custom",false);};
            UI<Button>("EditThemeJson").Click+=delegate {
                try {
                    EnsureThemeFile();
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"notepad.exe"),Quote(themeFile)){UseShellExecute=false});
                    SelectTheme("custom",false);
                } catch(Exception e) {ThemeMessage(e.Message,true);}
            };
        }
        void ThemeMessage(string message,bool error)
        {
            var status=UI<TextBlock>("ThemeStatus");
            status.Text=message; status.ToolTip=themeFile;
            status.Visibility=message.Length==0 ? Visibility.Collapsed : Visibility.Visible;
            status.Visibility=string.IsNullOrEmpty(message)?Visibility.Collapsed:Visibility.Visible;
            status.SetResourceReference(TextBlock.ForegroundProperty,error?"Theme.Error":"Theme.Muted");
        }
        void SelectTheme(string id,bool startup)
        {
            try {
                if(id=="custom") EnsureThemeFile();
                ApplyTheme(id);
                themeId=id;
                ThemeMessage(id=="custom" ? "Custom colors loaded. Save your edits, then select Reload." : "",false);
                if(!startup && window.IsLoaded) SaveSettings();
            } catch(Exception e) {
                // Keep the last working palette. Preserve the Custom selection on startup for repair.
                if(startup && id=="custom") themeId=id;
                ThemeMessage(e.Message,true);
            }
            var picker=UI<ComboBox>("ColorTheme");
            syncingTheme=true;
            try {picker.SelectedIndex=Array.IndexOf(themeIds,themeId);}
            finally {syncingTheme=false;}
            UI<Button>("ReloadTheme").IsEnabled=themeId=="custom" || File.Exists(themeFile);
        }
        void EnsureThemeFile()
        {
            if(File.Exists(themeFile)) return;
            string dir=Path.GetDirectoryName(themeFile);
            Directory.CreateDirectory(dir);
            int preset=Math.Max(0,Math.Min(2,Array.IndexOf(themeIds,themeId)));
            var rows=new List<string>{"  \"base\": \""+themeIds[preset]+"\""};
            for(int i=0;i<themeColors.GetLength(0);i++) {
                rows.Add("  \""+themeColors[i,0]+"\": \""+themeColors[i,preset+1]+"\"");
            }
            string staging=Path.Combine(dir,Guid.NewGuid().ToString("N")+".tmp");
            try {
                File.WriteAllText(staging,"{"+Environment.NewLine+string.Join(","+Environment.NewLine,rows)+Environment.NewLine+"}"+Environment.NewLine,new UTF8Encoding(false));
                try {File.Move(staging,themeFile);} catch(IOException) {if(!File.Exists(themeFile)) throw;}
            } finally {if(File.Exists(staging)) File.Delete(staging);}
        }
        Dictionary<string,string> ReadCustom(out string preset)
        {
            if(new FileInfo(themeFile).Length>16384) throw new InvalidDataException("custom.json exceeds 16 KB. Use one JSON object with color values.");
            Dictionary<string,object> fields;
            try {fields=new JavaScriptSerializer{MaxJsonLength=16384}.Deserialize<Dictionary<string,object>>(File.ReadAllText(themeFile));}
            catch(ArgumentException){throw new InvalidDataException("Could not read custom.json. Check the braces, commas and quoted color values.");}
            catch(InvalidOperationException){throw new InvalidDataException("custom.json must contain one JSON object.");}
            if(fields==null) throw new InvalidDataException("custom.json must contain one JSON object.");
            preset="mint";
            var colors=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in fields) {
                string color=pair.Value as string;
                if(string.Equals(pair.Key,"base",StringComparison.OrdinalIgnoreCase)) {
                    preset=(color??"").ToLowerInvariant();
                    if(preset!="mint" && preset!="ember" && preset!="daylight") throw new InvalidDataException("Choose mint, ember or daylight for base.");
                    continue;
                }
                bool known=false;
                for(int i=0;i<themeColors.GetLength(0);i++) if(string.Equals(themeColors[i,0],pair.Key,StringComparison.OrdinalIgnoreCase)) {known=true;break;}
                if(!known) throw new InvalidDataException("Unknown theme color “"+pair.Key+"”. Check its name in the theme guide.");
                if(color==null || !Regex.IsMatch(color,@"\A#[0-9a-fA-F]{6}\z")) throw new InvalidDataException("Use a color such as #8FE6D0 for "+pair.Key+".");
                colors[pair.Key]=color;
            }
            return colors;
        }
        void ApplyTheme(string id)
        {
            string baseId=id;
            var colors=id=="custom"?ReadCustom(out baseId):new Dictionary<string,string>();
            int preset=Array.IndexOf(themeIds,baseId)+1;
            var next=new ResourceDictionary();
            for(int i=0;i<themeColors.GetLength(0);i++) {
                string role=themeColors[i,0],value;
                if(!colors.TryGetValue(role,out value)) value=themeColors[i,preset];
                var color=(Color)ColorConverter.ConvertFromString(value);
                var brush=new SolidColorBrush(color);brush.Freeze();
                next["Theme."+role]=brush;
                if(role=="Background") {
                    var scrim=new SolidColorBrush(Color.FromArgb(235,color.R,color.G,color.B));scrim.Freeze();next["Theme.Scrim"]=scrim;
                }
            }
            var merged=window.Resources.MergedDictionaries;
            if(activeColors==null) merged.Add(next); else merged[merged.IndexOf(activeColors)]=next;
            activeColors=next;
            if(meter!=null) meter.RefreshTheme();
            var glyph=UI<Button>("OpenSource").Content as TransportGlyph;
            if(glyph!=null) glyph.InvalidateVisual();
        }
    }
}
