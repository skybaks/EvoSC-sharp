﻿using EvoSC.Commands.Exceptions;
using EvoSC.Commands.Interfaces;
using EvoSC.Commands.Parser;
using EvoSC.Common.Exceptions.PlayerExceptions;
using EvoSC.Common.Interfaces;
using EvoSC.Common.Interfaces.Controllers;
using EvoSC.Common.Interfaces.Middleware;
using EvoSC.Common.Interfaces.Models;
using EvoSC.Common.Interfaces.Parsing;
using EvoSC.Common.Interfaces.Services;
using EvoSC.Common.Middleware;
using EvoSC.Common.Remote.ChatRouter;
using EvoSC.Common.TextParsing;
using EvoSC.Common.TextParsing.ValueReaders;
using EvoSC.Common.Util;
using EvoSC.Common.Util.ServerUtils;
using Microsoft.Extensions.Logging;

namespace EvoSC.Commands.Middleware;

public class CommandsMiddleware
{
    private readonly ActionDelegate _next;
    private readonly ILogger<CommandsMiddleware> _logger;
    private readonly IControllerManager _controllers;
    private readonly IServerClient _serverClient;
    private readonly IActionPipelineManager _actionPipeline;
    private readonly IPlayerManagerService _playersManager;
    private readonly ChatCommandParser _parser;

    public CommandsMiddleware(ActionDelegate next, ILogger<CommandsMiddleware> logger,
        IChatCommandManager cmdManager, IControllerManager controllers, IServerClient serverClient,
        IActionPipelineManager actionPipeline, IPlayerManagerService playersManager)
    {
        _next = next;
        _logger = logger;
        _controllers = controllers;
        _serverClient = serverClient;
        _actionPipeline = actionPipeline;
        _playersManager = playersManager;
        _parser = new ChatCommandParser(cmdManager, GetValueReader());
    }
    
    private IValueReaderManager GetValueReader()
    {
        var valueReader = new ValueReaderManager();

        valueReader.AddReader(new FloatReader());
        valueReader.AddReader(new IntegerReader());
        valueReader.AddReader(new Common.TextParsing.ValueReaders.StringReader());
        valueReader.AddReader(new OnlinePlayerReader(_playersManager));

        return valueReader;
    }

    async Task HandleUserErrorsAsync(IParserResult result, string playerLogin)
    {
        if (result.Exception is CommandParserException cmdParserException)
        {
            if (!cmdParserException.IntendedCommand)
            {
                return;
            }

            var message = $"Error: {cmdParserException.Message}";
            await _serverClient.SendChatMessageAsync($"Error: {message}", playerLogin);
        }

        if (result.Exception is PlayerNotFoundException playerNotFoundException)
        {
            await _serverClient.SendChatMessageAsync($"Error: {playerNotFoundException.Message}", playerLogin);
        }
        else
        {
            _logger.LogError(result.Exception, "Failed to parse command");
        }
    }

    private async Task ExecuteCommandAsync(IChatCommand cmd, object[] args, ChatRouterPipelineContext routerContext)
    {
        var (controller, context) = _controllers.CreateInstance(cmd.ControllerType);

        var playerInteractionContext = new CommandInteractionContext((IOnlinePlayer)routerContext.Player, context)
        {
            CommandExecuted = cmd
        };
        
        controller.SetContext(playerInteractionContext);

        var actionChain = _actionPipeline.BuildChain(PipelineType.ControllerAction, context =>
        {
            return (Task)cmd.HandlerMethod.Invoke(controller, args);
        });

        await actionChain(playerInteractionContext);
    }

    private static void CheckAliasHiding(ChatRouterPipelineContext context, IParserResult parserResult)
    {
        if (parserResult.IsIntended)
        {
            return;
        }

        if (parserResult.Command.Aliases.TryGetValue(parserResult.AliasUsed, out var alias) && alias.Hide)
        {
            context.ForwardMessage = false;
        }
    }
    
    public async Task ExecuteAsync(ChatRouterPipelineContext context)
    {
        if (context.MessageText.Trim().StartsWith("/", StringComparison.Ordinal))
        {
            context.ForwardMessage = false;
        }
        
        try
        {
            var parserResult = await _parser.ParseAsync(context.MessageText);

            if (parserResult.Success)
            {
                await ExecuteCommandAsync(parserResult.Command, parserResult.Arguments.ToArray(), context);
                CheckAliasHiding(context, parserResult);
            }
            else if (parserResult.Exception != null)
            {
                await HandleUserErrorsAsync(parserResult, context.Player.GetLogin());
            }
            else
            {
                _logger.LogError(
                    "An unknown error occured while trying to parse command. No exception thrown, but parsing failed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "A fatal error occured while trying to handle chat command");
            throw;
        }
        
        await _next(context);
    }
}
