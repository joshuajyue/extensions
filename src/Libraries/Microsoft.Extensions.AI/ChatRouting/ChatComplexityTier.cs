// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>Describes how complex a request is judged to be by a <see cref="ComplexityChatRouteSelector"/>.</summary>
/// <remarks>The tiers mirror the LiteLLM complexity router: <c>SIMPLE</c>, <c>MEDIUM</c>, <c>COMPLEX</c>, and <c>REASONING</c>.</remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public enum ChatComplexityTier
{
    /// <summary>A short, trivial request (for example a greeting or a definition lookup).</summary>
    Simple,

    /// <summary>An everyday request of moderate complexity.</summary>
    Medium,

    /// <summary>A demanding request involving code, technical terms, or multiple steps.</summary>
    Complex,

    /// <summary>A request that calls for explicit multi-step reasoning.</summary>
    Reasoning,
}
