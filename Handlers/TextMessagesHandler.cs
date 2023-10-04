﻿using Discord;
using Discord.Webhook;
using Discord.Commands;
using Discord.WebSocket;
using CharacterEngineDiscord.Services;
using static CharacterEngineDiscord.Services.CommonService;
using static CharacterEngineDiscord.Services.IntegrationsService;
using static CharacterEngineDiscord.Services.CommandsService;
using CharacterEngineDiscord.Models.Database;
using Microsoft.Extensions.DependencyInjection;
using CharacterEngineDiscord.Models.Common;
using Microsoft.EntityFrameworkCore;
using CharacterEngineDiscord.Models.KoboldAI;
using Newtonsoft.Json.Linq;

namespace CharacterEngineDiscord.Handlers
{
    internal class TextMessagesHandler
    {
        private readonly IServiceProvider _services;
        private readonly DiscordSocketClient _client;
        private readonly IntegrationsService _integration;

        public TextMessagesHandler(IServiceProvider services)
        {
            _services = services;
            _integration = _services.GetRequiredService<IntegrationsService>();
            _client = _services.GetRequiredService<DiscordSocketClient>();

            _client.MessageReceived += (message) =>
            {
                Task.Run(async () => {
                    try { await HandleMessageAsync(message); }
                    catch (Exception e) { HandleTextMessageException(message, e); }
                });
                return Task.CompletedTask;
            };
        }

        private async Task HandleMessageAsync(SocketMessage sm)
        {
            var userMessage = sm as SocketUserMessage;
            bool invalidInput = userMessage is null || Equals(userMessage.Author.Id, _client.CurrentUser.Id) || userMessage.Content.StartsWith("~ignore");
            if (invalidInput) return;

            var context = new SocketCommandContext(_client, userMessage);
            if (context.Guild is null) return;
            if (context.Channel is not IGuildChannel guildChannel) return;
            if (context.Guild.CurrentUser.GetPermissions(guildChannel).SendMessages is false) return;

            ulong channelId;
            bool isThread = false;
            if (guildChannel is IThreadChannel tc)
            {
                channelId = tc.CategoryId ?? 0; // parent channel of the thread
                isThread = true;
            }
            else
            {
                channelId = guildChannel.Id;
            }

            var calledCharacters = await DetermineCalledCharacterWebhook(userMessage!, channelId);
            if (calledCharacters.Count == 0 || await _integration.UserIsBanned(context)) return;

            foreach (var characterWebhook in calledCharacters)
            {
                int delay = characterWebhook.ResponseDelay;
                if (context.User.IsWebhook || context.User.IsBot)
                {
                    delay = Math.Max(10, delay);
                }

                await Task.Delay(delay * 1000);
                await TryToCallCharacterAsync(characterWebhook.Id, userMessage!, isThread);
            }
        }

