// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Extensions.AI;

public class SelectingChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_ForwardsToSelectedCandidateAndNormalizesOptions()
    {
        // Arrange
        string expectedModelId = "gpt-4o-mini";
        var expectedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        var expectedMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "hello")
        };
        var expectedOptions = new ChatOptions();

        using var innerClient = new TestChatClient
        {
            GetResponseAsyncCallback = (messages, options, cancellationToken) =>
            {
                Assert.Same(expectedMessages, messages);
                Assert.NotSame(expectedOptions, options);
                Assert.Equal(expectedModelId, options!.ModelId);
                Assert.Equal(CancellationToken.None, cancellationToken);

                Assert.NotNull(options.AdditionalProperties);
                Assert.Equal("candidate-1", options.AdditionalProperties["auto_select.candidate_name"]?.ToString());
                Assert.Equal("openai", options.AdditionalProperties["auto_select.provider_name"]?.ToString());
                Assert.Equal(expectedModelId, options.AdditionalProperties["auto_select.model_id"]?.ToString());

                return Task.FromResult(expectedResponse);
            }
        };

        using var client = new SelectingChatClient(
            [
                new SelectingChatClientCandidate("candidate-1", innerClient, providerName: "openai", modelId: expectedModelId)
            ]);

        // Act
        ChatResponse response = await client.GetResponseAsync(expectedMessages, expectedOptions, CancellationToken.None);

        // Assert
        Assert.Same(expectedResponse, response);
    }

    [Fact]
    public async Task GetResponseAsync_PerInstanceStickiness_ReusesFirstSelectedCandidate()
    {
        var response1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var response2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var client1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(response1) };
        using var client2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(response2) };

        var candidates = new List<SelectingChatClientCandidate>
        {
            new("candidate-1", client1, providerName: "openai", modelId: "gpt-5.3"),
            new("candidate-2", client2, providerName: "openai", modelId: "gpt-4o-mini"),
        };

        int selectionCount = 0;
        using var selectingClient = new SelectingChatClient(
            candidates,
            SelectingChatClient.SelectionStickiness.PerInstance,
            selector: (_, _, allCandidates) => allCandidates[(selectionCount++) % allCandidates.Count]);

        ChatResponse first = await selectingClient.GetResponseAsync([new(ChatRole.User, "first")], new ChatOptions { ConversationId = "c1" });
        ChatResponse second = await selectingClient.GetResponseAsync([new(ChatRole.User, "second")], new ChatOptions { ConversationId = "c2" });

        Assert.Same(response1, first);
        Assert.Same(response1, second);
    }

    [Fact]
    public async Task GetResponseAsync_ByConversationIdStickiness_SticksPerConversation()
    {
        var response1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var response2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var client1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(response1) };
        using var client2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(response2) };

        var candidates = new List<SelectingChatClientCandidate>
        {
            new("candidate-1", client1, providerName: "openai", modelId: "gpt-5.3"),
            new("candidate-2", client2, providerName: "openai", modelId: "gpt-4o-mini"),
        };

        int selectionCount = 0;
        using var selectingClient = new SelectingChatClient(
            candidates,
            SelectingChatClient.SelectionStickiness.ByConversationId,
            selector: (_, _, allCandidates) => allCandidates[(selectionCount++) % allCandidates.Count]);

        ChatResponse c1First = await selectingClient.GetResponseAsync([new(ChatRole.User, "turn1")], new ChatOptions { ConversationId = "c1" });
        ChatResponse c1Second = await selectingClient.GetResponseAsync([new(ChatRole.User, "turn2")], new ChatOptions { ConversationId = "c1" });
        ChatResponse c2First = await selectingClient.GetResponseAsync([new(ChatRole.User, "turn3")], new ChatOptions { ConversationId = "c2" });

        Assert.Same(response1, c1First);
        Assert.Same(response1, c1Second);
        Assert.Same(response2, c2First);
    }

    [Fact]
    public async Task GetResponseAsync_ByConversationIdStickiness_WithoutConversationIdFallsBackToEveryCall()
    {
        var response1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var response2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var client1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(response1) };
        using var client2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(response2) };

        var candidates = new List<SelectingChatClientCandidate>
        {
            new("candidate-1", client1, providerName: "openai", modelId: "gpt-5.3"),
            new("candidate-2", client2, providerName: "openai", modelId: "gpt-4o-mini"),
        };

        int selectionCount = 0;
        using var selectingClient = new SelectingChatClient(
            candidates,
            SelectingChatClient.SelectionStickiness.ByConversationId,
            selector: (_, _, allCandidates) => allCandidates[(selectionCount++) % allCandidates.Count]);

        ChatResponse first = await selectingClient.GetResponseAsync([new(ChatRole.User, "turn1")]);
        ChatResponse second = await selectingClient.GetResponseAsync([new(ChatRole.User, "turn2")], new ChatOptions { ConversationId = "" });

        Assert.Same(response1, first);
        Assert.Same(response2, second);
    }
}
