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
/// Maps a LiteLLM model catalog document (<c>model_prices_and_context_window.json</c>) into
/// metadata-only <see cref="RoutingChatModel"/> entries that a selection policy can consume.
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
/// <see cref="RoutingChatModelTraits.Reasoning"/>); per-token cost is converted to a per-million
/// figure; the context window is promoted to <see cref="RoutingChatModel.MaxInputTokens"/>; and
/// remaining objective fields (max output tokens, mode, deprecation date, and additional capability
/// flags) are carried under <see cref="RoutingChatModel.AdditionalProperties"/> keyed with the
/// <see cref="MetadataKeyPrefix"/>. Latency- and quality-related traits are never inferred because
/// the catalog carries no such data.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public static class LiteLlmModelCatalog
{
    /// <summary>The prefix used for catalog-derived keys placed in <see cref="RoutingChatModel.AdditionalProperties"/>.</summary>
    public const string MetadataKeyPrefix = "litellm.";

    /// <summary>The additional-properties key carrying the maximum output token count.</summary>
    public const string MaxOutputTokensMetadataKey = MetadataKeyPrefix + "max_output_tokens";

    /// <summary>The additional-properties key carrying the model deprecation date.</summary>
    public const string DeprecationDateMetadataKey = MetadataKeyPrefix + "deprecation_date";

    private const string SampleSpecKey = "sample_spec";
    private const decimal TokensPerMillion = 1_000_000m;

    // Objective fields carried verbatim into AdditionalProperties under the MetadataKeyPrefix.
    private static readonly (string Field, JsonValueKind Kind)[] _carriedFields =
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

    /// <summary>Maps a LiteLLM catalog JSON string into routing model metadata.</summary>
    /// <param name="json">The catalog document as a JSON string.</param>
    /// <param name="options">Optional mapping options.</param>
    /// <returns>The mapped metadata-only models, de-duplicated by name (case-insensitive, first wins).</returns>
    public static IReadOnlyList<RoutingChatModel> Parse(string json, LiteLlmCatalogOptions? options = null)
    {
        _ = Throw.IfNull(json);

        using var document = JsonDocument.Parse(json);
        return CreateModels(document, options ?? new LiteLlmCatalogOptions());
    }

    /// <summary>Maps a LiteLLM catalog JSON stream into routing model metadata.</summary>
    /// <param name="utf8Json">A stream containing the catalog document as UTF-8 JSON.</param>
    /// <param name="options">Optional mapping options.</param>
    /// <returns>The mapped metadata-only models, de-duplicated by name (case-insensitive, first wins).</returns>
    public static IReadOnlyList<RoutingChatModel> Load(Stream utf8Json, LiteLlmCatalogOptions? options = null)
    {
        _ = Throw.IfNull(utf8Json);

        using var document = JsonDocument.Parse(utf8Json);
        return CreateModels(document, options ?? new LiteLlmCatalogOptions());
    }

    private static List<RoutingChatModel> CreateModels(JsonDocument document, LiteLlmCatalogOptions options)
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

            models.Add(CreateModel(entry.Name, entry.Value, options));
        }

        return models;
    }

    private static RoutingChatModel CreateModel(string name, JsonElement entry, LiteLlmCatalogOptions options)
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

        AdditionalPropertiesDictionary? additionalProperties = CollectMetadata(entry);

        return new RoutingChatModel(
            name: name,
            providerName: TryGetString(entry, "litellm_provider"),
            modelId: name,
            traits: traits,
            maxInputTokens: TryGetInt32(entry, "max_input_tokens"),
            inputTokenCostPerMillion: inputCost * TokensPerMillion,
            outputTokenCostPerMillion: outputCost * TokensPerMillion,
            sourceUri: ResolveSource(entry, options),
            updatedAt: options.UpdatedAt,
            additionalProperties: additionalProperties);
    }

    private static AdditionalPropertiesDictionary? CollectMetadata(JsonElement entry)
    {
        AdditionalPropertiesDictionary? properties = null;
        foreach ((string field, JsonValueKind kind) in _carriedFields)
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
            properties[MetadataKeyPrefix + field] = mapped;
        }

        return properties;
    }

    private static Uri? ResolveSource(JsonElement entry, LiteLlmCatalogOptions options)
    {
        string? source = TryGetString(entry, "source");
        if (source is not null && Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            return uri;
        }

        return options.SourceUri;
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
