// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Microsoft.Extensions.AI;

public class ChatModelCatalogTests
{
    private const string SampleLiteLlmCatalog = """
        {
          "sample_spec": { "mode": "chat", "litellm_provider": "spec" },
          "gpt-4o": {
            "litellm_provider": "openai",
            "mode": "chat",
            "max_input_tokens": 128000,
            "max_output_tokens": 16384,
            "input_cost_per_token": 0.0000025,
            "output_cost_per_token": 0.00001,
            "supports_function_calling": true,
            "supports_vision": true,
            "supports_response_schema": true,
            "deprecation_date": "2025-12-01",
            "source": "https://platform.openai.com/docs/pricing"
          },
          "o1-mini": {
            "litellm_provider": "openai",
            "mode": "chat",
            "input_cost_per_token": 0.0000011,
            "output_cost_per_token": 0.0000044,
            "supports_reasoning": true,
            "supports_function_calling": false
          },
          "text-embedding-3-small": {
            "litellm_provider": "openai",
            "mode": "embedding",
            "input_cost_per_token": 0.00000002
          }
        }
        """;

    private const string SampleGitHubModelsCatalog = """
        [
          {
            "id": "openai/gpt-5-mini",
            "name": "OpenAI GPT-5 mini",
            "publisher": "OpenAI",
            "summary": "Compact reasoning model.",
            "rate_limit_tier": "custom",
            "supported_input_modalities": ["text", "image"],
            "supported_output_modalities": ["text"],
            "tags": ["reasoning", "coding"],
            "registry": "azure-openai",
            "version": "2025-01-01",
            "capabilities": ["reasoning", "tool-calling", "streaming"],
            "limits": { "max_input_tokens": 200000, "max_output_tokens": 100000 },
            "html_url": "https://github.com/marketplace/models/azure-openai/gpt-5-mini"
          },
          {
            "id": "meta/llama-3",
            "name": "Llama 3",
            "publisher": "Meta",
            "supported_input_modalities": ["text"],
            "supported_output_modalities": ["text"],
            "capabilities": ["streaming"],
            "limits": { "max_input_tokens": 8192, "max_output_tokens": 4096 }
          },
          {
            "id": "openai/text-embedding-3-large",
            "name": "OpenAI Text Embedding 3 Large",
            "publisher": "OpenAI",
            "supported_input_modalities": ["text"],
            "supported_output_modalities": ["embeddings"],
            "capabilities": [],
            "rate_limit_tier": "embeddings",
            "limits": { "max_input_tokens": 8191, "max_output_tokens": null }
          }
        ]
        """;

    // ----- LiteLLM parser -----

