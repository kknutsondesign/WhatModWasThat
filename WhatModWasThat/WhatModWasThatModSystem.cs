using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using System.Text.Encodings;
using System.Text;
using System.Net;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Reflection.Emit;

namespace WhatModWasThat
{
    /*** TODO
     * Add folders/tags for mass management
     * Add auto settings for different ModDB tags
     * Hack together some way to do 1-click installs from inside world
     */
    public class WhatModWasThatModSystem : ModSystem
    {
        [Serializable]
        public class OldMod
        {
            public string modID { get; set; }
            public string name { get; set; }
            public string version { get; set; }
            public string website { get; set; }
            public string assetID { get; set; }
            public string latestVersion { get; set; }

            //flags
            public bool newMod { get; set; }
            public bool newVersion { get; set; }
            public bool hasNewVersion { get; set; }
            public bool removed { get; set; }
            public bool hidden {get; set;}

            [NonSerialized]
            public bool _wasChecked = false;
            [NonSerialized]
            public bool _wasRendered = false;

            public override string ToString()
            {
                return String.Format("{0}, {1}, {2}, {3}, {4}, {5}", modID, name, version, newMod, newVersion, removed);
            }

            public OldMod()
            {
                modID = name = version = website = assetID = latestVersion = "";
                newMod = newVersion = hasNewVersion = removed = hidden = false;
            }
        }

