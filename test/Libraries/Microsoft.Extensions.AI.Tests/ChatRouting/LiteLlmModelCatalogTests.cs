// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Microsoft.Extensions.AI;

public class LiteLlmModelCatalogTests
{
    private const string SampleCatalog = """
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

    [Fact]
    public void Parse_NullJson_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LiteLlmModelCatalog.Parse(null!));
    }

    [Fact]
    public void Parse_SkipsSampleSpec_AndNonChat_ByDefault()
    {
        IReadOnlyList<RoutingChatModel> models = LiteLlmModelCatalog.Parse(SampleCatalog);

        Assert.Equal(2, models.Count);
        Assert.Contains(models, m => m.Name == "gpt-4o");
        Assert.Contains(models, m => m.Name == "o1-mini");
        Assert.DoesNotContain(models, m => m.Name == "sample_spec");
        Assert.DoesNotContain(models, m => m.Name == "text-embedding-3-small");
    }

    [Fact]
    public void Parse_MapsCapabilityTraits()
    {
        IReadOnlyList<RoutingChatModel> models = LiteLlmModelCatalog.Parse(SampleCatalog);

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
    public void Parse_NeverInfersLatencyOrQuality()
    {
        IReadOnlyList<RoutingChatModel> models = LiteLlmModelCatalog.Parse(SampleCatalog);

        foreach (RoutingChatModel model in models)
        {
            Assert.Null(model.TypicalLatency);
        }
    }

    [Fact]
    public void Parse_ConvertsPerTokenCostToPerMillion()
    {
        RoutingChatModel gpt4o = Get(LiteLlmModelCatalog.Parse(SampleCatalog), "gpt-4o");

        Assert.Equal(2.5m, gpt4o.InputTokenCostPerMillion);
        Assert.Equal(10m, gpt4o.OutputTokenCostPerMillion);
    }

    [Fact]
    public void Parse_CarriesObjectiveMetadata()
    {
        RoutingChatModel gpt4o = Get(LiteLlmModelCatalog.Parse(SampleCatalog), "gpt-4o");

        Assert.Equal("openai", gpt4o.ProviderName);
        Assert.Equal("gpt-4o", gpt4o.ModelId);
        Assert.Equal(128_000, gpt4o.MaxInputTokens);
        Assert.Equal(new Uri("https://platform.openai.com/docs/pricing"), gpt4o.SourceUri);

        AdditionalPropertiesDictionary props = Assert.IsType<AdditionalPropertiesDictionary>(gpt4o.AdditionalProperties);
        Assert.Equal(16_384L, props[LiteLlmModelCatalog.MaxOutputTokensMetadataKey]);
        Assert.Equal("2025-12-01", props[LiteLlmModelCatalog.DeprecationDateMetadataKey]);
        Assert.True((bool)props[LiteLlmModelCatalog.MetadataKeyPrefix + "supports_response_schema"]!);
    }

    [Fact]
    public void Parse_LeavesMaxInputTokensNull_WhenAbsent()
    {
        RoutingChatModel o1 = Get(LiteLlmModelCatalog.Parse(SampleCatalog), "o1-mini");

        Assert.Null(o1.MaxInputTokens);
    }

    [Fact]
    public void Parse_IncludesEmbeddings_WhenChatModelsOnlyFalse()
    {
        IReadOnlyList<RoutingChatModel> models =
            LiteLlmModelCatalog.Parse(SampleCatalog, new LiteLlmCatalogOptions { ChatModelsOnly = false });

        Assert.Equal(3, models.Count);
        Assert.Contains(models, m => m.Name == "text-embedding-3-small");
    }

    [Fact]
    public void Parse_AppliesIncludeModelFilter()
    {
        IReadOnlyList<RoutingChatModel> models = LiteLlmModelCatalog.Parse(
            SampleCatalog,
            new LiteLlmCatalogOptions { IncludeModel = name => name.StartsWith("gpt", StringComparison.Ordinal) });

        RoutingChatModel only = Assert.Single(models);
        Assert.Equal("gpt-4o", only.Name);
    }

    [Fact]
    public void Parse_AppliesUpdatedAtProvenance()
    {
        var updatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        IReadOnlyList<RoutingChatModel> models =
            LiteLlmModelCatalog.Parse(SampleCatalog, new LiteLlmCatalogOptions { UpdatedAt = updatedAt });

        Assert.All(models, m => Assert.Equal(updatedAt, m.UpdatedAt));
    }

    [Fact]
    public void Parse_DeduplicatesByNameCaseInsensitive()
    {
        const string Json = """
            {
              "Model-X": { "mode": "chat", "litellm_provider": "p", "input_cost_per_token": 0.000001 },
              "model-x": { "mode": "chat", "litellm_provider": "p", "input_cost_per_token": 0.000002 }
            }
            """;

        IReadOnlyList<RoutingChatModel> models = LiteLlmModelCatalog.Parse(Json);

        RoutingChatModel only = Assert.Single(models);
        Assert.Equal("Model-X", only.Name);
        Assert.Equal(1m, only.InputTokenCostPerMillion);
    }

    [Fact]
    public void Load_Stream_ProducesSameResultAsParse()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleCatalog));

        IReadOnlyList<RoutingChatModel> models = LiteLlmModelCatalog.Load(stream);

        Assert.Equal(2, models.Count);
    }

    [Fact]
    public void Parse_ResultBindsIntoCatalogAndClient()
    {
        var catalog = new RoutingChatModelCatalog(LiteLlmModelCatalog.Parse(SampleCatalog));
        using var client = new TestChatClient();

        RoutingChatModel bound = catalog.CreateModel("gpt-4o", client);

        Assert.Same(client, bound.Client);
        Assert.Equal("openai", bound.ProviderName);
        Assert.True(bound.Traits.HasFlag(RoutingChatModelTraits.Vision));
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