        private async Task TryToCallCharacterAsync(ulong characterWebhookId, SocketUserMessage userMessage, bool isThread)
        {
            var db = new StorageContext();
            
            // Prevalidations
            var characterWebhook = await db.CharacterWebhooks.FindAsync(characterWebhookId);
            if (characterWebhook is null) return;
            if (characterWebhook.IntegrationType is IntegrationType.Empty) return;

            string userName;
            if (userMessage.Author is SocketGuildUser guildUser)
                userName = guildUser.GetBestName();
            else if (userMessage.Author.IsWebhook)
                userName = userMessage.Author.Username;
            else return;

            bool canNotProceed = !await EnsureCharacterCanBeCalledAsync(characterWebhook, userMessage.Channel);
            if (canNotProceed) return;

            // Reformat message
            string text = userMessage.Content ?? "";
            if (text.StartsWith("<")) text = MentionRegex().Replace(text, "", 1);

            var formatTemplate = characterWebhook.PersonalMessagesFormat ?? characterWebhook.Channel.Guild.GuildMessagesFormat ?? ConfigFile.DefaultMessagesFormat.Value!;
            text = formatTemplate.Replace("{{user}}", $"{userName}")
                                 .Replace("{{msg}}", $"{text.RemovePrefix(characterWebhook.CallPrefix)}")
                                 .Replace("\\n", "\n")
                                 .AddRefQuote(userMessage.ReferencedMessage);

            // Get character response
            CharacterResponse characterResponse = null!;
            if (characterWebhook.IntegrationType is IntegrationType.OpenAI)
                characterResponse = await CallOpenAiCharacterAsync(characterWebhook, text);
            else if (characterWebhook.IntegrationType is IntegrationType.CharacterAI)
                characterResponse = await CallCaiCharacterAsync(characterWebhook, text);
            else if (characterWebhook.IntegrationType is IntegrationType.KoboldAI)
                return;//characterResponse = await CallKoboldAiCharacterAsync(characterWebhook, text);
            else if (characterWebhook.IntegrationType is IntegrationType.HordeKoboldAI)
                return;// characterResponse = await CallHordeKoboldAiCharacterAsync(characterWebhook, text);

            if (characterResponse.IsFailure)
            {
                await userMessage.ReplyAsync(embed: characterResponse.Text.ToInlineEmbed(Color.Red));
                return;
            }

            var messageId = TryToSendCharacterMessageAsync(characterWebhook, characterResponse, userMessage, userName, isThread);
            TryToRemoveButtons(characterWebhook.LastCharacterDiscordMsgId, userMessage.Channel);

            characterWebhook.CurrentSwipeIndex = 0;
            characterWebhook.LastCharacterMsgId = characterResponse.CharacterMessageId;
            characterWebhook.LastUserMsgId = characterResponse.UserMessageId;
            characterWebhook.LastDiscordUserCallerId = userMessage.Author.Id;
            characterWebhook.LastCallTime = DateTime.UtcNow;
            characterWebhook.MessagesSent++;
            characterWebhook.Channel.Guild.MessagesSent++;
            characterWebhook.LastCharacterDiscordMsgId = await messageId;
            
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException e)
            {
                foreach(var entry in e.Entries)
                    entry.Reload();

                db.SaveChanges();
            }
        }

        
        /// <returns>Message ID or 0 if sending failed</returns>
        private async Task<ulong> TryToSendCharacterMessageAsync(CharacterWebhook characterWebhook, CharacterResponse characterResponse, SocketUserMessage userMessage, string userName, bool isThread)
        {
            _integration.Conversations.TryAdd(characterWebhook.Id, new(new(), DateTime.UtcNow));
            var convo = _integration.Conversations[characterWebhook.Id];

            lock (convo)
            {
                // Forget all choises from the last message and remember a new one
                convo.AvailableMessages.Clear();
                convo.AvailableMessages.Add(new()
                {
                    Text = characterResponse.Text,
                    MessageId = characterResponse.CharacterMessageId,
                    ImageUrl = characterResponse.ImageRelPath,
                    TokensUsed = characterResponse.TokensUsed
                });
                convo.LastUpdated = DateTime.UtcNow;
            }

            var webhookClient = _integration.GetWebhookClient(characterWebhook.Id, characterWebhook.WebhookToken);
            if (webhookClient is null)
            {
                await userMessage.Channel.SendMessageAsync(embed: $"{WARN_SIGN_DISCORD} Failed to send character message: `channel webhook was not found`".ToInlineEmbed(Color.Red));
                return 0;
            }

            // Reformat message
            string characterMessage = characterResponse.Text.Replace("{{user}}", $"**{userName}**");
            characterMessage = $"{(userMessage.Author.IsWebhook ? $"**{userMessage.Author.Username}**," : userMessage.Author.Mention)} {characterMessage}";

            // Cut if too long
            if (characterMessage.Length > 2000)
                characterMessage = characterMessage[0..1994] + "[...]";

            // Fill embeds
            List<Embed>? embeds = new();

            if (characterWebhook.ReferencesEnabled && !string.IsNullOrWhiteSpace(userMessage.Content))
            {
                int l = Math.Min(userMessage.Content.Length, 50);
                string quote = userMessage.Content;
                if (quote.StartsWith("<")) quote = MentionRegex().Replace(quote, "", 1);

                embeds.Add(new EmbedBuilder().WithFooter($"> {quote[0..l]}{(l == 50 ? "..." : "")}").Build());
            }

            if (characterResponse.ImageRelPath is not null)
            {
                bool canGetImage = await ImageIsAvailable(characterResponse.ImageRelPath, _integration.ImagesHttpClient);
                if (canGetImage) embeds.Add(new EmbedBuilder().WithImageUrl(characterResponse.ImageRelPath).Build());
            }

            // Sending message
            try
            {
                ulong messageId;
                if (isThread)
                    messageId = await webhookClient.SendMessageAsync(characterMessage, embeds: embeds, threadId: userMessage.Channel.Id);
                else
                    messageId = await webhookClient.SendMessageAsync(characterMessage, embeds: embeds);

                _integration.MessagesSent++;
                bool isUserMessage = !(userMessage.Author.IsWebhook || userMessage.Author.IsBot);
                if (isUserMessage) await TryToAddButtonsAsync(characterWebhook, userMessage.Channel, messageId);

                return messageId;
            }
            catch (Exception e)
            {
                await userMessage.Channel.SendMessageAsync(embed: $"{WARN_SIGN_DISCORD} Failed to send character message: `{e.Message}`".ToInlineEmbed(Color.Red));
                return 0;
            }
        }


