﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using NWN;
using SWLOR.Game.Server.Data.Contracts;
using SWLOR.Game.Server.Data;
using SWLOR.Game.Server.Enumeration;
using SWLOR.Game.Server.GameObject;
using SWLOR.Game.Server.NWNX.Contracts;
using SWLOR.Game.Server.Service.Contracts;
using static NWN.NWScript;
using System.Text;
using SWLOR.Game.Server.Data.Entity;

namespace SWLOR.Game.Server.Service
{
    public class ChatComponent
    {
        public string m_Text;
        public bool m_Translatable;
        public bool m_CustomColour;
        public byte m_ColourRed;
        public byte m_ColourGreen;
        public byte m_ColourBlue;
    };

    public class ChatTextService : IChatTextService
    {
        private readonly INWScript _;
        private readonly IColorTokenService _color;
        private readonly INWNXChat _nwnxChat;
        private readonly IDataService _data;
        private readonly ILanguageService _language;
        private readonly IEmoteStyleService _emoteStyle;

        public ChatTextService(
            INWScript script,
            IColorTokenService color,
            INWNXChat nwnxChat,
            IDataService data,
            ILanguageService language,
            IEmoteStyleService emoteStyle)
        {
            _ = script;
            _color = color;
            _nwnxChat = nwnxChat;
            _data = data;
            _language = language;
            _emoteStyle = emoteStyle;
        }

