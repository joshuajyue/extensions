// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Represents a single attempt by a <see cref="RoutingChatClient"/> to use a model.</summary>
/// <remarks>
/// Attempts are surfaced to a selection policy through <see cref="ChatRouteContext.PreviousAttempt"/>
/// so that advanced selectors (for example, circuit breakers) can react to a model failure.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class ChatRouteAttempt
{
    /// <summary>Initializes a new instance of the <see cref="ChatRouteAttempt"/> class.</summary>
    /// <param name="model">The model that was attempted.</param>
    /// <param name="attemptNumber">The 1-based ordinal of this attempt within a single request.</param>
    /// <param name="exception">The exception that caused the attempt to fail, if any.</param>
    public ChatRouteAttempt(RoutingChatModel model, int attemptNumber, Exception? exception = null)
    {
        Model = Throw.IfNull(model);
        AttemptNumber = attemptNumber;
        Exception = exception;
    }

    /// <summary>Gets the model that was attempted.</summary>
    public RoutingChatModel Model { get; }

    /// <summary>Gets the 1-based ordinal of this attempt within a single request.</summary>
    public int AttemptNumber { get; }

    /// <summary>Gets the exception that caused the attempt to fail, if any.</summary>
    public Exception? Exception { get; }
}