        // Calls

        private async Task<CharacterResponse> CallCaiCharacterAsync(CharacterWebhook cw, string text)
        {
            var caiToken = cw.Channel.Guild.GuildCaiUserToken ?? ConfigFile.DefaultCaiUserAuthToken.Value;
            var plusMode = cw.Channel.Guild.GuildCaiPlusMode ?? ConfigFile.DefaultCaiPlusMode.Value.ToBool();
            var caiResponse = await _integration.CaiClient!.CallCharacterAsync(cw.Character.Id, cw.Character.Tgt!, cw.ActiveHistoryID!, text, primaryMsgUuId: cw.LastCharacterMsgId, customAuthToken: caiToken, customPlusMode: plusMode);

            string message;
            bool success;

            if (!caiResponse.IsSuccessful)
            {
                message = $"{WARN_SIGN_DISCORD} Failed to fetch character response: ```\n{caiResponse.ErrorReason}\n```";
                success = false;
            }
            else
            {
                message = caiResponse.Response!.Text;
                success = true;
            }

            return new()
            {
                Text = message,
                IsSuccessful = success,
                CharacterMessageId = caiResponse.Response?.UuId,
                ImageRelPath = caiResponse.Response?.ImageRelPath,
                UserMessageId = caiResponse.LastUserMsgUuId,
                TokensUsed = 0,
            };
        }