        public void OnNWNXChat()
        {
            ChatChannelType channel = (ChatChannelType)_nwnxChat.GetChannel();

            // So we're going to play with a couple of channels here.

            // - PlayerTalk, PlayerWhisper, PlayerParty, and PlayerShout are all IC channels. These channels
            //   are subject to emote colouring and language translation. (see below for more info).
            // - PlayerParty is an IC channel with special behaviour. Those outside of the party but within
            //   range may listen in to the party chat. (see below for more information).
            // - PlayerShout sends a holocom message server-wide through the DMTell channel.
            // - PlayerDM echoes back the message received to the sender.

            bool inCharacterChat =
                channel == ChatChannelType.PlayerTalk ||
                channel == ChatChannelType.PlayerWhisper ||
                channel == ChatChannelType.PlayerParty ||
                channel == ChatChannelType.PlayerShout;

            bool messageToDm = channel == ChatChannelType.PlayerDM;

            if (!inCharacterChat && !messageToDm)
            {
                // We don't much care about traffic on the other channels.
                return;
            }

            NWObject sender = _nwnxChat.GetSender();
            string message = _nwnxChat.GetMessage();

            if (string.IsNullOrWhiteSpace(message))
            {
                // We can't handle empty messages, so skip it.
                return;
            }

            if (ChatCommandService.CanHandleChat(sender, message) ||
                BaseService.CanHandleChat(sender, message) ||
                CraftService.CanHandleChat(sender, message))
            {
                // This will be handled by other services, so just bail.
                return;
            }

            if (channel == ChatChannelType.PlayerDM)
            {
                // Simply echo the message back to the player.
                _nwnxChat.SendMessage((int)ChatChannelType.ServerMessage, "(Sent to DM) " + message, sender, sender);
                return;
            }

            // At this point, every channel left is one we want to manually handle.
            _nwnxChat.SkipMessage();

            // If this is a shout message, and the holonet is disabled, we disallow it.
            if (channel == ChatChannelType.PlayerShout && sender.IsPC && sender.GetLocalInt("DISPLAY_HOLONET") == FALSE)
            {
                NWPlayer player = sender.Object;
                player.SendMessage("You have disabled the holonet and cannot send this message.");
                return;
            }

            List<ChatComponent> chatComponents;

            // Quick early out - if we start with "//" or "((", this is an OOC message.
            if (message.Length >= 2 && (message.Substring(0, 2) == "//" || message.Substring(0, 2) == "(("))
            {
                ChatComponent component = new ChatComponent
                {
                    m_Text = message,
                    m_CustomColour = true,
                    m_ColourRed = 255,
                    m_ColourGreen = 0,
                    m_ColourBlue = 255,
                    m_Translatable = false
                };

                chatComponents = new List<ChatComponent>();
                chatComponents.Add(component);
            }
            else
            {
                if (_emoteStyle.GetEmoteStyle(sender) == EmoteStyle.Regular)
                {
                    chatComponents = SplitMessageIntoComponents_Regular(message);
                }
                else
                {
                    chatComponents = SplitMessageIntoComponents_Novel(message);
                }

                // For any components with colour, set the emote colour.
                foreach (ChatComponent component in chatComponents)
                {
                    if (component.m_CustomColour)
                    {
                        component.m_ColourRed = 0;
                        component.m_ColourGreen = 255;
                        component.m_ColourBlue = 0;
                    }
                }
            }

            // Now, depending on the chat channel, we need to build a list of recipients.
            bool needsAreaCheck = false;
            float distanceCheck = 0.0f;

            List<NWObject> recipients = new List<NWObject>();

            // The sender always wants to see their own message.
            recipients.Add(sender);

            // This is a server-wide holonet message (that receivers can toggle on or off).
            if (channel == ChatChannelType.PlayerShout)
            {
                recipients.AddRange(NWModule.Get().Players.Where(player => player.GetLocalInt("DISPLAY_HOLONET") == TRUE));
                recipients.AddRange(App.GetAppState().ConnectedDMs);
            }
            // This is the normal party chat, plus everyone within 20 units of the sender.
            else if (channel == ChatChannelType.PlayerParty)
            {
                // Can an NPC use the playerparty channel? I feel this is safe ...
                NWPlayer player = sender.Object;
                recipients.AddRange(player.PartyMembers.Cast<NWObject>().Where(x => x != sender));
                recipients.AddRange(App.GetAppState().ConnectedDMs);

                needsAreaCheck = true;
                distanceCheck = 20.0f;
            }
            // Normal talk - 20 units.
            else if (channel == ChatChannelType.PlayerTalk)
            {
                needsAreaCheck = true;
                distanceCheck = 20.0f;
            }
            // Whisper - 4 units.
            else if (channel == ChatChannelType.PlayerWhisper)
            {
                needsAreaCheck = true;
                distanceCheck = 4.0f;
            }

            if (needsAreaCheck)
            {
                recipients.AddRange(sender.Area.Objects.Where(obj => obj.IsPC && _.GetDistanceBetween(sender, obj) <= distanceCheck));
                recipients.AddRange(App.GetAppState().ConnectedDMs.Where(dm => dm.Area == sender.Area && _.GetDistanceBetween(sender, dm) <= distanceCheck));
            }

            // Now we have a list of who is going to actually receive a message, we need to modify
            // the message for each recipient then dispatch them.

            foreach (NWObject obj in recipients.Distinct())
            {
                // Generate the final message as perceived by obj.

                StringBuilder finalMessage = new StringBuilder();

                if (channel == ChatChannelType.PlayerShout)
                {
                    finalMessage.Append("[Holonet] ");
                }
                else if (channel == ChatChannelType.PlayerParty)
                {
                    finalMessage.Append("[Comms] ");

                    if (obj.IsDM)
                    {
                        // Convenience for DMs - append the party members.
                        finalMessage.Append("{ ");

                        int count = 0;
                        NWPlayer player = sender.Object;
                        List<NWPlayer> partyMembers = player.PartyMembers.ToList();

                        foreach (NWPlayer otherPlayer in partyMembers)
                        {
                            string name = otherPlayer.Name;
                            finalMessage.Append(name.Substring(0, Math.Min(name.Length, 10)));

                            ++count;

                            if (count >= 3)
                            {
                                finalMessage.Append(", ...");
                                break;
                            }
                            else if (count != partyMembers.Count)
                            {
                                finalMessage.Append(",");
                            }
                        }

                        finalMessage.Append(" } ");
                    }
                }

                SkillType language = _language.GetActiveLanguage(sender);
                
                // Wookiees cannot speak any other language (but they can understand them).
                // Swap their language if they attempt to speak in any other language.
                CustomRaceType race = (CustomRaceType) _.GetRacialType(sender);
                if (race == CustomRaceType.Wookiee && language != SkillType.Shyriiwook)
                {
                    _language.SetActiveLanguage(sender, SkillType.Shyriiwook);
                    language = SkillType.Shyriiwook;
                }

                int colour = _language.GetColour(language);
                byte r = (byte)(colour >> 24 & 0xFF);
                byte g = (byte)(colour >> 16 & 0xFF);
                byte b = (byte)(colour >> 8 & 0xFF);

                if (language != SkillType.Basic)
                {
                    string languageName = _language.GetName(language);
                    finalMessage.Append(_color.Custom($"[{languageName}] ", r, g, b));
                }

                foreach (ChatComponent component in chatComponents)
                {
                    string text = component.m_Text;

                    if (component.m_Translatable && language != SkillType.Basic)
                    {
                        text = _language.TranslateSnippetForListener(sender, obj.Object, language, component.m_Text);

                        if (colour != 0)
                        {
                            text = _color.Custom(text, r, g, b);
                        }
                    }

                    if (component.m_CustomColour)
                    {
                        text = _color.Custom(text, component.m_ColourRed, component.m_ColourGreen, component.m_ColourBlue);
                    }

                    finalMessage.Append(text);
                }

                // Dispatch the final message - method depends on the original chat channel.
                // - Shout and party is sent as DMTalk. We do this to get around the restriction that
                //   the PC needs to be in the same area for the normal talk channel.
                //   We could use the native channels for these but the [shout] or [party chat] labels look silly.
                // - Talk and whisper are sent as-is.

                ChatChannelType finalChannel = channel;

                if (channel == ChatChannelType.PlayerShout || channel == ChatChannelType.PlayerParty)
                {
                    finalChannel = ChatChannelType.DMTalk;
                }

                // There are a couple of colour overrides we want to use here.
                // - One for holonet (shout).
                // - One for comms (party chat).

                string finalMessageColoured = finalMessage.ToString();

                if (channel == ChatChannelType.PlayerShout)
                {
                    finalMessageColoured = _color.Custom(finalMessageColoured, 0, 180, 255);
                }
                else if (channel == ChatChannelType.PlayerParty)
                {
                    finalMessageColoured = _color.Orange(finalMessageColoured);
                }

                _nwnxChat.SendMessage((int)finalChannel, finalMessageColoured, sender, obj);
            }
        }

