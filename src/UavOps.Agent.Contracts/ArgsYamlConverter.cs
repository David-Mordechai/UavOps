using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace UavOps.Agent.Contracts;

/// <summary>
/// The watchdog config file's <c>args</c> field accepts either a single string or an array of
/// strings (documented in the file's own header comment). This converter reads either form into a
/// uniform <see cref="List{T}"/> of <see cref="string"/>, and always writes back as a sequence — a
/// deliberate, documented normalization (values/order preserved, only the on-disk representation
/// is standardized), not data loss. Lives alongside <see cref="ServiceConfigEntry"/> in Contracts
/// so both <c>Agents.MaintenanceAgent.ServiceConfigFileStore</c> (full-file read/write) and
/// <see cref="ServiceConfigEntryFormatter"/> (single-entry snippets) share one implementation.
/// </summary>
public sealed class ArgsYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(List<string>);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return new List<string> { scalar.Value };
        }

        var result = new List<string>();
        parser.Consume<SequenceStart>();
        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            result.Add(parser.Consume<Scalar>().Value);
        }

        return result;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var list = (List<string>?)value ?? [];
        emitter.Emit(new SequenceStart(anchor: null, tag: null, isImplicit: true, SequenceStyle.Block));
        foreach (var item in list)
        {
            // Single-quoted, matching the config file's own documented examples (e.g. '-c arg1').
            emitter.Emit(new Scalar(anchor: null, tag: null, value: item, ScalarStyle.SingleQuoted, isPlainImplicit: false, isQuotedImplicit: true));
        }

        emitter.Emit(new SequenceEnd());
    }
}
