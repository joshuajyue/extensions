# Microsoft.Extensions.AI.Abstractions — File Reference

This document describes the purpose of every file in the `Microsoft.Extensions.AI.Abstractions` project. The library defines the core abstractions (interfaces, base types, and shared DTOs) used across the `Microsoft.Extensions.AI.*` ecosystem for chat, embeddings, tools/functions, image/speech, realtime sessions, and hosted-file scenarios.

---

## Project / metadata files

| File | Purpose |
|------|---------|
| `Microsoft.Extensions.AI.Abstractions.csproj` | MSBuild project file. Declares target frameworks, package metadata, and dependencies for the abstractions library. |
| `Microsoft.Extensions.AI.Abstractions.json` | Public API baseline manifest (used by API compatibility tooling). |
| `CompatibilitySuppressions.xml` | Records intentional API-compat suppressions accepted across versions. |
| `README.md` | Package-level README shown on NuGet and GitHub. |

---

## Root-level types

| File | Purpose |
|------|---------|
| `AdditionalPropertiesDictionary.cs` | A `Dictionary<string, object?>` subclass for attaching arbitrary, provider-specific metadata to AI types. |
| `AdditionalPropertiesDictionary{TValue}.cs` | Generic variant keyed by string with typed values, used where stronger typing is desirable. |
| `UsageDetails.cs` | DTO capturing token-usage statistics (input/output/total token counts plus extensible per-provider details). |
| `ResponseContinuationToken.cs` | Opaque token used to resume long-running or paged AI responses. |
| `Throw.cs` | Internal argument-validation helpers (null/empty checks) used throughout the library. |
| `HostedMcpServerToolApprovalMode.cs` | Abstract base for approval-policy modes applied to a hosted MCP-server tool. |
| `HostedMcpServerToolAlwaysRequireApprovalMode.cs` | Approval mode: every tool invocation requires user approval. |
| `HostedMcpServerToolNeverRequireApprovalMode.cs` | Approval mode: tool invocations never require approval. |
| `HostedMcpServerToolRequireSpecificApprovalMode.cs` | Approval mode: only an explicit allow/deny list of tools requires approval. |

---

## `ChatCompletion/` — Chat client abstractions

| File | Purpose |
|------|---------|
| `IChatClient.cs` | Core interface for chat-completion clients (`GetResponseAsync`, `GetStreamingResponseAsync`, metadata, services). |
| `DelegatingChatClient.cs` | Base class for building middleware/decorator chat clients that forward to an inner `IChatClient`. |
| `ChatClientExtensions.cs` | Extension helpers on `IChatClient` (e.g., string-prompt overloads, service resolution). |
| `ChatClientMetadata.cs` | Provider/model identifying metadata exposed by an `IChatClient`. |
| `ChatMessage.cs` | A single chat message: role, content list, author name, additional properties. |
| `ChatRole.cs` | Strongly-typed wrapper for chat roles (`system`, `user`, `assistant`, `tool`, …). |
| `ChatOptions.cs` | Request-side options (temperature, max tokens, model id, tools, stop sequences, response format, etc.). |
| `ChatResponse.cs` | A complete (non-streaming) chat response: messages, finish reason, usage, raw representation. |
| `ChatResponseUpdate.cs` | One chunk of a streaming chat response; aggregates into a `ChatResponse`. |
| `ChatResponseExtensions.cs` | Helpers to convert/aggregate response updates and extract typed payloads. |
| `ChatFinishReason.cs` | Strongly-typed wrapper for finish reasons (`stop`, `length`, `tool_calls`, …). |
| `ChatResponseFormat.cs` | Abstract base describing the requested response format. |
| `ChatResponseFormatText.cs` | Plain-text response-format marker. |
| `ChatResponseFormatJson.cs` | JSON response format, optionally constrained by a JSON schema. |
| `ChatToolMode.cs` | Abstract base controlling how tools may be selected by the model. |
| `AutoChatToolMode.cs` | Tool mode: model decides whether and which tool to call. |
| `AutoSelectingChatClient.cs` | Internal step-1 auto-select router that chooses among candidate chat clients and forwards requests with normalized selection metadata. |
| `AutoSelectingChatClientCandidate.cs` | Internal candidate descriptor used by the step-1 auto-select router (name, provider, model, and underlying client). |
| `NoneChatToolMode.cs` | Tool mode: tool calling disabled. |
| `RequiredChatToolMode.cs` | Tool mode: model must call a tool (optionally a named one). |
| `ReasoningOptions.cs` | Options for models that support explicit reasoning (effort level, etc.). |
| `ReasoningEffort.cs` | Strongly-typed reasoning effort values (e.g., low/medium/high). |
| `ReasoningOutput.cs` | Output describing the reasoning a model produced. |