        public void OnModuleEnter()
        {
            NWPlayer player = _.GetEnteringObject();
            if (!player.IsPlayer) return;

            var dbPlayer = _data.Single<Player>(x => x.ID == player.GlobalID);
            player.SetLocalInt("DISPLAY_HOLONET", dbPlayer.DisplayHolonet ? TRUE : FALSE);
        }
        
        private enum WorkingOnEmoteStyle
        {
            None,
            Asterisk,
            Bracket,
            ColonForward,
            ColonBackward
        };

        private static List<ChatComponent> SplitMessageIntoComponents_Regular(string message)
        {
            List<ChatComponent> components = new List<ChatComponent>();

            WorkingOnEmoteStyle workingOn = WorkingOnEmoteStyle.None;
            int indexStart = 0;
            int length = -1;
            int depth = 0;

            for (int i = 0; i < message.Length; ++i)
            {
                char ch = message[i];

                if (ch == '[')
                {
                    if (workingOn == WorkingOnEmoteStyle.None || workingOn == WorkingOnEmoteStyle.Bracket)
                    {
                        depth += 1;
                        if (depth == 1)
                        {
                            ChatComponent component = new ChatComponent
                            {
                                m_CustomColour = false,
                                m_Translatable = true,
                                m_Text = message.Substring(indexStart, i - indexStart)
                            };
                            components.Add(component);

                            indexStart = i + 1;
                            workingOn = WorkingOnEmoteStyle.Bracket;
                        }
                    }
                }
                else if (ch == ']')
                {
                    if (workingOn == WorkingOnEmoteStyle.Bracket)
                    {
                        depth -= 1;
                        if (depth == 0)
                        {
                            length = i - indexStart;
                        }
                    }
                }
                else if (ch == '*')
                {
                    if (workingOn == WorkingOnEmoteStyle.None || workingOn == WorkingOnEmoteStyle.Asterisk)
                    {
                        if (depth == 0)
                        {
                            ChatComponent component = new ChatComponent
                            {
                                m_CustomColour = false,
                                m_Translatable = true,
                                m_Text = message.Substring(indexStart, i - indexStart)
                            };
                            components.Add(component);

                            depth = 1;
                            indexStart = i;
                            workingOn = WorkingOnEmoteStyle.Asterisk;
                        }
                        else
                        {
                            depth = 0;
                            length = i - indexStart + 1;
                        }
                    }
                }
                else if (ch == ':')
                {
                    if (workingOn == WorkingOnEmoteStyle.None || workingOn == WorkingOnEmoteStyle.ColonForward)
                    {
                        depth += 1;
                        if (depth == 1)
                        {
                            // Only match this colon if the next symbol is also a colon.
                            // This needs to be done because a single colon can be used in normal chat.
                            if (i + 1 < message.Length && message[i + 1] == ':')
                            {
                                ChatComponent component = new ChatComponent
                                {
                                    m_CustomColour = false,
                                    m_Translatable = true,
                                    m_Text = message.Substring(indexStart, i - indexStart)
                                };
                                components.Add(component);

                                indexStart = i;
                                workingOn = WorkingOnEmoteStyle.ColonForward;
                            }
                            else
                            {
                                depth -= 1;
                            }
                        }
                        else if (depth == 2)
                        {
                            workingOn = WorkingOnEmoteStyle.ColonBackward;
                        }
                    }
                    else if (workingOn == WorkingOnEmoteStyle.ColonBackward)
                    {
                        depth -= 1;
                        if (depth == 0)
                        {
                            length = i - indexStart + 1;
                        }
                    }
                }

                if (length != -1)
                {
                    ChatComponent component = new ChatComponent
                    {
                        m_CustomColour = workingOn != WorkingOnEmoteStyle.Bracket || message[indexStart] == '[',
                        m_Translatable = false,
                        m_Text = message.Substring(indexStart, length)
                    };
                    components.Add(component);

                    indexStart = i + 1;
                    length = -1;
                    workingOn = WorkingOnEmoteStyle.None;
                }
                else
                {
                    // If this is the last character in the string, we should just display what we've got.
                    if (i == message.Length - 1)
                    {
                        ChatComponent component = new ChatComponent
                        {
                            m_CustomColour = depth != 0,
                            m_Translatable = depth == 0,
                            m_Text = message.Substring(indexStart, i - indexStart + 1)
                        };
                        components.Add(component);
                    }
                }
            }

            // Strip any empty components.
            components.RemoveAll(comp => string.IsNullOrEmpty(comp.m_Text));

            return components;
        }