        private async Task<CharacterResponse> CallOpenAiCharacterAsync(CharacterWebhook cw, string text)
        {            
            cw.StoredHistoryMessages.Add(new() { Role = "user", Content = text, CharacterWebhookId = cw.Id }); // remember user message (will be included in payload)
            var openAiRequestParams = BuildChatOpenAiRequestPayload(cw);

            if (openAiRequestParams.Messages.Count < 2)
            {
                return new()
                {
                    Text = $"{WARN_SIGN_DISCORD} Failed to fetch character response: `Your message couldn't fit in the max token limit`",
                    TokensUsed = 0,
                    IsSuccessful = false
                };
            }

            string message;
            bool success;
            int tokens = 0;
            string? charMsgId = null;

            var openAiResponse = await SendOpenAiRequestAsync(openAiRequestParams, _integration.CommonHttpClient);

            if (openAiResponse is null || openAiResponse.IsFailure || openAiResponse.Message.IsEmpty())
            {
                string desc = (openAiResponse?.ErrorReason is null || openAiResponse.ErrorReason.Contains("IP")) ? "Something went wrong" : openAiResponse.ErrorReason;
                message = $"{WARN_SIGN_DISCORD} Failed to fetch character response: `{desc}`";
                success = false;
            }
            else
            {
                // Remember character message
                cw.StoredHistoryMessages.Add(new() { Role = "assistant", Content = openAiResponse.Message!, CharacterWebhookId = cw.Id });
                cw.LastRequestTokensUsage = openAiResponse.Usage ?? 0;

                // Clear old messages, 80-100 is a good balance between response speed and needed context size, also it's usually pretty close to the GPT-3.5 token limit
                if (cw.StoredHistoryMessages.Count > 100)
                    cw.StoredHistoryMessages.RemoveRange(0, 20);

                message = openAiResponse.Message!;
                success = true;
                tokens = openAiResponse.Usage ?? 0;
                charMsgId = openAiResponse.MessageId;
            }

            return new()
            {
                Text = message,
                TokensUsed = tokens,
                CharacterMessageId = charMsgId,
                IsSuccessful = success
            };
        }

        private async Task<CharacterResponse> CallKoboldAiCharacterAsync(CharacterWebhook cw, string text)
        {
            cw.StoredHistoryMessages.Add(new() { Role = "\nUser: ", Content = text, CharacterWebhookId = cw.Id }); // remember user message (will be included in payload)
            var koboldAiRequestParams = BuildKoboldAiRequestPayload(cw);

            if (koboldAiRequestParams.Messages.Count < 2)
            {
                return new()
                {
                    Text = $"{WARN_SIGN_DISCORD} Failed to fetch character response: `Your message couldn't fit in the max token limit`",
                    TokensUsed = 0,
                    IsSuccessful = false
                };
            }

            string message;
            bool success;

            var kobkoldAiResponse = await SendKoboldAiRequestAsync(koboldAiRequestParams, _integration.CommonHttpClient, continueRequest: false);

            if (kobkoldAiResponse is null || kobkoldAiResponse.IsFailure || kobkoldAiResponse.Message.IsEmpty())
            {
                string desc = kobkoldAiResponse?.ErrorReason ?? "Something went wrong";
                message = $"{WARN_SIGN_DISCORD} Failed to fetch character response: `{desc}`";
                success = false;
            }
            else
            {   // Remember character message
                cw.StoredHistoryMessages.Add(new() { Role = "\nYou: ", Content = kobkoldAiResponse.Message!, CharacterWebhookId = cw.Id });

                if (cw.StoredHistoryMessages.Count > 100)
                    cw.StoredHistoryMessages.RemoveRange(0, 20);

                message = kobkoldAiResponse.Message!;
                success = true;
            }

            return new()
            {
                Text = message,
                IsSuccessful = success,
                TokensUsed = 0
            };
        }