        [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
        public class WhatMod_StateMessage {
            public string message; 
        }

        private ICoreServerAPI _api;
        private ICoreClientAPI _capi;

        private IServerNetworkChannel _serverChannel;
        private IClientNetworkChannel _clientChannel;

        public Dictionary<string, OldMod> oldMods;

        private bool _renderNames;
        private bool _renderOnlyChanges;
        private bool _hideRemoved;
        private bool _unhideAdded;
        private bool _showNewVersionAvailable;

        private GuiHandbookTextPage _whatPage;

        private Dictionary<string, string> flags = new Dictionary<string, string>();

        private Dictionary<string, string> defFlags = new Dictionary<string, string>
        {
            ["Updated"] = "1B9AAA",
            ["Added"] = "06D6A0",
            ["Default"] = "F8FFE5",
            ["NeedsUpdate"] = "FFC43D",
            ["Removed"] = "EF478F"
        };
        public override void StartClientSide(ICoreClientAPI api) //TODO: Send and Recieve clipboard packet? Then handle with capi.Forms.SetClipboard
        {
            _capi = api;
            _clientChannel = api.Network.RegisterChannel("whatmod") .RegisterMessageType<WhatMod_StateMessage>()
                                                                    .SetMessageHandler<WhatMod_StateMessage>(OnStateMessage);

            api.ModLoader.GetModSystem<ModSystemSurvivalHandbook>(true).OnInitCustomPages += this.ModSystemWhatMod_OnInitCustomPages;
            api.ChatCommands.Create("redo").HandleWith((args) => { return RandomText(args); });
            
            foreach(var kv in _capi.LinkProtocols)
            {
                api.Logger.Notification(kv.Key);
            }

            base.StartClientSide(api);
        }

        private void ModSystemWhatMod_OnInitCustomPages(List<GuiHandbookPage> pages)
        {
            _whatPage = new GuiHandbookTextPage();
            _whatPage.Title = "What Mod Manager";
            _whatPage.Text = "Whoops, not loaded yet";
            _whatPage.pageCode = "whatmod-root";
            _whatPage.Init(_capi);

            pages.Add(_whatPage);
        }

        private void OnStateMessage(WhatMod_StateMessage message)
        {
            //Message from server with new tooltip render
            //Store locally 
            //Rebuild handbook -> Seperate step for language support
            //¯\_(ツ)_/¯
            //Reload handbook page
            _whatPage.Text = AddHandbookElements(message.message);
            _whatPage.Init(_capi);
            reloadHandbook();
        }

        private string AddHandbookElements(string tooltip)
        {
            var lines = tooltip.Split("<br>");

            for (int i=0;i<lines.Length;i++)
            {
                lines[i] += "   =)";
                var match = Regex.Match(lines[i], @"modid=(.+?)/");
                if (match.Success)
                {
                    var modid = match.Groups[1].Value;
                    lines[i] += $"    <a href=\"command:///whatmod hide {modid}\">Hide</a>";
                }
            }
            
            return String.Join("<br>",lines);
        }

        private TextCommandResult RandomText(TextCommandCallingArgs args)
        {
            var text = " test page text ";
            _whatPage.Text = text;
            _whatPage.Init(_capi); //Reinit page so the richtext elements get reparsed   //lazy
            reloadHandbook();

            return TextCommandResult.Success(text);
        }
        private void reloadHandbook()
        {
            GuiDialogHandbook guiDialogHandbook = _capi.Gui.OpenedGuis.FirstOrDefault((GuiDialog dlg) => dlg is GuiDialogHandbook) as GuiDialogHandbook;
            if (guiDialogHandbook == null)
            {
                return;
            }
            guiDialogHandbook.ReloadPage();
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            _api = api;

            _serverChannel = _api.Network.RegisterChannel("whatmod").RegisterMessageType<WhatMod_StateMessage>();

            api.Event.PlayerNowPlaying += (joinedPlayer) => { OnPlayerFinishedLoading(joinedPlayer); };

            try {
                Initialize(api);

                //Catelogue enabled mods
                foreach (var o in oldMods)
                {
                    OldMod entry = oldMods[o.Key];

                    entry.newMod = false;
                    entry.newVersion = false;
                }

                var loadedMods = api.ModLoader.Mods;
                var defaultHiddenMods = String.Format("^(game|survival|creative|{0})$", this.Mod.Info.ModID);
                var hiddenModRegex = new Regex(defaultHiddenMods);

                //Update ledger with loaded mods
                foreach (var mod in loadedMods)
                {
                    var info = mod.Info;

                    if (oldMods.ContainsKey(info.ModID))
                    {
                        var entry = oldMods[info.ModID];

                        entry.newVersion = info.Version != entry.version;
                        if (entry.latestVersion == info.Version) entry.hasNewVersion = false;

                        entry.name = info.Name;
                        entry.version = info.Version;
                        entry._wasChecked = true;

                        if (_unhideAdded && entry.removed) { entry.hidden = false; entry.newMod = true; }
                        entry.removed = false;
                        //api.Logger.Notification("Old");
                    }
                    else
                    {
                        var entry = new OldMod();
                        entry.newMod = true;
                        entry.removed = false;

                        entry.modID = info.ModID;
                        entry.name = info.Name;
                        entry.version = info.Version;
                        entry._wasChecked = true;
                        entry.website = "";
                        entry.assetID = "";

                        //if (hiddenModRegex.IsMatch(mod.Info.ModID)) { entry.hidden = true; }

                        oldMods[entry.modID] = entry;
                    }
                }


                foreach (var mod in oldMods)
                {
                    if (!mod.Value._wasChecked) { 
                        mod.Value.removed = true; 
                        mod.Value._wasChecked = true;
                        if (_hideRemoved) { mod.Value.hidden = true; }
                    }
                }


                RegisterCommands(api);

                //Querry the API and push data to worldConfig
                Task.Run(() => GetAPIInfo().ContinueWith(new Action<Task>((Task t) => { PushOldMods(api); PushTooltip(api); })));

            }
            catch (Exception e) { api.Logger.Error($"\n\n###   {this.Mod.Info.Name} {this.Mod.Info.Version} -- Something went wrong during Initialization.  ###\n###  Bother @Soggylithe on Discord or ModDB.  ###\n"); api.Logger.Error(e); }
        }

        public override double ExecuteOrder()
        {
            return 999999.0;
        }
        public override bool ShouldLoad(EnumAppSide side)
        {
            return true;
            //return side == EnumAppSide.Server;
        }

        public void OnPlayerFinishedLoading(IServerPlayer joinedPlayer)
        {
            _serverChannel.SendPacket<WhatMod_StateMessage>(new WhatMod_StateMessage { message = RenderTooltip(_api) }, joinedPlayer);
        }

        public void RegisterCommands(ICoreServerAPI api)
        {
            var flagIconNames = flags.Keys.ToArray();

            api.ChatCommands.Create("whatmod")
            .WithDescription("Commands for WhatModWasThat.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return Root(api); })

            //####################################################################

            .BeginSubCommand("hide")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("ModID"))
            .WithDescription("Hide a mod from the tooltip.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return HideMod(api, args); })
            .EndSubCommand()

            .BeginSubCommand("show")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("ModID"))
            .WithDescription("Show a hidden mod from the tooltip.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return ShowMod(api, args); })
            .EndSubCommand()

            //####################################################################

            .BeginSubCommand("hideall")
            .WithDescription("Hide all mods from the tooltip.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return HideAllMods(api); })
            .EndSubCommand()

            .BeginSubCommand("showall")
            .WithDescription("Show all hidden mods from the tooltip.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return ShowAllMods(api); })
            .EndSubCommand()

            //####################################################################

            .BeginSubCommand("listids")
            .WithDescription("List all loaded mods in chat.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return ListIDs(api); })
            .EndSubCommand()

            .BeginSubCommand("togglenames")
            .WithDescription("Toggle tooltip between names and modIDs.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return ToggleNames(api); })
            .EndSubCommand()

            .BeginSubCommand("tooltip")
            .WithDescription("Show the current tooltip in chat.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return Tooltip(api); })
            .EndSubCommand()

            .BeginSubCommand("debugRemove")
            .WithDescription("Remove all data from the worldConfig.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return DebugRemove(api); })
            .EndSubCommand()

            .BeginSubCommand("setIconColor")
            .WithArgs(new ICommandArgumentParser[] { api.ChatCommands.Parsers.OptionalWord("Icon"), api.ChatCommands.Parsers.OptionalWord("Color")})
            .WithDescription("Sets the color of the specified icon.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return SetIconColor(api, args); })
            .EndSubCommand()

            .BeginSubCommand("toggleOption")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("Option"))
            .WithDescription("Toggle the specified option.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return ToggleOption(api, args); })
            .EndSubCommand()

            .BeginSubCommand("resetIconColor")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("Icon"))
            .WithDescription("Reset the color of one or all icons.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith((args) => { return ResetIconColor(api, args); })
            .EndSubCommand();

        }

        public TextCommandResult HideMod(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            var message = ListMods();

            var word = args.LastArg?.ToString();
            if (word != null)
            {
                if (oldMods.ContainsKey(word))
                {
                    oldMods[word].hidden = true;
                    
                    PushOldMods(api);
                    PushTooltip(api);

                    return TextCommandResult.Success($"{oldMods[word].name} hidden.");
                }
                else
                {
                    //Invalid all mod ids
                    message += $"<br>{word} not found, try a ModID from this list.";
                }
            }
            else {
                message += "<br>Try a ModID from this list.<br>";
            }

            return TextCommandResult.Success(message);
        }

        public TextCommandResult ShowMod(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            var message = ListMods();

            var word = args.LastArg?.ToString();
            if (word != null)
            {
                if (oldMods.ContainsKey(word))
                {
                    oldMods[word].hidden = false;

                    PushOldMods(api);
                    PushTooltip(api);

                    return TextCommandResult.Success($"{oldMods[word].name} unhidden.");
                }
                else
                {
                    //Invalid all mod ids
                    message += $"<br>{word} not found, try a ModID from this list.";
                }
            }
            else
            {
                message += "<br>Try a ModID from this list.<br>";
            }

            return TextCommandResult.Success(message);
        }

        public TextCommandResult HideAllMods(ICoreServerAPI api)
        {

            foreach (var mod in oldMods)
            {
                mod.Value.hidden = true;
            }

            PushTooltip(api);
            PushOldMods(api);

            return TextCommandResult.Success("All mods hidden.");
        }

        public TextCommandResult ShowAllMods(ICoreServerAPI api)
        {
            foreach (var mod in oldMods)
            {
                mod.Value.hidden = false;
            }

            PushTooltip(api);
            PushOldMods(api);
            return TextCommandResult.Success("All mods unhidden.");
        }

        public TextCommandResult ListIDs(ICoreServerAPI api)
        {
            var message = ListMods();

            return TextCommandResult.Success(message);
        }
        public TextCommandResult ToggleNames(ICoreServerAPI api)
        {
            _renderNames = !_renderNames;

            PushTooltip(api);

            var t = RenderTooltip(api);
            return TextCommandResult.Success(t);
        }
        public TextCommandResult Tooltip(ICoreServerAPI api)
        {
            var tooltip = RenderTooltip(api);

            return TextCommandResult.Success(tooltip);
        }
        public TextCommandResult Root(ICoreServerAPI api)
        {
            var tooltip = RenderTooltip(api);

            var text = new StringBuilder();

            foreach (var cm in api.ChatCommands.Get("whatmod").AllSubcommands)
            {
                text.AppendLine("<a href=\"chattype:///whatmod " + cm.Key + "\">" + cm.Value.CallSyntax + "</a> - " + (cm.Value != null && cm.Value.Description != null ? cm.Value.Description : ""));
            }

            tooltip = tooltip + "<br><br>" + text.ToString();

            return TextCommandResult.Success(tooltip);
        }
        public TextCommandResult DebugRemove(ICoreServerAPI api)
        {
            DebugRemoveAll(api);

            return TextCommandResult.Success("All data removed from worldConfig.");
        }
        public TextCommandResult SetIconColor(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            var iconKeys = String.Join(", ", flags.Keys.Select(o => $"<font color=#{flags[o]}>{o.ToString()}</font>").ToArray());

            var _icon = "";
            var _color = "";

            foreach(var p in args.Parsers){
                if (p.ArgumentName == "Icon") { _icon = p.GetValue()?.ToString(); continue; }
                if (p.ArgumentName == "Color") { _color = p.GetValue()?.ToString(); continue; }
            }

            if (_icon != null) { _icon = char.ToUpper(_icon[0]) + _icon.Substring(1); }
            if (_color != null && _color.Length == 7 && _color[0] == '#') { _color = _color.Substring(1); }

            if (_icon != null && flags.ContainsKey(_icon))
            {
                var validColorReg = new Regex( @"[0-9a-fA-F]{6}" );
                if(_color != null && _color.Length == 6 && validColorReg.IsMatch(_color))
                {
                    var color = validColorReg.Match(_color).Value;
                    if(color != String.Empty) {
                        flags[_icon] = color;
                        PushTooltip(api);
                        PushOptions(api);
                        return TextCommandResult.Success($"Successfully set {_icon} to <font color=#{color}>{color}</font>");
                    }
                    else
                    {
                        return TextCommandResult.Success($"Failed to set {_icon}'s color, unknown color {_color}.<br>");
                    }
                }
                else if (_color != null && _color.Length == 6)
                {
                    return TextCommandResult.Success($"{_color} is not a valid color.<br>Please enter a valid Hexidecimal color. A hex color is 6 characters 0-9 or A-F. '80A4FF'<br>");
                }
                else
                {
                    return TextCommandResult.Success($"Please enter a valid Hexidecimal color. A hex color is 6 characters 0-9 or A-F. '80A4FF'<br>");
                }
            }
            else { 
                if(_icon == null)
                {
                    return TextCommandResult.Success("No Icon found. Please enter which Icon you want to change.<br>" + $"[{iconKeys}]<br>") ;
                }
                else
                {
                    return TextCommandResult.Success($"\"{_icon}\" is not one of the Icons.<br>Please enter which Icon you want to change.<br>" + $"[{iconKeys}]<br>");
                }
            }
        }
        public TextCommandResult ResetIconColor(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            var iconKeys = String.Join(", ", flags.Keys.Select(o => $"<font color=#{flags[o]}>{o.ToString()}</font>").ToArray());

            var _icon = args.LastArg?.ToString();

            if(_icon != null) { _icon = char.ToUpper(_icon[0]) + _icon.Substring(1); }


            if (_icon != null && flags.ContainsKey(_icon))
            {
                var def = defFlags[_icon];
                flags[_icon] = def;

                PushTooltip(api);
                PushOptions(api);
                return TextCommandResult.Success($"{_icon} reset to <font color=#{defFlags[_icon]}>default color</font>.");
            }
            else if(_icon != null && _icon == "All")
            {
                foreach(var k in flags.Keys)
                {
                    flags[k] = defFlags[k];
                }

                PushTooltip(api);
                PushOptions(api);
                return TextCommandResult.Success("All icon colors reset.");
            }
            else
            {
                if (_icon == null)
                {
                    return TextCommandResult.Success("No Icon found. Please enter which Icon you want to reset.<br>" + $"[{iconKeys}, All]<br>");
                }
                else
                {
                    return TextCommandResult.Success($"\"{_icon}\" is not one of the Icons.<br>Please enter which Icon you want to reset.<br>" + $"[{iconKeys}, All]<br>");
                }
            }
        }
        public TextCommandResult ToggleOption(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            var message = "";
            List<string> options = new List<string> { "Show-Names","Show-Only-Changes","Hide-Removed-Mods","Unhide-Added-Mods","Show-New-Versions-Available" };

            var word = args.LastArg?.ToString();
            if (word != null && word.Length > 0)
            {
                string key = "";
                bool state = false;

                switch(word)
                {
                    case "Show-Names":
                        key = "Whatmod-Render-Mod-Names";
                        state = _renderNames;
                        break;
                    case "Show-Only-Changes":
                        key = "Whatmod-Render-Only-Changed-Mods";
                        state = _renderOnlyChanges;
                        break;
                    case "Hide-Removed-Mods":
                        key = "Whatmod-Auto-Hide-Removed-Mods";
                        state = _hideRemoved;
                        break;
                    case "Unhide-Added-Mods":
                        key = "Whatmod-Unhide-Added";
                        state = _unhideAdded;
                        break;
                    case "Show-New-Versions-Available":
                        key = "Whatmod-Show-New-Version-Available";
                        state = _showNewVersionAvailable;
                        break;
                    default:
                        break;
                }

                if(key != "")
                {
                    api.World.Config.SetBool(key, !state);
                    
                    PullOptions(api);
                    PushTooltip(api);

                    message = $"Option {word} set to {!state}.";
                }
                else
                {
                    message = "Please enter what option to toggle<br>" + $"[{String.Join(", ", options)}]";
                }
            }
            else
            {
                message = "Please enter what option to toggle<br>"+$"[{String.Join(", ",options)}]";
            }

            return TextCommandResult.Success(message);
        }



        private string RenderTooltip(ICoreServerAPI api)
        {   
        var lines = new List<string>();

        var longestName = 0;
        var longestVersion = 0;

        var maxName = 20;
        var maxVersion = 8;
        //Build tooltip string
        foreach (var mod in oldMods)
        {
            if (mod.Value.name.Length > longestName) longestName = mod.Value.name.Length;
            if (mod.Value.version.Length > longestVersion) longestVersion = mod.Value.version.Length;
        }

        longestName = maxName;
        longestVersion = maxVersion;

        //Build tooltip string
        foreach (var mod in oldMods)
        {
            if (mod.Value.hidden) continue;
            if (_hideRemoved && mod.Value.removed) continue;
            if (_renderOnlyChanges && ( !mod.Value.newMod ||
                                        !mod.Value.newVersion ||
                                        !mod.Value.removed
                                        )) continue;

            var flag = $"<font color=#{flags["Default"]}><icon name=wpVessel></icon></font>";
            if (mod.Value.newMod) { flag = $"<font color=#{flags["Added"]}><icon name=wpVessel></icon></font>"; }
            else if (mod.Value.newVersion) { flag = $"<font color=#{flags["Updated"]}><icon name=wpTrader></icon></font>"; }
            else if (mod.Value.removed) { flag = $"<font color=#{flags["Removed"]}><icon name=select></icon></font>"; }
            else if (mod.Value.hasNewVersion && _showNewVersionAvailable) { flag = $"<font color=#{flags["NeedsUpdate"]}><icon name=wpTrader></icon></font>"; }

            var nameL = Math.Min(mod.Value.name.Length, longestName);
            var verL = Math.Min(mod.Value.version.Length, longestVersion);
            var shorttenedVer = verL < mod.Value.version.Length;
            var sortCode = new Regex(@"[A-Za-z]").Match(mod.Value.name).ToString();

            var formattedWebsite = mod.Value.website;
            var formattedName = mod.Value.name.Substring(0, nameL);
            var formattedVersion = shorttenedVer ? "…" + mod.Value.version.Substring(mod.Value.version.Length - maxVersion + 1, (maxVersion - 1)) : mod.Value.version;

            if (!_renderNames) { 
                nameL = Math.Min(mod.Value.modID.Length, longestName);
                formattedName = mod.Value.modID.Substring(0, nameL);
            }

            var spacing = formattedName.PadRight(longestName).Substring(nameL);
            formattedName = formattedWebsite.StartsWith("http") ? String.Format("<a href={1}>{0}</a>{2}", formattedName, formattedWebsite, spacing) : formattedName+spacing;
            formattedVersion = formattedVersion.PadRight(longestVersion);

            //                           20chr - 8chr Symbol
            lines.Add(String.Format("<meta sortCode={3} modid={4}/><code>{0} - {1} {2}</code>",  formattedName,
                                                                                        formattedVersion,
                                                                                        flag,
                                                                                        sortCode,
                                                                                        mod.Value.modID));
        }

            lines.Sort();

            var tooltip = "<br>" + (lines.Count > 0 ? String.Join("<br>", lines) : "None");

            return tooltip;
        }

        private void Initialize(ICoreServerAPI api)
        {
            var empty = WebUtility.UrlEncode("{}");
            var raw = api.World.Config.GetString("Whatmod-Old-Mods", empty);
            if (raw == "") raw = empty;
            raw = WebUtility.UrlDecode(raw);

            oldMods = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, OldMod>>(raw);

            PullOptions(api);
        }
        private void PullOptions(ICoreServerAPI api)
        {
            _renderNames = api.World.Config.GetBool("Whatmod-Render-Mod-Names", true);
            _renderOnlyChanges = api.World.Config.GetBool("Whatmod-Render-Only-Changed-Mods", false);
            _hideRemoved = api.World.Config.GetBool("Whatmod-Auto-Hide-Removed-Mods", false);
            _showNewVersionAvailable = api.World.Config.GetBool("Whatmod-Show-New-Version-Available", true);
            _unhideAdded = api.World.Config.GetBool("Whatmod-Unhide-Added", true);

            PullFlags(api);
        }

        private void PullFlags(ICoreServerAPI api)
        {
            flags["Default"] = api.World.Config.GetString("Whatmod-Flags-Default", defFlags["Default"]);
            flags["Added"] = api.World.Config.GetString("Whatmod-Flags-Added", defFlags["Added"]);
            flags["Updated"] = api.World.Config.GetString("Whatmod-Flags-Updated", defFlags["Updated"]);
            flags["Removed"] = api.World.Config.GetString("Whatmod-Flags-Removed", defFlags["Removed"]);
            flags["NeedsUpdate"] = api.World.Config.GetString("Whatmod-Flags-NeedsUpdate", defFlags["NeedsUpdate"]);
        }

        private void PushOptions(ICoreServerAPI api)
        {
            api.World.Config.SetBool("Whatmod-Render-Mod-Names", _renderNames);
            api.World.Config.SetBool("Whatmod-Render-Only-Changed-Mods", _renderOnlyChanges);
            api.World.Config.SetBool("Whatmod-Auto-Hide-Removed-Mods", _hideRemoved);
            api.World.Config.SetBool("Whatmod-Show-New-Version-Available", _showNewVersionAvailable);
            api.World.Config.SetBool("Whatmod-Unhide-Added", _unhideAdded);

            api.World.Config.SetString("Whatmod-Flags-Default", flags["Default"]);
            api.World.Config.SetString("Whatmod-Flags-Added", flags["Added"]);
            api.World.Config.SetString("Whatmod-Flags-Updated", flags["Updated"]);
            api.World.Config.SetString("Whatmod-Flags-Removed", flags["Removed"]);
            api.World.Config.SetString("Whatmod-Flags-NeedsUpdate", flags["NeedsUpdate"]);
        }

        private void PushOldMods(ICoreServerAPI api)
        {
            //Inject into worldconfig
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            var oldModData = System.Text.Json.JsonSerializer.Serialize<Dictionary<string, OldMod>>(oldMods, options);

            oldModData = WebUtility.UrlEncode(oldModData);
            api.World.Config.SetString("Whatmod-Old-Mods", oldModData);
        }

        private void PushTooltip(ICoreServerAPI api)
        {
            var tooltip = RenderTooltip(api);
            
            api.World.Config.SetString("Whatmod-Last-Loaded-Mods", tooltip);

            _serverChannel.BroadcastPacket<WhatMod_StateMessage>(new WhatMod_StateMessage { message = tooltip });
        }

        //Should be handled more asyncly but meh
        private async Task GetAPIInfo()
        {
            foreach (var mod in oldMods)
            {
                //DB API call
                WebRequest request = WebRequest.Create($"https://mods.vintagestory.at/api/mod/{mod.Value.modID}");
                Stream s = request.GetResponse().GetResponseStream();
                StreamReader streamReader = new StreamReader(s);

                string rawJson = streamReader.ReadToEnd();
                dynamic response = JObject.Parse(rawJson);

                var website = "";

                //good response carry on
                if (response.statuscode == "200")
                {
                    mod.Value.assetID = response.mod.assetid;
                    website = $"https://mods.vintagestory.at/show/mod/{response.mod.assetid}";

                    var versions = response.mod.releases;
                    if (versions != null && versions.Count > 0)
                    {
                        var str = versions[0].modversion;
                        if (str != null && str != "")
                        {
                            mod.Value.latestVersion = str;
                            if (mod.Value.latestVersion != mod.Value.version) { 
                                _api.Logger.Notification($"{mod.Key} has new version."); 
                                mod.Value.hasNewVersion = true; 
                            }
                        }
                    }
                }

                //Sanitize trailing '/'
                if (website.EndsWith('/')) { website = website.Substring(0, website.Length - 2); }
                mod.Value.website = website;
            }
        }

        private void DebugRemoveAll(ICoreServerAPI api)
        {
            api.World.Config.SetString("Whatmod-Last-Loaded-Mods", "this is default");
            api.World.Config.SetString("Whatmod-Old-Mods", "");

            api.World.Config.SetBool("Whatmod-Render-Mod-Names", true);
            api.World.Config.SetBool("Whatmod-Render-Only-Changed-Mods", false);
            api.World.Config.SetBool("Whatmod-Auto-Hide-Removed-Mods", false);
            api.World.Config.SetBool("Whatmod-Show-New-Version-Available", true);
            api.World.Config.SetBool("Whatmod-Unhide-Added", false);

            api.World.Config.SetString("Whatmod-Flags-Default", "F8FFE5");
            api.World.Config.SetString("Whatmod-Flags-Added", "06D6A0");
            api.World.Config.SetString("Whatmod-Flags-Updated", "1B9AAA");
            api.World.Config.SetString("Whatmod-Flags-Removed", "EF478F");
            api.World.Config.SetString("Whatmod-Flags-NeedsUpdate", "FFC43D");
        }

        private string ListMods()
        {
            var sb = new StringBuilder();
            var longestName = 0;

            foreach (var mod in oldMods)
            {
                if (mod.Value.modID.Length > longestName) longestName = mod.Value.modID.Length;
            }

            foreach (var mod in oldMods)
            {
                sb.AppendLine($"<code>{mod.Value.modID.PadRight(longestName)} - {mod.Value.name}</code>");
            }

            return sb.ToString();
        }
    }
}
