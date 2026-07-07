// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>
/// Well-known capability tokens and the <see cref="ChatRoute.AdditionalProperties"/> key a route uses to
/// declare what it supports, for use with a <c>RoutingChatClient</c> capability detector.
/// </summary>
/// <remarks>
/// <para>
/// Capabilities are modeled as open <see cref="string"/> tokens rather than a closed enum: a route declares
/// the tokens it supports under <see cref="PropertyKey"/> in its <see cref="ChatRoute.AdditionalProperties"/>
/// (the value is an <see cref="IEnumerable{T}"/> of <see cref="string"/>), and a request declares the tokens it
/// requires. The router keeps only routes whose declared set is a superset of the required set. Because the
/// vocabulary is open, an application can introduce its own tokens (for example <c>"code_interpreter"</c> or a
/// bespoke <c>"legal_reviewed"</c>) without any library change; the constants here are the well-known tokens the
/// default detector understands, named to align with LiteLLM's <c>supports_*</c> flags.
/// </para>
/// <para>
/// Only <see cref="Vision"/> and <see cref="FunctionCalling"/> are emitted by the router's default detector,
/// because they are the two capabilities a request can provably require from its own content (image parts and
/// <see cref="AIFunctionDeclaration"/> tools). The remaining tokens are provided for discoverability so a
/// custom detector can require them; the router never requires them on its own.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public static class ChatModelCapabilities
{
    /// <summary>
    /// The <see cref="ChatRoute.AdditionalProperties"/> key under which a route declares the capability tokens
    /// it supports. The value is an <see cref="IEnumerable{T}"/> of <see cref="string"/> tokens.
    /// </summary>
    public const string PropertyKey = "capabilities";

    /// <summary>The route accepts image input (LiteLLM <c>supports_vision</c>).</summary>
    public const string Vision = "vision";

    /// <summary>The route supports tool use / function calling (LiteLLM <c>supports_function_calling</c>).</summary>
    public const string FunctionCalling = "function_calling";

    /// <summary>The route supports structured/JSON-schema response formats (LiteLLM <c>supports_response_schema</c>).</summary>
    public const string ResponseSchema = "response_schema";

    /// <summary>The route accepts PDF document input (LiteLLM <c>supports_pdf_input</c>).</summary>
    public const string PdfInput = "pdf_input";

    /// <summary>The route accepts audio input (LiteLLM <c>supports_audio_input</c>).</summary>
    public const string AudioInput = "audio_input";

    /// <summary>The route supports reasoning (LiteLLM <c>supports_reasoning</c>).</summary>
    public const string Reasoning = "reasoning";

    /// <summary>The route supports provider-hosted web search (LiteLLM <c>supports_web_search</c>).</summary>
    public const string WebSearch = "web_search";
}
