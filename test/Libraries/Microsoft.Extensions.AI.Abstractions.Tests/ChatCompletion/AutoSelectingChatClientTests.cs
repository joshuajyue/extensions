// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Extensions.AI;

public class AutoSelectingChatClientTests
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

        using var client = new AutoSelectingChatClient(
            [
                new AutoSelectingChatClientCandidate("candidate-1", innerClient, providerName: "openai", modelId: expectedModelId)
            ]);

        // Act
        ChatResponse response = await client.GetResponseAsync(expectedMessages, expectedOptions, CancellationToken.None);

        // Assert
        Assert.Same(expectedResponse, response);
    }
}