        private async Task<CharacterResponse> CallHordeKoboldAiCharacterAsync(CharacterWebhook cw, string text)
        {
            cw.StoredHistoryMessages.Add(new() { Role = "\nUser: ", Content = text, CharacterWebhookId = cw.Id }); // remember user message (will be included in payload)
            var hordeRequestParams = BuildHordeKoboldAiRequestPayload(cw);

            if (hordeRequestParams.KoboldAiSettings.Messages.Count < 2)
            {
                return new()
                {
                    Text = $"{WARN_SIGN_DISCORD} Failed to fetch character response: `Your message couldn't fit in the max token limit`",
                    TokensUsed = 0,
                    IsSuccessful = false
                };
            }

            string message;
            bool success;

            var hordeResponse = await SendHordeKoboldAiRequestAsync(hordeRequestParams, _integration.CommonHttpClient, continueRequest: false);

            if (hordeResponse is null || hordeResponse.IsFailure || hordeResponse.MessageId.IsEmpty())
            {
                string desc = hordeResponse?.ErrorReason ?? "Something went wrong";
                message = $"{WARN_SIGN_DISCORD} Failed to fetch character response: `{desc}`";
                success = false;
            }
            else
            {   // Remember character message
                var hordeResult = await TryToAwaitForHordeRequestResultAsync(hordeResponse.MessageId, _integration.CommonHttpClient, 0);
                if (hordeResult.IsSuccessful)
                {
                    cw.StoredHistoryMessages.Add(new() { Role = "\nYou: ", Content = hordeResult.Message!.Value.Content, CharacterWebhookId = cw.Id });

                    if (cw.StoredHistoryMessages.Count > 100)
                        cw.StoredHistoryMessages.RemoveRange(0, 20);

                    message = hordeResult.Message.Value.Content;
                    success = true;
                }
                else
                {
                    message = hordeResult.ErrorReason!;
                    success = false;
                }
            }

            return new()
            {
                Text = message,
                IsSuccessful = success,
                TokensUsed = 0
            };
        }

        private static async Task<HordeKoboldAiResult> TryToAwaitForHordeRequestResultAsync(string? messageId, HttpClient httpClient, int attemptCount)
        {
            string url = $"https://horde.koboldai.net/api/v2/generate/text/status/{messageId}";

            try
            {
                var response = await httpClient.GetAsync(url);
                string content = await response.Content.ReadAsStringAsync();
                dynamic contentParsed = content.ToDynamicJsonString()!;

                if ($"{contentParsed.done}".ToBool())
                {
                    var generations = (JArray)contentParsed.generations;
                    string text = (generations.First() as dynamic).text;

                    return new()
                    {
                        IsSuccessful = true,
                        Message = new("\nYou: ", text)
                    };
                }
                else if (!$"{contentParsed.is_possible}".ToBool() || $"{contentParsed.faulted}".ToBool())
                {
                    return new()
                    {
                        IsSuccessful = false,
                        ErrorReason = "Request failed. Try again later or change the model."
                    };
                }
                else
                {
                    if (attemptCount > 20) // 2 min max
                    {
                        return new()
                        {
                            IsSuccessful = false,
                            ErrorReason = "Timed out"
                        };
                    }
                    else
                    {
                        await Task.Delay(6000);
                        return await TryToAwaitForHordeRequestResultAsync(messageId, httpClient, attemptCount + 1);
                    }
                }
            }
            catch
            {
                return new()
                {
                    IsSuccessful = false,
                    ErrorReason = "Something went wrong"
                };
            }
        }


        // Ensure

        private static async Task<bool> EnsureCharacterCanBeCalledAsync(CharacterWebhook characterWebhook, ISocketMessageChannel channel)
        {
            bool result;
            if (characterWebhook.IntegrationType is IntegrationType.CharacterAI)
                result = await EnsureCaiCharacterCanBeCalledAsync(characterWebhook, channel);
            else if (characterWebhook.IntegrationType is IntegrationType.OpenAI)
                result = await EnsureOpenAiCharacterCanBeCalledAsync(characterWebhook, channel);
            else if (characterWebhook.IntegrationType is IntegrationType.KoboldAI)
                result = await EnsureKoboldAiCharacterCanBeCalledAsync(characterWebhook, channel);
            else if (characterWebhook.IntegrationType is IntegrationType.HordeKoboldAI)
                result = await EnsureHordeKoboldAiCharacterCanBeCalledAsync(characterWebhook, channel);
            else
            {
                await channel.SendMessageAsync(embed: $"{WARN_SIGN_DISCORD} You have to set a backend API for this integration. Use `/update set-api` command.".ToInlineEmbed(Color.Orange));
                result = false;
            }

            return result;
        }