    [Fact]
    public void ParseLiteLlm_NullJson_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ChatModelCatalog.ParseLiteLlm(null!));
    }

    [Fact]
    public void ParseLiteLlm_SkipsSampleSpec_AndNonChat_ByDefault()
    {
        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.ParseLiteLlm(SampleLiteLlmCatalog);

        Assert.Equal(2, models.Count);
        Assert.Contains(models, m => m.Name == "gpt-4o");
        Assert.Contains(models, m => m.Name == "o1-mini");
        Assert.DoesNotContain(models, m => m.Name == "sample_spec");
        Assert.DoesNotContain(models, m => m.Name == "text-embedding-3-small");
    }

    [Fact]
    public void ParseLiteLlm_MapsCapabilityTraits()
    {
        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.ParseLiteLlm(SampleLiteLlmCatalog);

        RoutingChatModel gpt4o = Get(models, "gpt-4o");
        Assert.True(gpt4o.Traits.HasFlag(RoutingChatModelTraits.ToolCalling));
        Assert.True(gpt4o.Traits.HasFlag(RoutingChatModelTraits.Vision));
        Assert.False(gpt4o.Traits.HasFlag(RoutingChatModelTraits.Reasoning));

        RoutingChatModel o1 = Get(models, "o1-mini");
        Assert.True(o1.Traits.HasFlag(RoutingChatModelTraits.Reasoning));
        Assert.False(o1.Traits.HasFlag(RoutingChatModelTraits.ToolCalling));
        Assert.False(o1.Traits.HasFlag(RoutingChatModelTraits.Vision));
    }

    [Fact]
    public void ParseLiteLlm_NeverInfersLatencyOrQuality()
    {
        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.ParseLiteLlm(SampleLiteLlmCatalog);

        foreach (RoutingChatModel model in models)
        {
            Assert.Null(model.TypicalLatency);
        }
    }

    [Fact]
    public void ParseLiteLlm_ConvertsPerTokenCostToPerMillion()
    {
        RoutingChatModel gpt4o = Get(ChatModelCatalog.ParseLiteLlm(SampleLiteLlmCatalog), "gpt-4o");

        Assert.Equal(2.5m, gpt4o.InputTokenCostPerMillion);
        Assert.Equal(10m, gpt4o.OutputTokenCostPerMillion);
    }

    [Fact]
    public void ParseLiteLlm_CarriesObjectiveMetadata()
    {
        RoutingChatModel gpt4o = Get(ChatModelCatalog.ParseLiteLlm(SampleLiteLlmCatalog), "gpt-4o");

        Assert.Equal("openai", gpt4o.ProviderName);
        Assert.Equal("gpt-4o", gpt4o.ModelId);
        Assert.Equal(128_000, gpt4o.MaxInputTokens);
        Assert.Equal(new Uri("https://platform.openai.com/docs/pricing"), gpt4o.SourceUri);

        AdditionalPropertiesDictionary props = Assert.IsType<AdditionalPropertiesDictionary>(gpt4o.AdditionalProperties);
        Assert.Equal(16_384L, props[ChatModelCatalog.LiteLlmMaxOutputTokensMetadataKey]);
        Assert.Equal("2025-12-01", props[ChatModelCatalog.LiteLlmDeprecationDateMetadataKey]);
        Assert.True((bool)props[ChatModelCatalog.LiteLlmMetadataKeyPrefix + "supports_response_schema"]!);
    }

    [Fact]
    public void ParseLiteLlm_LeavesMaxInputTokensNull_WhenAbsent()
    {
        RoutingChatModel o1 = Get(ChatModelCatalog.ParseLiteLlm(SampleLiteLlmCatalog), "o1-mini");

        Assert.Null(o1.MaxInputTokens);
    }

    [Fact]
    public void ParseLiteLlm_IncludesEmbeddings_WhenChatModelsOnlyFalse()
    {
        IReadOnlyList<RoutingChatModel> models =
            ChatModelCatalog.ParseLiteLlm(SampleLiteLlmCatalog, new ChatModelCatalogOptions { ChatModelsOnly = false });

        Assert.Equal(3, models.Count);
        Assert.Contains(models, m => m.Name == "text-embedding-3-small");
    }

    [Fact]
    public void ParseLiteLlm_AppliesIncludeModelFilter()
    {
        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.ParseLiteLlm(
            SampleLiteLlmCatalog,
            new ChatModelCatalogOptions { IncludeModel = name => name.StartsWith("gpt", StringComparison.Ordinal) });

        RoutingChatModel only = Assert.Single(models);
        Assert.Equal("gpt-4o", only.Name);
    }

    [Fact]
    public void ParseLiteLlm_AppliesUpdatedAtProvenance()
    {
        var updatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        IReadOnlyList<RoutingChatModel> models =
            ChatModelCatalog.ParseLiteLlm(SampleLiteLlmCatalog, new ChatModelCatalogOptions { UpdatedAt = updatedAt });

        Assert.All(models, m => Assert.Equal(updatedAt, m.UpdatedAt));
    }

    [Fact]
    public void ParseLiteLlm_DeduplicatesByNameCaseInsensitive()
    {
        const string Json = """
            {
              "Model-X": { "mode": "chat", "litellm_provider": "p", "input_cost_per_token": 0.000001 },
              "model-x": { "mode": "chat", "litellm_provider": "p", "input_cost_per_token": 0.000002 }
            }
            """;

        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.ParseLiteLlm(Json);

        RoutingChatModel only = Assert.Single(models);
        Assert.Equal("Model-X", only.Name);
        Assert.Equal(1m, only.InputTokenCostPerMillion);
    }

    [Fact]
    public void LoadLiteLlm_Stream_ProducesSameResultAsParse()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleLiteLlmCatalog));

        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.LoadLiteLlm(stream);

        Assert.Equal(2, models.Count);
    }

    [Fact]
    public void ParseLiteLlm_ResultBindsIntoCatalogAndClient()
    {
        var catalog = new RoutingChatModelCatalog(ChatModelCatalog.ParseLiteLlm(SampleLiteLlmCatalog));
        using var client = new TestChatClient();

        RoutingChatModel bound = catalog.CreateModel("gpt-4o", client);

        Assert.Same(client, bound.Client);
        Assert.Equal("openai", bound.ProviderName);
        Assert.True(bound.Traits.HasFlag(RoutingChatModelTraits.Vision));
    }

    // ----- GitHub Models parser -----

    [Fact]
    public void ParseGitHubModels_NullJson_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ChatModelCatalog.ParseGitHubModels(null!));
    }

    [Fact]
    public void ParseGitHubModels_SkipsEmbeddings_ByDefault()
    {
        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.ParseGitHubModels(SampleGitHubModelsCatalog);

        Assert.Equal(2, models.Count);
        Assert.Contains(models, m => m.Name == "gpt-5-mini");
        Assert.Contains(models, m => m.Name == "llama-3");
        Assert.DoesNotContain(models, m => m.Name == "text-embedding-3-large");
    }

    [Fact]
    public void ParseGitHubModels_StripsPublisherPrefixForName_KeepsFullModelId()
    {
        RoutingChatModel gpt5 = Get(ChatModelCatalog.ParseGitHubModels(SampleGitHubModelsCatalog), "gpt-5-mini");

        Assert.Equal("gpt-5-mini", gpt5.Name);
        Assert.Equal("openai/gpt-5-mini", gpt5.ModelId);
        Assert.Equal("OpenAI", gpt5.ProviderName);
    }

    [Fact]
    public void ParseGitHubModels_MapsCapabilityAndModalityTraits()
    {
        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.ParseGitHubModels(SampleGitHubModelsCatalog);

        RoutingChatModel gpt5 = Get(models, "gpt-5-mini");
        Assert.True(gpt5.Traits.HasFlag(RoutingChatModelTraits.ToolCalling));
        Assert.True(gpt5.Traits.HasFlag(RoutingChatModelTraits.Reasoning));
        Assert.True(gpt5.Traits.HasFlag(RoutingChatModelTraits.Vision));

        RoutingChatModel llama = Get(models, "llama-3");
        Assert.Equal(RoutingChatModelTraits.None, llama.Traits);
    }

    [Fact]
    public void ParseGitHubModels_MapsMaxInputTokens()
    {
        RoutingChatModel gpt5 = Get(ChatModelCatalog.ParseGitHubModels(SampleGitHubModelsCatalog), "gpt-5-mini");

        Assert.Equal(200_000, gpt5.MaxInputTokens);
    }

    [Fact]
    public void ParseGitHubModels_NeverInfersCostOrLatency()
    {
        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.ParseGitHubModels(SampleGitHubModelsCatalog);

        foreach (RoutingChatModel model in models)
        {
            Assert.Null(model.InputTokenCostPerMillion);
            Assert.Null(model.OutputTokenCostPerMillion);
            Assert.Null(model.TypicalLatency);
        }
    }

    [Fact]
    public void ParseGitHubModels_CarriesObjectiveMetadata()
    {
        RoutingChatModel gpt5 = Get(ChatModelCatalog.ParseGitHubModels(SampleGitHubModelsCatalog), "gpt-5-mini");

        Assert.Equal(new Uri("https://github.com/marketplace/models/azure-openai/gpt-5-mini"), gpt5.SourceUri);

        AdditionalPropertiesDictionary props = Assert.IsType<AdditionalPropertiesDictionary>(gpt5.AdditionalProperties);
        Assert.Equal("openai/gpt-5-mini", props[ChatModelCatalog.GitHubModelsMetadataKeyPrefix + "id"]);
        Assert.Equal("custom", props[ChatModelCatalog.GitHubModelsMetadataKeyPrefix + "rate_limit_tier"]);
        Assert.Equal("reasoning,coding", props[ChatModelCatalog.GitHubModelsMetadataKeyPrefix + "tags"]);
        Assert.Equal("text,image", props[ChatModelCatalog.GitHubModelsMetadataKeyPrefix + "supported_input_modalities"]);
        Assert.Equal(100_000L, props[ChatModelCatalog.GitHubModelsMaxOutputTokensMetadataKey]);
    }

    [Fact]
    public void ParseGitHubModels_IncludesEmbeddings_WhenChatModelsOnlyFalse()
    {
        IReadOnlyList<RoutingChatModel> models =
            ChatModelCatalog.ParseGitHubModels(SampleGitHubModelsCatalog, new ChatModelCatalogOptions { ChatModelsOnly = false });

        Assert.Equal(3, models.Count);
        Assert.Contains(models, m => m.Name == "text-embedding-3-large");
    }

    [Fact]
    public void ParseGitHubModels_AppliesIncludeModelFilterByBareName()
    {
        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.ParseGitHubModels(
            SampleGitHubModelsCatalog,
            new ChatModelCatalogOptions { IncludeModel = name => name.StartsWith("gpt", StringComparison.Ordinal) });

        RoutingChatModel only = Assert.Single(models);
        Assert.Equal("gpt-5-mini", only.Name);
    }

    [Fact]
    public void LoadGitHubModels_Stream_ProducesSameResultAsParse()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleGitHubModelsCatalog));

        IReadOnlyList<RoutingChatModel> models = ChatModelCatalog.LoadGitHubModels(stream);

        Assert.Equal(2, models.Count);
    }

    [Fact]
    public void ParseGitHubModels_WrongRootKind_Throws()
    {
        Assert.Throws<ArgumentException>(() => ChatModelCatalog.ParseGitHubModels("{}"));
    }

    // ----- Cross-catalog merge -----

    [Fact]
    public void BothCatalogs_ResolveSharedModelToSameName()
    {
        const string LiteLlm = """
            {
              "gpt-5-mini": {
                "litellm_provider": "openai",
                "mode": "chat",
                "input_cost_per_token": 0.00000025,
                "output_cost_per_token": 0.000002
              }
            }
            """;

        RoutingChatModel fromLiteLlm = Get(ChatModelCatalog.ParseLiteLlm(LiteLlm), "gpt-5-mini");
        RoutingChatModel fromGitHub = Get(ChatModelCatalog.ParseGitHubModels(SampleGitHubModelsCatalog), "gpt-5-mini");

        // Same merge key across both catalogs.
        Assert.Equal(fromLiteLlm.Name, fromGitHub.Name);

        // LiteLLM contributes pricing; GitHub Models contributes capabilities + context window.
        Assert.NotNull(fromLiteLlm.InputTokenCostPerMillion);
        Assert.Null(fromGitHub.InputTokenCostPerMillion);
        Assert.True(fromGitHub.Traits.HasFlag(RoutingChatModelTraits.Reasoning));
        Assert.Equal(200_000, fromGitHub.MaxInputTokens);
    }

    private static RoutingChatModel Get(IReadOnlyList<RoutingChatModel> models, string name)
    {
        foreach (RoutingChatModel model in models)
        {
            if (model.Name == name)
            {
                return model;
            }
        }

        throw new KeyNotFoundException(name);
    }
}