---

## `ChatReduction/`

| File | Purpose |
|------|---------|
| `IChatReducer.cs` | Interface for components that reduce/trim a chat history (e.g., summarization or windowing) before sending to a model. |

---

## `Contents/` — `AIContent` polymorphic content types

| File | Purpose |
|------|---------|
| `AIContent.cs` | Polymorphic base class for all message-content items, with JSON polymorphism and extension data. |
| `AIContentExtensions.cs` | Helpers over `IList<AIContent>` (concatenate text, find specific kinds). |
| `TextContent.cs` | Plain-text content. |
| `TextReasoningContent.cs` | Text content representing model reasoning (separate from user-facing text). |
| `DataContent.cs` | Inline binary content with a media type (bytes embedded directly). |
| `UriContent.cs` | Content referenced by a URI with a media type. |
| `DataUriParser.cs` | Internal helper that parses/validates `data:` URIs. |
| `ErrorContent.cs` | Represents an error reported in a message stream. |
| `UsageContent.cs` | Wraps `UsageDetails` so usage can flow as part of a streaming content stream. |
| `FunctionCallContent.cs` | A model-issued function call (id, name, arguments). |
| `FunctionResultContent.cs` | The result of executing a function call, sent back to the model. |
| `ToolCallContent.cs` | Base type for hosted-tool invocation content. |
| `ToolResultContent.cs` | Base type for hosted-tool result content. |
| `ToolApprovalRequestContent.cs` | Content asking the host/user to approve a tool call. |
| `ToolApprovalResponseContent.cs` | Content carrying the approval/denial of a tool call. |
| `CodeInterpreterToolCallContent.cs` | Tool-call content for a hosted code-interpreter invocation. |
| `CodeInterpreterToolResultContent.cs` | Result content from a hosted code-interpreter run. |
| `ImageGenerationToolCallContent.cs` | Tool-call content for a hosted image-generation request. |
| `ImageGenerationToolResultContent.cs` | Result content from a hosted image-generation run. |
| `WebSearchToolCallContent.cs` | Tool-call content for a hosted web-search request. |
| `WebSearchToolResultContent.cs` | Result content from a hosted web-search run. |
| `McpServerToolCallContent.cs` | Tool-call content directed at a hosted MCP server tool. |
| `McpServerToolResultContent.cs` | Result content from a hosted MCP server tool. |
| `HostedFileContent.cs` | Reference to a file stored on the hosted service. |
| `HostedVectorStoreContent.cs` | Reference to a vector store stored on the hosted service. |
| `InputRequestContent.cs` | Base type for content representing a request for additional input. |
| `InputResponseContent.cs` | Base type for content carrying that additional input back. |
| `AIAnnotation.cs` | Base type for annotations attached to content (e.g., citations, regions). |
| `AnnotatedRegion.cs` | Base type for region descriptors (which span an annotation refers to). |
| `TextSpanAnnotatedRegion.cs` | Annotated region describing a `[start, end)` text span. |
| `CitationAnnotation.cs` | Annotation carrying citation metadata (title, URL, source span). |

---

## `Embeddings/` — Embedding generator abstractions

| File | Purpose |
|------|---------|
| `IEmbeddingGenerator.cs` | Non-generic root interface (metadata/services) for embedding generators. |
| `IEmbeddingGenerator{TInput,TEmbedding}.cs` | Generic interface for generating `TEmbedding` values from `TInput` items. |
| `DelegatingEmbeddingGenerator.cs` | Base class for embedding-generator middleware/decorators. |
| `EmbeddingGeneratorExtensions.cs` | Helpers (single-input overloads, service resolution). |
| `EmbeddingGeneratorMetadata.cs` | Provider/model metadata exposed by a generator. |
| `EmbeddingGenerationOptions.cs` | Request options (dimensions, model id, additional properties). |
| `Embedding.cs` | Non-generic base class for a single embedding (metadata, usage, created-at). |
| `Embedding{T}.cs` | Generic embedding carrying a `ReadOnlyMemory<T>` vector (typically `float`). |
| `BinaryEmbedding.cs` | Embedding represented as a packed bit vector. |
| `GeneratedEmbeddings.cs` | Collection of embeddings plus aggregate usage/metadata. |

