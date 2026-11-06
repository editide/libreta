using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Libreta;

public static class YamlFile
{
  public static IDeserializer Deserializer()
  {
    return Configure(new DeserializerBuilder()).Build();
  }

  public static DeserializerBuilder Configure(DeserializerBuilder builder)
  {
    return builder
      .WithNamingConvention(UnderscoredNamingConvention.Instance)
      .WithTypeConverter(new ViewColumnConverter());
  }

  public static T Load<T>(string path, IDeserializer? deserializer = null, IBookFiles? source = null)
  {
    try
    {
      return (deserializer ?? Deserializer()).Deserialize<T>((source ?? DiskFiles.Instance).ReadAllText(path));
    }
    catch (YamlException exception)
    {
      throw new InvalidDataException($"Invalid YAML in '{path}': {exception.Message}", exception);
    }
  }


}

/// <summary>Reads `columns: [2024, 2025]` and the mapping form into one shape.</summary>
public sealed class ViewColumnConverter : IYamlTypeConverter
{
  public bool Accepts(Type type)
  {
    return type == typeof(ViewColumn);
  }

  public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
  {
    if (parser.Current is Scalar)
    {
      return new ViewColumn { Source = parser.Consume<Scalar>().Value };
    }

    parser.Consume<MappingStart>();
    var column = new ViewColumn();
    while (!parser.TryConsume<MappingEnd>(out _))
    {
      string key = parser.Consume<Scalar>().Value;
      string value = parser.Consume<Scalar>().Value;
      switch (key)
      {
        case "source": column.Source = value; break;
        case "line": column.Line = value; break;
        case "column": column.Column = value; break;
        case "label": column.Label = value; break;
        case "style": column.Style = value; break;
        case "sign": column.Sign = value; break;
        case "note": column.Note = value; break;
        default: throw new InvalidDataException($"A view column has no property '{key}'");
      }
    }
    return column;
  }

  public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
  {
    throw new NotSupportedException();
  }


}