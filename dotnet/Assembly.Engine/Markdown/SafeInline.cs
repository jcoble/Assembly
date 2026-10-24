using System.Text.Json.Serialization;

namespace Assembly.Engine.Markdown;

/// <summary>An inline piece of a block. On the wire, <c>kind</c> says which one.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SafeText), "text")]
[JsonDerivedType(typeof(SafeCodeSpan), "code")]
[JsonDerivedType(typeof(SafeStrong), "strong")]
[JsonDerivedType(typeof(SafeEmphasis), "emphasis")]
[JsonDerivedType(typeof(SafeStrike), "strike")]
[JsonDerivedType(typeof(SafeLink), "link")]
[JsonDerivedType(typeof(SafeFileLink), "file-link")]
public abstract record SafeInline;