        private static async Task<bool> EnsureCaiCharacterCanBeCalledAsync(CharacterWebhook cw, ISocketMessageChannel channel)
        {
            if (!ConfigFile.CaiEnabled.Value.ToBool())
            {
                await channel.SendMessageAsync(embed: $"{WARN_SIGN_DISCORD} CharacterAI integration is not available".ToInlineEmbed(Color.Red));
                return false;
            }

            var caiToken = cw.Channel.Guild.GuildCaiUserToken ?? ConfigFile.DefaultCaiUserAuthToken.Value;

            if (string.IsNullOrWhiteSpace(caiToken))
            {
                await channel.SendMessageAsync(embed: $"{WARN_SIGN_DISCORD} You have to specify a CharacterAI auth token for your server first!".ToInlineEmbed(Color.Red));
                return false;
            }

            return true;
        }

        private static async Task<bool> EnsureOpenAiCharacterCanBeCalledAsync(CharacterWebhook cw, ISocketMessageChannel channel)
        {
            string? openAiToken = cw.PersonalApiToken ?? cw.Channel.Guild.GuildOpenAiApiToken ?? ConfigFile.DefaultOpenAiApiToken.Value;

            if (string.IsNullOrWhiteSpace(openAiToken))
            {
                await channel.SendMessageAsync(embed: $"{WARN_SIGN_DISCORD} You have to specify an OpenAI API token for your server first!".ToInlineEmbed(Color.Red));
                return false;
            }

            return true;
        }

        private static async Task<bool> EnsureKoboldAiCharacterCanBeCalledAsync(CharacterWebhook cw, ISocketMessageChannel channel)
        {
            string? koboldAiApiEndpoint = cw.PersonalApiEndpoint ?? cw.Channel.Guild.GuildKoboldAiApiEndpoint;

            if (string.IsNullOrWhiteSpace(koboldAiApiEndpoint))
            {
                await channel.SendMessageAsync(embed: $"{WARN_SIGN_DISCORD} You have to specify an KoboldAI API endpoint for your server first!".ToInlineEmbed(Color.Red));
                return false;
            }

            return true;
        }

        private static async Task<bool> EnsureHordeKoboldAiCharacterCanBeCalledAsync(CharacterWebhook cw, ISocketMessageChannel channel)
        {
            string? hordeApiToken = cw.PersonalApiToken ?? cw.Channel.Guild.GuildHordeApiToken;

            if (string.IsNullOrWhiteSpace(hordeApiToken))
            {
                await channel.SendMessageAsync(embed: $"{WARN_SIGN_DISCORD} You have to specify an KoboldAI API endpoint for your server first!".ToInlineEmbed(Color.Red));
                return false;
            }

            return true;
        }


        // Other

