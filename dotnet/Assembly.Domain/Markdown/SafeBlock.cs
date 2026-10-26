using System.Text.Json.Serialization;

namespace Assembly.Domain.Markdown;

/// <summary>A block of a message, already parsed and made safe, so the UI draws it without parsing anything.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SafeParagraph), "paragraph")]
[JsonDerivedType(typeof(SafeHeading), "heading")]
[JsonDerivedType(typeof(SafeCode), "code")]
[JsonDerivedType(typeof(SafeQuote), "quote")]
[JsonDerivedType(typeof(SafeList), "list")]
[JsonDerivedType(typeof(SafeTable), "table")]
[JsonDerivedType(typeof(SafeRule), "rule")]
public abstract record SafeBlock;
