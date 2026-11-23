using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        DiscordPresence discord;
        string titleAddress,detectedTitle="",publishedTitle;
        bool publishedButton,lookingUp;
        int lookupGeneration;
        DateTime nextLookup,applyTitleAfter;

        void InitDiscord(Dictionary<string,object> saved)
        {
            var enabled=UI<CheckBox>("SharePresence");
            var includeGame=UI<CheckBox>("ShareGame");
            var includeProject=UI<CheckBox>("ShareProject");
            enabled.IsChecked=Field(saved,"discordPresence")=="True";
            includeGame.IsChecked=Field(saved,"discordGame")!="False";
            includeProject.IsChecked=Field(saved,"discordProject")=="True";
            foreach(var option in new[]{enabled,includeGame,includeProject}) option.Click+=delegate {
                ResetTitleLookup(); TickDiscord(); SaveSettings();
            };
            UI<TextBox>("GameTitle").TextChanged+=delegate {applyTitleAfter=DateTime.UtcNow.AddMilliseconds(700);};
            TickDiscord();
        }
        void ResetTitleLookup()
        {
            lookupGeneration++; lookingUp=false; detectedTitle=""; nextLookup=DateTime.MinValue;
        }
        void EndDiscordSession()
        {
            ResetTitleLookup(); titleAddress=null; firstFrame=false;
            UI<TextBox>("GameTitle").Clear();
            TickDiscord();
        }
        async void ReadDiscordTitle(string address,int generation)
        {
            lookingUp=true;
            try {
                string name=await DirectConsole.ReadGameTitleAsync(address);
                if(!closing && generation==lookupGeneration) detectedTitle=name??"";
            } catch(Exception) {
                // Title discovery is optional and cannot interrupt playback or presence.
                if(generation==lookupGeneration) detectedTitle="";
            } finally {
                if(generation==lookupGeneration) {lookingUp=false; nextLookup=DateTime.UtcNow.AddSeconds(30);}
            }
        }
        void TickDiscord()
        {
            bool enabled=UI<CheckBox>("SharePresence").IsChecked==true;
            bool game=UI<CheckBox>("ShareGame").IsChecked==true;
            UI<CheckBox>("ShareGame").IsEnabled=enabled;
            UI<CheckBox>("ShareProject").IsEnabled=enabled;
            var manual=UI<TextBox>("GameTitle"); manual.IsEnabled=enabled && game;
            string state;
            bool button=UI<CheckBox>("ShareProject").IsChecked==true;
            if(!enabled || closing) {
                if(discord!=null) discord.Clear();
                publishedTitle=null;
                state="Rich Presence is off.";
            } else if(stopping || !firstFrame || rendering==null || (source!="Direct" && source!="Sony")) {
                if(discord==null) discord=new DiscordPresence();
                discord.SetIdle(button); publishedTitle=null;
                state=discord.Status;
            } else {
                string title=game?manual.Text.Trim():"";
                if(game && title.Length==0) {
                    if(source=="Direct" && titleAddress!=null && !lookingUp && DateTime.UtcNow>=nextLookup)
                        ReadDiscordTitle(titleAddress,lookupGeneration);
                    title=detectedTitle;
                }
                if((publishedTitle!=title || publishedButton!=button) && (title.Length==0 || DateTime.UtcNow>=applyTitleAfter)) {
                    if(discord==null) discord=new DiscordPresence();
                    discord.SetActivity(title,button); publishedTitle=title; publishedButton=button;
                }
                state=discord==null?"Waiting for Discord…":discord.Status;
            }
            var status=UI<TextBlock>("DiscordState");
            if(status.Text!=state) status.Text=state;
        }
    }
}
