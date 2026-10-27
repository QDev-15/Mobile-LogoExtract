using System.Text.Json.Serialization;

namespace LogoExtract.Core;

/// <summary>Source-generated metadata for ItemStore's own JSON reads/writes (item.json, batches.json) --
/// a plain reflection-based JsonSerializer.Serialize/Deserialize&lt;T&gt; call throws
/// "InvalidOperationException: JsonSerializerIsReflectionDisabled" the instant Release's full linking
/// (AndroidLinkMode=Full) strips the reflection metadata it needs (found on a real device, 2026-10-27:
/// crashed on the very first photo import). IncludeFields=true: WarpGrid.Displacements is
/// IReadOnlyList&lt;(double Dx, double Dy)&gt; -- ValueTuple exposes Item1/Item2 as public FIELDS, not
/// properties, so without this they would silently serialize as "{}" instead of throwing anything.</summary>
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(LogoItem))]
[JsonSerializable(typeof(List<BatchGroup>))]
internal partial class AppJsonContext : JsonSerializerContext;