---

## `Files/` — Hosted-file client abstractions

| File | Purpose |
|------|---------|
| `IHostedFileClient.cs` | Interface for clients that upload, list, download, and delete files on a hosted AI service. |
| `DelegatingHostedFileClient.cs` | Base class for middleware/decorators around `IHostedFileClient`. |
| `HostedFileClientExtensions.cs` | Convenience extensions (overloads, service resolution). |
| `HostedFileClientMetadata.cs` | Provider metadata for a hosted-file client. |
| `HostedFileClientOptions.cs` | Per-request options for hosted-file operations. |
| `HostedFileDownloadStream.cs` | Stream type returned by download operations, carrying file metadata. |

---

## `Functions/` — Callable function abstractions

| File | Purpose |
|------|---------|
| `AIFunctionDeclaration.cs` | Abstract declaration of a function (name, description, JSON schema) without an invoker. |
| `AIFunction.cs` | Abstract invocable function (declaration + `InvokeAsync`). |
| `AIFunctionArguments.cs` | Dictionary-like argument bag passed to `AIFunction.InvokeAsync`, with services. |
| `AIFunctionFactory.cs` | Static factory that wraps `Delegate`/`MethodInfo` instances as `AIFunction`s. |
| `AIFunctionFactoryOptions.cs` | Configuration for the factory (naming, JSON options, schema options, marshalling). |
| `DelegatingAIFunctionDeclaration.cs` | Base class for declaration decorators. |
| `DelegatingAIFunction.cs` | Base class for `AIFunction` decorators/middleware. |
| `ApprovalRequiredAIFunction.cs` | Wrapper marking a function as requiring approval before invocation. |

---

## `Image/` — Image generator abstractions

| File | Purpose |
|------|---------|
| `IImageGenerator.cs` | Interface for image-generation clients. |
| `DelegatingImageGenerator.cs` | Base class for image-generator middleware/decorators. |
| `ImageGeneratorExtensions.cs` | Convenience extensions for `IImageGenerator`. |
| `ImageGeneratorMetadata.cs` | Provider/model metadata. |
| `ImageGenerationOptions.cs` | Request options (size, count, response format, model id, …). |
| `ImageGenerationRequest.cs` | Strongly-typed request DTO (prompt, optional source images for edit/variation). |
| `ImageGenerationResponse.cs` | Response DTO containing generated `AIContent` images plus usage. |

---

## `Realtime/` — Realtime (streaming bidirectional) session abstractions

| File | Purpose |
|------|---------|
| `IRealtimeClient.cs` | Factory interface for opening realtime sessions. |
| `IRealtimeClientSession.cs` | A live duplex session: send client messages, receive server messages. |
| `DelegatingRealtimeClient.cs` | Base class for realtime-client decorators. |
| `RealtimeSessionKind.cs` | Discriminates session kinds (e.g., audio, text). |
| `RealtimeSessionOptions.cs` | Options used when opening a session (modalities, voice, VAD, tools). |
| `RealtimeAudioFormat.cs` | Strongly-typed audio-format identifier (pcm16, g711, …). |
| `VoiceActivityDetectionOptions.cs` | Server-side VAD settings (thresholds, silence durations). |
| `RealtimeConversationItem.cs` | A conversation item exchanged over the realtime channel. |
| `RealtimeResponseStatus.cs` | Status info for a realtime response (completed, failed, cancelled). |
| `RealtimeClientMessage.cs` | Polymorphic base for messages sent client → server. |
| `SessionUpdateRealtimeClientMessage.cs` | Client message updating session-level options mid-session. |
| `CreateConversationItemRealtimeClientMessage.cs` | Client message appending a conversation item. |
| `CreateResponseRealtimeClientMessage.cs` | Client message requesting the server to generate a response. |
| `InputAudioBufferAppendRealtimeClientMessage.cs` | Client message appending audio bytes to the input buffer. |
| `InputAudioBufferCommitRealtimeClientMessage.cs` | Client message committing the buffered input audio as an utterance. |
| `RealtimeServerMessage.cs` | Polymorphic base for messages received server → client. |
| `RealtimeServerMessageType.cs` | Discriminator/enum of server message kinds. |
| `ErrorRealtimeServerMessage.cs` | Server message reporting an error. |
| `InputAudioTranscriptionRealtimeServerMessage.cs` | Server message delivering ASR transcription of input audio. |
| `OutputTextAudioRealtimeServerMessage.cs` | Server message delivering output text/audio deltas. |
| `ResponseCreatedRealtimeServerMessage.cs` | Server message signalling a response has begun. |
| `ResponseOutputItemRealtimeServerMessage.cs` | Server message delivering an output item within a response. |

