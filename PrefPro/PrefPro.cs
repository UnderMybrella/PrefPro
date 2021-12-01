using Dalamud.Game.Command;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Logging;

namespace PrefPro
{
    public unsafe class PrefPro : IDalamudPlugin
    {
        public enum NameSetting
        {
            FirstLast,
            FirstOnly,
            LastOnly,
            LastFirst
        }

        public enum GenderSetting
        {
            Male,
            Female,
            Random,
            Model,
            TheyThem,
            Dumpster
        }

        public string Name => "PrefPro";
        private const string CommandName = "/prefpro";

        private readonly DalamudPluginInterface _pi;
        private readonly CommandManager _commandManager;
        private readonly ClientState _clientState;
        private readonly Configuration _configuration;
        private readonly PluginUI _ui;

        //reEncode[1] == 0x29 && reEncode[2] == 0x3 && reEncode[3] == 0xEB && reEncode[4] == 0x2
        private static readonly byte[] FullNameBytes = { 0x02, 0x29, 0x03, 0xEB, 0x02, 0x03 };
        private static readonly byte[] FirstNameBytes = { 0x02, 0x2C, 0x0D, 0xFF, 0x07, 0x02, 0x29, 0x03, 0xEB, 0x02, 0x03, 0xFF, 0x02, 0x20, 0x02, 0x03 };
        private static readonly byte[] LastNameBytes = { 0x02, 0x2C, 0x0D, 0xFF, 0x07, 0x02, 0x29, 0x03, 0xEB, 0x02, 0x03, 0xFF, 0x02, 0x20, 0x03, 0x03 };

        private delegate int GetStringPrototype(void* unknown, byte* text, void* unknown2, void* stringStruct);
        private readonly Hook<GetStringPrototype> _getStringHook;

        private static string filterText = "";
        public string PlayerName => _clientState?.LocalPlayer?.Name.ToString();
        public ulong CurrentPlayerContentId => _clientState?.LocalContentId ?? 0;

        public PrefPro(
            [RequiredVersion("1.0")] SigScanner sigScanner,
            [RequiredVersion("1.0")] DalamudPluginInterface pluginInterface,
            [RequiredVersion("1.0")] CommandManager commandManager,
            [RequiredVersion("1.0")] ClientState clientState
            )
        {
            _pi = pluginInterface;
            _commandManager = commandManager;
            _clientState = clientState;

            _configuration = _pi.GetPluginConfig() as Configuration ?? new Configuration();
            _configuration.Initialize(_pi, this);

            _ui = new PluginUI(_configuration, this);

            _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Display the PrefPro configuration interface."
            });

            string getStringStr = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 83 B9 ?? ?? ?? ?? ?? 49 8B F9 49 8B F0 48 8B EA 48 8B D9 75 09 48 8B 01 FF 90";
            IntPtr getStringPtr = sigScanner.ScanText(getStringStr);
            _getStringHook = new Hook<GetStringPrototype>(getStringPtr, GetStringDetour);

            _getStringHook.Enable();

            _pi.UiBuilder.Draw += DrawUI;
            _pi.UiBuilder.OpenConfigUi += DrawConfigUI;
        }

        private int GetStringDetour(void* unknown, byte* text, void* unknown2, void* stringStruct)
        {
#if DEBUG
            int len = 0;
            byte* text2 = text;
            while (*text2 != 0) { text2++; len++; }
            string str = Encoding.ASCII.GetString(text, len);
            if (filterText != "" && str.Contains(filterText))
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < len; i++)
                    sb.Append($"{*(text + i):X} ");

                PluginLog.Log($"GS Dump  : {sb}");
                PluginLog.Log($"GetString: {Encoding.ASCII.GetString(text, len)}");

            }
#endif
            if (_configuration.Enabled)
            {
                HandlePtr(ref text);
            }
#if DEBUG
            len = 0;
            text2 = text;
            while (*text2 != 0) { text2++; len++; }
            int retVal = _getStringHook.Original(unknown, text, unknown2, stringStruct);
            str = Encoding.ASCII.GetString(text, len);
            if (filterText != "" && str.Contains(filterText))
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < len; i++)
                    sb.Append($"{*(text + i):X} ");

                PluginLog.Log($"GS Dump  : {sb}");
                PluginLog.Log($"GetString: {Encoding.ASCII.GetString(text, len)}");
            }
            return retVal;
#else
            return _getStringHook.Original(unknown, text, unknown2, stringStruct);