        private static List<ChatComponent> SplitMessageIntoComponents_Novel(string message)
        {
            List<ChatComponent> components = new List<ChatComponent>();

            int indexStart = 0;
            bool workingOnQuotes = false;
            bool workingOnBrackets = false;

            for (int i = 0; i < message.Length; ++i)
            {
                char ch = message[i];

                if (ch == '"')
                {
                    if (!workingOnQuotes)
                    {
                        ChatComponent component = new ChatComponent
                        {
                            m_CustomColour = true,
                            m_Translatable = false,
                            m_Text = message.Substring(indexStart, i - indexStart)
                        };
                        components.Add(component);

                        workingOnQuotes = true;
                        indexStart = i;
                    }
                    else
                    {
                        ChatComponent component = new ChatComponent
                        {
                            m_CustomColour = false,
                            m_Translatable = true,
                            m_Text = message.Substring(indexStart, i - indexStart + 1)
                        };
                        components.Add(component);

                        workingOnQuotes = false;
                        indexStart = i + 1;
                    }
                }
                else if (ch == '[')
                {
                    bool translate = workingOnQuotes;

                    ChatComponent component = new ChatComponent
                    {
                        m_CustomColour = !translate,
                        m_Translatable = translate,
                        m_Text = message.Substring(indexStart, i - indexStart)
                    };
                    components.Add(component);

                    workingOnBrackets = true;
                    indexStart = i + 1;
                }
                else if (ch == ']')
                {
                    ChatComponent component = new ChatComponent
                    {
                        m_CustomColour = true,
                        m_Translatable = false,
                        m_Text = message.Substring(indexStart, i - indexStart)
                    };
                    components.Add(component);

                    workingOnBrackets = false;
                    indexStart = i + 1;
                }
            }

            {
                bool translate = workingOnQuotes && !workingOnBrackets;

                ChatComponent component = new ChatComponent
                {
                    m_CustomColour = !translate,
                    m_Translatable = translate,
                    m_Text = message.Substring(indexStart, message.Length - indexStart)
                };
                components.Add(component);
            }

            // Strip any empty components.
            components.RemoveAll(comp => string.IsNullOrEmpty(comp.m_Text));

            return components;
        }

    }
}
