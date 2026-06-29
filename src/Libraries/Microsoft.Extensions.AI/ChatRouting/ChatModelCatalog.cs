// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// Maps external model catalog documents into metadata-only <see cref="RoutingChatModel"/> entries
/// that a selection policy can consume. Two sources are supported: the LiteLLM catalog
/// (<c>model_prices_and_context_window.json</c>) and the GitHub Models catalog
/// (<c>https://models.github.ai/catalog/models</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is a policy-side helper: it produces advisory model metadata (provider, traits, token cost,
/// context window) that an <see cref="IChatRouteSelector"/> may use. It performs no I/O beyond
/// reading the supplied JSON, and the produced models carry no <see cref="RoutingChatModel.Client"/> —
/// bind a client with <see cref="RoutingChatModel.WithClient"/> (or a <see cref="RoutingChatModelCatalog"/>).
/// </para>
/// <para>
/// Only objective fields are mapped. Capability flags become <see cref="RoutingChatModelTraits"/>
/// (<see cref="RoutingChatModelTraits.ToolCalling"/>, <see cref="RoutingChatModelTraits.Vision"/>,
/// <see cref="RoutingChatModelTraits.Reasoning"/>); per-token cost (when present) is converted to a
/// per-million figure; the context window is promoted to <see cref="RoutingChatModel.MaxInputTokens"/>;
/// and remaining objective fields are carried under <see cref="RoutingChatModel.AdditionalProperties"/>
/// keyed with a source-specific prefix (<see cref="LiteLlmMetadataKeyPrefix"/> or
/// <see cref="GitHubModelsMetadataKeyPrefix"/>). Latency- and quality-related traits are never inferred
/// because the catalogs carry no such data.
/// </para>
/// <para>
/// Models common to both catalogs (such as <c>gpt-5-mini</c>) resolve to the same
/// <see cref="RoutingChatModel.Name"/>, so the two sources can be merged: the GitHub Models catalog
/// supplies capabilities and context limits, while LiteLLM adds token pricing.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public static class ChatModelCatalog
{
    /// <summary>The prefix used for LiteLLM-derived keys placed in <see cref="RoutingChatModel.AdditionalProperties"/>.</summary>
    public const string LiteLlmMetadataKeyPrefix = "litellm.";

    /// <summary>The additional-properties key carrying the maximum output token count from the LiteLLM catalog.</summary>
    public const string LiteLlmMaxOutputTokensMetadataKey = LiteLlmMetadataKeyPrefix + "max_output_tokens";

    /// <summary>The additional-properties key carrying the model deprecation date from the LiteLLM catalog.</summary>
    public const string LiteLlmDeprecationDateMetadataKey = LiteLlmMetadataKeyPrefix + "deprecation_date";

    /// <summary>The prefix used for GitHub Models-derived keys placed in <see cref="RoutingChatModel.AdditionalProperties"/>.</summary>
    public const string GitHubModelsMetadataKeyPrefix = "github.";

    /// <summary>The additional-properties key carrying the maximum output token count from the GitHub Models catalog.</summary>
    public const string GitHubModelsMaxOutputTokensMetadataKey = GitHubModelsMetadataKeyPrefix + "max_output_tokens";

    private const string SampleSpecKey = "sample_spec";
    private const decimal TokensPerMillion = 1_000_000m;

    // LiteLLM objective fields carried verbatim into AdditionalProperties under LiteLlmMetadataKeyPrefix.
    private static readonly (string Field, JsonValueKind Kind)[] _liteLlmCarriedFields =
    [
        ("max_output_tokens", JsonValueKind.Number),
        ("max_tokens", JsonValueKind.Number),
        ("mode", JsonValueKind.String),
        ("deprecation_date", JsonValueKind.String),
        ("supports_response_schema", JsonValueKind.True),
        ("supports_web_search", JsonValueKind.True),
        ("supports_prompt_caching", JsonValueKind.True),
        ("supports_audio_input", JsonValueKind.True),
        ("supports_audio_output", JsonValueKind.True),
    ];

    // GitHub Models objective scalar fields carried verbatim into AdditionalProperties under GitHubModelsMetadataKeyPrefix.
    private static readonly string[] _gitHubModelsCarriedStrings =
    [
        "id",
        "publisher",
        "summary",
        "rate_limit_tier",
        "registry",
        "version",
    ];

    // GitHub Models array fields joined and carried into AdditionalProperties under GitHubModelsMetadataKeyPrefix.
    private static readonly string[] _gitHubModelsCarriedArrays =
    [
        "tags",
        "capabilities",
        "supported_input_modalities",
        "supported_output_modalities",
    ];

    /// <summary>Maps a LiteLLM catalog JSON string into routing model metadata.</summary>
    /// <param name="json">The catalog document as a JSON string.</param>
    /// <param name="options">Optional mapping options.</param>
    /// <returns>The mapped metadata-only models, de-duplicated by name (case-insensitive, first wins).</returns>
    public static IReadOnlyList<RoutingChatModel> ParseLiteLlm(string json, ChatModelCatalogOptions? options = null)
    {
        _ = Throw.IfNull(json);

        using var document = JsonDocument.Parse(json);
        return CreateLiteLlmModels(document, options ?? new ChatModelCatalogOptions());
    }

    /// <summary>Maps a LiteLLM catalog JSON stream into routing model metadata.</summary>
    /// <param name="utf8Json">A stream containing the catalog document as UTF-8 JSON.</param>
    /// <param name="options">Optional mapping options.</param>
    /// <returns>The mapped metadata-only models, de-duplicated by name (case-insensitive, first wins).</returns>
    public static IReadOnlyList<RoutingChatModel> LoadLiteLlm(Stream utf8Json, ChatModelCatalogOptions? options = null)
    {
        _ = Throw.IfNull(utf8Json);

        using var document = JsonDocument.Parse(utf8Json);
        return CreateLiteLlmModels(document, options ?? new ChatModelCatalogOptions());
    }

    /// <summary>Maps a GitHub Models catalog JSON string into routing model metadata.</summary>
    /// <param name="json">The catalog document as a JSON string.</param>
    /// <param name="options">Optional mapping options.</param>
    /// <returns>The mapped metadata-only models, de-duplicated by name (case-insensitive, first wins).</returns>
    public static IReadOnlyList<RoutingChatModel> ParseGitHubModels(string json, ChatModelCatalogOptions? options = null)
    {
        _ = Throw.IfNull(json);

        using var document = JsonDocument.Parse(json);
        return CreateGitHubModels(document, options ?? new ChatModelCatalogOptions());
    }

    /// <summary>Maps a GitHub Models catalog JSON stream into routing model metadata.</summary>
    /// <param name="utf8Json">A stream containing the catalog document as UTF-8 JSON.</param>
    /// <param name="options">Optional mapping options.</param>
    /// <returns>The mapped metadata-only models, de-duplicated by name (case-insensitive, first wins).</returns>
    public static IReadOnlyList<RoutingChatModel> LoadGitHubModels(Stream utf8Json, ChatModelCatalogOptions? options = null)
    {
        _ = Throw.IfNull(utf8Json);

        using var document = JsonDocument.Parse(utf8Json);
        return CreateGitHubModels(document, options ?? new ChatModelCatalogOptions());
    }

    private static List<RoutingChatModel> CreateLiteLlmModels(JsonDocument document, ChatModelCatalogOptions options)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            Throw.ArgumentException(nameof(document), "The LiteLLM catalog root must be a JSON object.");
        }

        var models = new List<RoutingChatModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty entry in document.RootElement.EnumerateObject())
        {
            if (string.Equals(entry.Name, SampleSpecKey, StringComparison.Ordinal) ||
                entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? mode = TryGetString(entry.Value, "mode");
            if (options.ChatModelsOnly && !string.Equals(mode, "chat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (options.IncludeModel is not null && !options.IncludeModel(entry.Name))
            {
                continue;
            }

            if (!seen.Add(entry.Name))
            {
                continue;
            }

            models.Add(CreateLiteLlmModel(entry.Name, entry.Value, options));
        }

        return models;
    }

    private static RoutingChatModel CreateLiteLlmModel(string name, JsonElement entry, ChatModelCatalogOptions options)
    {
        RoutingChatModelTraits traits = RoutingChatModelTraits.None;
        if ((TryGetBool(entry, "supports_function_calling") ?? false) ||
            (TryGetBool(entry, "supports_tool_choice") ?? false))
        {
            traits |= RoutingChatModelTraits.ToolCalling;
        }

        if ((TryGetBool(entry, "supports_vision") ?? false) ||
            (TryGetBool(entry, "supports_image_input") ?? false))
        {
            traits |= RoutingChatModelTraits.Vision;
        }

        if (TryGetBool(entry, "supports_reasoning") ?? false)
        {
            traits |= RoutingChatModelTraits.Reasoning;
        }

        decimal? inputCost = TryGetDecimal(entry, "input_cost_per_token");
        decimal? outputCost = TryGetDecimal(entry, "output_cost_per_token");

        AdditionalPropertiesDictionary? additionalProperties = CollectLiteLlmMetadata(entry);

        return new RoutingChatModel(
            name: name,
            providerName: TryGetString(entry, "litellm_provider"),
            modelId: name,
            traits: traits,
            maxInputTokens: TryGetInt32(entry, "max_input_tokens"),
            inputTokenCostPerMillion: inputCost * TokensPerMillion,
            outputTokenCostPerMillion: outputCost * TokensPerMillion,
            sourceUri: ResolveLiteLlmSource(entry, options),
            updatedAt: options.UpdatedAt,
            additionalProperties: additionalProperties);
    }

    private static AdditionalPropertiesDictionary? CollectLiteLlmMetadata(JsonElement entry)
    {
        AdditionalPropertiesDictionary? properties = null;
        foreach ((string field, JsonValueKind kind) in _liteLlmCarriedFields)
        {
            if (!entry.TryGetProperty(field, out JsonElement value))
            {
                continue;
            }

            object? mapped = kind switch
            {
                JsonValueKind.Number when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) => number,
                JsonValueKind.String when value.ValueKind == JsonValueKind.String => value.GetString(),
                JsonValueKind.True when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
                _ => null,
            };

            if (mapped is null)
            {
                continue;
            }

            properties ??= [];
            properties[LiteLlmMetadataKeyPrefix + field] = mapped;
        }

        return properties;
    }

    private static Uri? ResolveLiteLlmSource(JsonElement entry, ChatModelCatalogOptions options)
    {
        string? source = TryGetString(entry, "source");
        if (source is not null && Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            return uri;
        }

        return options.SourceUri;
    }

    private static List<RoutingChatModel> CreateGitHubModels(JsonDocument document, ChatModelCatalogOptions options)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            Throw.ArgumentException(nameof(document), "The GitHub Models catalog root must be a JSON array.");
        }

        var models = new List<RoutingChatModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? id = TryGetString(entry, "id");
            if (id is null)
            {
                continue;
            }

            if (options.ChatModelsOnly && !ArrayContains(entry, "supported_output_modalities", "text"))
            {
                continue;
            }

            string name = StripPublisherPrefix(id);

            if (options.IncludeModel is not null && !options.IncludeModel(name))
            {
                continue;
            }

            if (!seen.Add(name))
            {
                continue;
            }

            models.Add(CreateGitHubModel(name, id, entry, options));
        }

        return models;
    }

    private static RoutingChatModel CreateGitHubModel(string name, string id, JsonElement entry, ChatModelCatalogOptions options)
    {
        RoutingChatModelTraits traits = RoutingChatModelTraits.None;
        if (ArrayContains(entry, "capabilities", "tool-calling"))
        {
            traits |= RoutingChatModelTraits.ToolCalling;
        }

        if (ArrayContains(entry, "supported_input_modalities", "image"))
        {
            traits |= RoutingChatModelTraits.Vision;
        }

        if (ArrayContains(entry, "capabilities", "reasoning"))
        {
            traits |= RoutingChatModelTraits.Reasoning;
        }

        int? maxInputTokens = null;
        if (entry.TryGetProperty("limits", out JsonElement limits) && limits.ValueKind == JsonValueKind.Object)
        {
            maxInputTokens = TryGetInt32(limits, "max_input_tokens");
        }

        return new RoutingChatModel(
            name: name,
            providerName: TryGetString(entry, "publisher"),
            modelId: id,
            traits: traits,
            maxInputTokens: maxInputTokens,
            sourceUri: ResolveGitHubModelsSource(entry, options),
            updatedAt: options.UpdatedAt,
            additionalProperties: CollectGitHubMetadata(entry, limits));
    }

    private static AdditionalPropertiesDictionary? CollectGitHubMetadata(JsonElement entry, JsonElement limits)
    {
        AdditionalPropertiesDictionary? properties = null;

        foreach (string field in _gitHubModelsCarriedStrings)
        {
            string? value = TryGetString(entry, field);
            if (value is not null)
            {
                properties ??= [];
                properties[GitHubModelsMetadataKeyPrefix + field] = value;
            }
        }

        foreach (string field in _gitHubModelsCarriedArrays)
        {
            string? joined = JoinStringArray(entry, field);
            if (joined is not null)
            {
                properties ??= [];
                properties[GitHubModelsMetadataKeyPrefix + field] = joined;
            }
        }

        if (limits.ValueKind == JsonValueKind.Object &&
            limits.TryGetProperty("max_output_tokens", out JsonElement maxOutput) &&
            maxOutput.ValueKind == JsonValueKind.Number &&
            maxOutput.TryGetInt64(out long maxOutputTokens))
        {
            properties ??= [];
            properties[GitHubModelsMaxOutputTokensMetadataKey] = maxOutputTokens;
        }

        return properties;
    }

    private static Uri? ResolveGitHubModelsSource(JsonElement entry, ChatModelCatalogOptions options)
    {
        string? source = TryGetString(entry, "html_url");
        if (source is not null && Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            return uri;
        }

        return options.SourceUri;
    }

    private static string StripPublisherPrefix(string id)
    {
        int slash = id.IndexOf("/", StringComparison.Ordinal);
        return slash >= 0 && slash < id.Length - 1 ? id.Substring(slash + 1) : id;
    }

    private static bool ArrayContains(JsonElement entry, string name, string value)
    {
        if (entry.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    string.Equals(item.GetString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? JoinStringArray(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } part)
            {
                parts.Add(part);
            }
        }

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static string? TryGetString(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ?
            value.GetString() :
            null;

    private static bool? TryGetBool(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ?
            value.GetBoolean() :
            null;

    private static int? TryGetInt32(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ?
            number :
            null;

    private static decimal? TryGetDecimal(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed))
        {
            return parsed;
        }

        return null;
    }
}