#endif
        }

        private void HandlePtr(ref byte* ptr)
        {
            var byteList = new List<byte>();
            int i = 0;
            while (ptr[i] != 0)
                byteList.Add(ptr[i++]);
            var byteArr = byteList.ToArray();

            // Write handlers, put them here
            var parsed = SeString.Parse(byteArr);
            for (int payloadIndex = 0; payloadIndex < parsed.Payloads.Count; payloadIndex++)
            {
                var thisPayload = parsed.Payloads[payloadIndex];
                if (thisPayload.Type == PayloadType.Unknown)
                {
                    // Add handlers here
                    Payload[] genders = new Payload[3];
                    bool hasPrev = payloadIndex > 0;
                    bool hasNext = payloadIndex < parsed.Payloads.Count - 1;
                    if (hasPrev) genders[0] = parsed.Payloads[payloadIndex - 1];
                    if (hasNext) genders[2] = parsed.Payloads[payloadIndex + 1];
                    genders[1] = thisPayload;

                    HandleGenderPayload(genders);
                    if (hasPrev) parsed.Payloads[payloadIndex - 1] = genders[0];
                    if (hasNext) parsed.Payloads[payloadIndex + 1] = genders[2];
                    parsed.Payloads[payloadIndex] = genders[1];

                    parsed.Payloads[payloadIndex] = HandleFullNamePayload(parsed.Payloads[payloadIndex]);
                    parsed.Payloads[payloadIndex] = HandleFirstNamePayload(parsed.Payloads[payloadIndex]);
                    parsed.Payloads[payloadIndex] = HandleLastNamePayload(parsed.Payloads[payloadIndex]);
                }
            }
            var encoded = parsed.Encode();

            if (ByteArrayEquals(encoded, byteArr))
                return;

            if (encoded.Length <= byteArr.Length)
            {
                int j;
                for (j = 0; j < encoded.Length; j++)
                    ptr[j] = encoded[j];
                ptr[j] = 0;
            }
            else
            {
                byte* newStr = (byte*)Marshal.AllocHGlobal(encoded.Length + 1);
                int j;
                for (j = 0; j < encoded.Length; j++)
                    newStr[j] = encoded[j];
                newStr[j] = 0;
                ptr = newStr;
            }
        }

        private static bool ByteArrayEquals(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
        {
            return a1.SequenceEqual(a2);
        }

        private static string Last(string[] array)
        {
            return array[array.Length - 1];
        }

        private static string First(string[] array)
        {
            return array[0];
        }

        private static string ReplaceLastOccurrence(string Source, string Find, string Replace)
        {
            int place = Source.LastIndexOf(Find);

            if (place == -1)
                return Source;

            string result = Source.Remove(place, Find.Length).Insert(place, Replace);
            return result;
        }

        private static Dictionary<string, string[]> THEY_THEM = new Dictionary<string, string[]>()
        {
            ["[he/she]'s"] = new string[] { null, "they", "'re" },
            ["[he/she]"] = new string[] { null, "they", null },
        };

        // For contextual pronouns (Like they/them), we need to potentially modify the past and next words in a sentence
        // These payloads are the previous block of text, the gender payload, and the next block of text
        // TODO: fix how these are referenced to actually get the previous block of text
        private void HandleGenderPayload(Payload[] payloads)
        {
            string previousBlock = payloads[0] == null ? null : Encoding.UTF8.GetString(payloads[0].Encode());
            string nextBlock = payloads[2] == null ? null : Encoding.UTF8.GetString(payloads[2].Encode());

            byte[] reEncode = payloads[1].Encode();
            // We have to compare bytes here because there is a wildcard in the middle
            if (reEncode[1] != 8 || reEncode[3] != 0xE9 || reEncode[4] != 5
                || _configuration.Gender == GenderSetting.Model)
                return;
            
            int femaleStart = 7;
            int femaleLen = reEncode[6] - 1;
            int maleStart = femaleStart + femaleLen + 2;
            int maleLen = reEncode[maleStart - 1] - 1;

            if (_configuration.Gender == GenderSetting.TheyThem)
            {
                // Okay, pronouns are *hard*
                // Pronouns like they/them are going to be more contextual in replacements than he/she
                // Sentences that might use he or she in two different contexts will require different pronouns
                // For instance, "Of course she's promising" and "If she keep up the good work"
                // These both have 'she' as the pronoun of choice, but need different replacements
                // "Of course they're promising" and "If they keep up the good work"
                // Her and their make it even worse -- "Did you collect my gil from her?" and "Is that her bag?"
                // Become "Did you collect my gil from them?" and "Is that their bag?" -- completely different words
                // To 'solve' this, we need to build a fallthrough key system from the previous and next 'word'

                List<String> keys = new List<string>();

                string maleKey = Encoding.UTF8.GetString(reEncode, maleStart, maleLen);
                string femaleKey = Encoding.UTF8.GetString(reEncode, femaleStart, femaleLen);
                string pronounKey = '[' + maleKey + '/' + femaleKey + ']';

                string previousWord = previousBlock == null ? null : Last(previousBlock.Trim().Split(' '));
                string nextWord = nextBlock == null ? null : First(nextBlock.Trim().Split(' '));

                if (previousWord != null && nextWord != null)
                {
                    keys.Add(previousWord + pronounKey + nextWord);
                    keys.Add(previousWord + pronounKey);
                    keys.Add(pronounKey + nextWord);
                } 
                else if (previousWord != null)
                {
                    keys.Add(previousWord + pronounKey);
                } 
                else if (nextWord != null)
                {
                    keys.Add(pronounKey + nextWord);
                }

                keys.Add(pronounKey);

                PluginLog.Information("Attempting gender payload: {0},{1},{2}", previousWord, pronounKey, nextWord);

                string[] replacements = null;
                foreach (string key in keys)
                {
                    THEY_THEM.TryGetValue(key, out replacements);
                    if (replacements == null) continue;

                    if (replacements[0] != null && previousWord != null)
                    {
                        payloads[0] = new ActuallyRawPayload(Encoding.UTF8.GetBytes(ReplaceLastOccurrence(previousBlock, previousWord, replacements[0])));
                    }

                    if (replacements[1] != null)
                    {
                        payloads[1] = new ActuallyRawPayload(Encoding.UTF8.GetBytes(replacements[1]));
                    }

                    if (replacements[2] != null && nextWord != null)
                    {
                        payloads[2] = new ActuallyRawPayload(Encoding.UTF8.GetBytes(ReplaceLastOccurrence(nextBlock, nextWord, replacements[2])));
                    }

                    return;
                }

                return;
            }
            else if (_configuration.Gender == GenderSetting.Dumpster)
            {
                payloads[1] = new ActuallyRawPayload(Encoding.ASCII.GetBytes('[' + Convert.ToHexString(reEncode) + ']'));
                return;
            }

            bool male;
            if (_configuration.Gender == GenderSetting.Random)
                male = new Random().Next(0, 2) == 0;
            else
                male = _configuration.Gender == GenderSetting.Male;
            
            int len = male ? maleLen : femaleLen;
            int start = male ? maleStart : femaleStart;

            byte[] newTextBytes = new byte[len];
            for (int c = 0; c < newTextBytes.Length; c++)
                newTextBytes[c] = reEncode[start + c];

            payloads[1] = new ActuallyRawPayload(newTextBytes);
        }

        private Payload HandleFullNamePayload(Payload thisPayload)
        {
            byte[] reEncode = thisPayload.Encode();
            if (!ByteArrayEquals(reEncode, FullNameBytes)) return thisPayload;

            return new TextPayload(GetNameText(_configuration.FullName));
        }

        private Payload HandleFirstNamePayload(Payload thisPayload)
        {
            byte[] reEncode = thisPayload.Encode();
            if (!ByteArrayEquals(reEncode, FirstNameBytes)) return thisPayload;
            
            return new TextPayload(GetNameText(_configuration.FirstName));
        }
        
        private Payload HandleLastNamePayload(Payload thisPayload)
        {
            byte[] reEncode = thisPayload.Encode();
            if (!ByteArrayEquals(reEncode, LastNameBytes)) return thisPayload;

            return new TextPayload(GetNameText(_configuration.LastName));
        }

        private string GetNameText(NameSetting setting)
        {
            var name = _configuration.Name;
            var split = name.Split(' ');
            var first = split[0];
            var last = split[1];

            return setting switch
            {
                NameSetting.FirstLast => name,
                NameSetting.FirstOnly => first,
                NameSetting.LastOnly => last,
                NameSetting.LastFirst => $"{last} {first}",
                _ => PlayerName
            };
        }
        
        // private void ProcessGenderedParam(byte* ptr)
        // {
        //     int len = 0;
        //     byte* text2 = ptr;
        //     while (*text2 != 0) { text2++; len++; }
        //
        //     byte[] newText = new byte[len + 1];
        //     
        //     int currentPos = 0;
        //
        //     for (int i = 0; i < len; i++)
        //     {
        //         if (ptr[i] == 2 && ptr[i + 1] == 8 && ptr[i + 3] == 0xE9 && ptr[i + 4] == 5)
        //         {
        //             int codeStart = i;
        //             int codeLen = ptr[i + 2] + 2;
        //
        //             int femaleStart = codeStart + 7;
        //             int femaleLen = ptr[codeStart + 6] - 1;
        //             int maleStart = femaleStart + femaleLen + 2;
        //             int maleLen = ptr[maleStart - 1] - 1;
        //
        //             if (configuration.SelectedGender == "Male")
        //             {
        //                 for (int pos = maleStart; pos < maleStart + maleLen; pos++)
        //                 {
        //                     newText[currentPos] = ptr[pos];
        //                     currentPos++;
        //                 }
        //             }
        //             else
        //             {
        //                 for (int pos = femaleStart; pos < femaleStart + femaleLen; pos++)
        //                 {
        //                     newText[currentPos] = ptr[pos];
        //                     currentPos++;
        //                 }
        //             }
        //
        //             i += codeLen;
        //         }
        //         else
        //         {
        //             newText[currentPos] = ptr[i];
        //             currentPos++;
        //         }
        //     }
        //
        //     for (int i = 0; i < len; i++)
        //         ptr[i] = newText[i];
        // }

        public void Dispose()
        {
            _ui.Dispose();
            
            _getStringHook.Disable();
            _getStringHook.Dispose();

            _commandManager.RemoveHandler(CommandName);
            _pi.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            _ui.SettingsVisible = true;
        }

        private void DrawUI()
        {
            _ui.Draw();
        }
        
        private void DrawConfigUI()
        {
            _ui.SettingsVisible = true;
        }
    }
}