---

## `SpeechToText/`

| File | Purpose |
|------|---------|
| `ISpeechToTextClient.cs` | Interface for STT clients (streaming and non-streaming). |
| `DelegatingSpeechToTextClient.cs` | Base class for STT middleware/decorators. |
| `SpeechToTextClientExtensions.cs` | Convenience extensions on `ISpeechToTextClient`. |
| `SpeechToTextClientMetadata.cs` | Provider/model metadata. |
| `SpeechToTextOptions.cs` | Common STT options. |
| `TranscriptionOptions.cs` | Transcription-specific options (language, prompt, timestamps). |
| `SpeechToTextResponse.cs` | Complete transcription response. |
| `SpeechToTextResponseUpdate.cs` | Single streaming update chunk. |
| `SpeechToTextResponseUpdateKind.cs` | Discriminator for the kind of streaming update. |
| `SpeechToTextResponseUpdateExtensions.cs` | Helpers to aggregate updates into a full response. |

---

## `TextToSpeech/`

| File | Purpose |
|------|---------|
| `ITextToSpeechClient.cs` | Interface for TTS clients (streaming and non-streaming). |
| `DelegatingTextToSpeechClient.cs` | Base class for TTS middleware/decorators. |
| `TextToSpeechClientExtensions.cs` | Convenience extensions. |
| `TextToSpeechClientMetadata.cs` | Provider/model metadata. |
| `TextToSpeechOptions.cs` | Common TTS options (voice, format, speed, …). |
| `TextToSpeechResponse.cs` | Complete synthesis response. |
| `TextToSpeechResponseUpdate.cs` | Single streaming update chunk. |
| `TextToSpeechResponseUpdateKind.cs` | Discriminator for the kind of streaming update. |
| `TextToSpeechResponseUpdateExtensions.cs` | Helpers to aggregate updates into a full response. |

---

## `Tools/` — Hosted (server-side) tool markers

| File | Purpose |
|------|---------|
| `AITool.cs` | Common base type for anything passable as a tool in `ChatOptions.Tools` (includes `AIFunction`). |
| `HostedCodeInterpreterTool.cs` | Marker tool indicating the service should expose its hosted code interpreter. |
| `HostedFileSearchTool.cs` | Marker tool enabling hosted file search (e.g., against a vector store). |
| `HostedImageGenerationTool.cs` | Marker tool enabling hosted image generation. |
| `HostedWebSearchTool.cs` | Marker tool enabling hosted web search. |
| `HostedToolSearchTool.cs` | Marker tool enabling hosted tool discovery/search. |
| `HostedMcpServerTool.cs` | Marker tool wiring a hosted MCP-server-backed toolset, including its approval mode. |

---

## `Utilities/` — JSON schema and serialization helpers

| File | Purpose |
|------|---------|
| `AIJsonUtilities.cs` | Public façade for shared JSON helpers (serializer options, schema entry points). |
| `AIJsonUtilities.Defaults.cs` | Default `JsonSerializerOptions` and `JsonSchemaExporterOptions` used across the library. |
| `AIJsonUtilities.Schema.Create.cs` | API for creating JSON schemas from .NET types/methods for use with AI models. |
| `AIJsonUtilities.Schema.Transform.cs` | API for post-processing/transforming generated schemas (e.g., to provider dialects). |
| `AIJsonSchemaCreateOptions.cs` | Options controlling schema creation (descriptions, conventions, transforms). |
| `AIJsonSchemaCreateContext.cs` | Context object passed during schema creation (type/parameter being described). |
| `AIJsonSchemaTransformOptions.cs` | Options for the transform pipeline (disallow additional props, require all, etc.). |
| `AIJsonSchemaTransformContext.cs` | Context object passed during schema transformation. |
| `AIJsonSchemaTransformCache.cs` | Caches transformed schemas keyed by source options + input, to avoid repeat work. |