        private static readonly Random @Random = new();
        private static async Task<List<CharacterWebhook>> DetermineCalledCharacterWebhook(SocketUserMessage userMessage, ulong channelId)
        {
            List<CharacterWebhook> characterWebhooks = new();

            var db = new StorageContext();
            var channel = await db.Channels.FindAsync(channelId);

            if (channel is null || channel.CharacterWebhooks.Count == 0)
            {
                return characterWebhooks;
            }

            var text = userMessage.Content.Trim();
            var rm = userMessage.ReferencedMessage;
            var withRefMessage = rm is not null && rm.Author.IsWebhook;
            var chance = (float)(@Random.Next(99) + 0.001 + @Random.NextDouble());

            // Try to find one certain character that was called by a prefix
            var cw = channel.CharacterWebhooks.FirstOrDefault(w => text.StartsWith(w.CallPrefix));

            if (cw is not null)
            {
                characterWebhooks.Add(cw);
            }
            else if (withRefMessage) // or find some other that was called by a reply
            {
                cw = channel.CharacterWebhooks.Find(cw => cw.Id == rm!.Author.Id);
                if (cw is not null) characterWebhooks.Add(cw);
            }

            // Add characters who hunt the userchrome://vivaldi-webui/startpage?section=Speed-dials&background-color=#23234f
            var hunters = channel.CharacterWebhooks.Where(w => w.HuntedUsers.Any(h => h.UserId == userMessage.Author.Id && h.Chance > chance)).ToList();
            if (hunters is not null && hunters.Count > 0)
            {
                foreach (var h in hunters)
                    if (!characterWebhooks.Contains(h)) characterWebhooks.Add(h);
            }

            // Add some random character (1) by channel's random reply chance
            if (channel.RandomReplyChance > chance)
            {
                var characters = channel.CharacterWebhooks.Where(w => w.Id != userMessage.Author.Id).ToList();
                if (characters.Count > 0)
                {
                    var someRandomCharacter = characters[@Random.Next(characters.Count)];
                    if (!characterWebhooks.Contains(someRandomCharacter))
                        characterWebhooks.Add(someRandomCharacter);
                }
            }

            // Add certain random characters by their personal random reply chance
            var randomCharacters = channel.CharacterWebhooks.Where(w => w.Id != userMessage.Author.Id && w.ReplyChance > chance).ToList();
            if (randomCharacters.Count > 0)
            {
                foreach (var rc in randomCharacters)
                    if (!characterWebhooks.Contains(rc)) characterWebhooks.Add(rc);
            }

            return characterWebhooks;
        }

        private static async Task TryToAddButtonsAsync(CharacterWebhook characterWebhook, ISocketMessageChannel channel, ulong messageId)
        {
            try
            {
                var message = await channel.GetMessageAsync(messageId);

                if (characterWebhook.SwipesEnabled)
                {
                    await message.AddReactionAsync(ARROW_LEFT);
                    await message.AddReactionAsync(ARROW_RIGHT);
                }
                if (characterWebhook.CrutchEnabled)
                {
                    await message.AddReactionAsync(CRUTCH_BTN);
                }
            }
            catch
            {
                await channel.SendMessageAsync(embed: $"{WARN_SIGN_DISCORD} Failed to add reaction-buttons to the character message.\nMake sure that bot has permission to add reactions in this channel, or disable this feature with `/update toggle-swipes enable:false` command.".ToInlineEmbed(Color.Red));
            }
        }

        private void TryToRemoveButtons(ulong oldMessageId, ISocketMessageChannel channel)
        {
            Task.Run(async () =>
            {
                if (oldMessageId == 0) return;

                var oldMessage = await channel.GetMessageAsync(oldMessageId);
                if (oldMessage is null) return;

                var btns = new Emoji[] { ARROW_LEFT, ARROW_RIGHT, CRUTCH_BTN };
                await Parallel.ForEachAsync(btns, async (btn, ct)
                    => await oldMessage.RemoveReactionAsync(btn, _client.CurrentUser));
            });
        }

        private void HandleTextMessageException(SocketMessage message, Exception e)
        {
            LogException(new[] { e });

            if (e.Message.Contains("Missing Permissions")) return;

            var channel = message.Channel as SocketGuildChannel;
            var guild = channel?.Guild;
            TryToReportInLogsChannel(_client, title: "Message Exception",
                                              desc: $"Guild: `{guild?.Name} ({guild?.Id})`\n" +
                                                    $"Owner: `{guild?.Owner.GetBestName()} ({guild?.Owner.Username})`\n" +
                                                    $"Channel: `{channel?.Name} ({channel?.Id})`\n" +
                                                    $"User: {message.Author.Username}" + (message.Author.IsWebhook ? " (webhook)" : message.Author.IsBot ? " (bot)" : "") +
                                                    $"\nMessage: {message.Content[0..Math.Min(message.Content.Length, 1000)]}",
                                              content: e.ToString(),
                                              color: Color.Red,
                                              error: true);
        }
    }
}
