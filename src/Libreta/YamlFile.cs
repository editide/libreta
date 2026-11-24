using System.Collections.Concurrent;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.TypeInspectors;

namespace Libreta;

public static class YamlFile
{
  public static IDeserializer Deserializer()
  {
    return Configure(new DeserializerBuilder()).Build();
  }

  private static DeserializerBuilder Configure(DeserializerBuilder builder)
  {
    return builder
      .WithNamingConvention(UnderscoredNamingConvention.Instance)
      .WithTypeConverter(new ViewColumnConverter())
      .WithTypeInspector(inner => new AttributeCachingTypeInspector(inner));
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

/// <summary>YamlDotNet reflects over a property's attributes every time it sets the property, which dominates loading a
/// book of hundreds of files unless each property's are read once.</summary>
public sealed class AttributeCachingTypeInspector : TypeInspectorSkeleton
{
  private readonly ITypeInspector inner;
  private readonly ConcurrentDictionary<Type, IPropertyDescriptor[]> properties =
    new ConcurrentDictionary<Type, IPropertyDescriptor[]>();

  public AttributeCachingTypeInspector(ITypeInspector inner)
  {
    this.inner = inner;
  }

  public override IEnumerable<IPropertyDescriptor> GetProperties(Type type, object? container)
  {
    return properties.GetOrAdd(type, _ => inner.GetProperties(type, container)
      .Select(property => (IPropertyDescriptor)new CachedAttributes(property))
      .ToArray());
  }

  public override string GetEnumName(Type enumType, string name)
  {
    return inner.GetEnumName(enumType, name);
  }

  public override string GetEnumValue(object enumValue)
  {
    return inner.GetEnumValue(enumValue);
  }

  public override bool HasParseMethod(Type type)
  {
    return inner.HasParseMethod(type);
  }

  public override object Parse(string value, Type type)
  {
    return inner.Parse(value, type);
  }

  private sealed class CachedAttributes : IPropertyDescriptor
  {
    private readonly IPropertyDescriptor inner;
    private readonly Lazy<Type?> converterType;
    private readonly ConcurrentDictionary<Type, Attribute?> attributes = new ConcurrentDictionary<Type, Attribute?>();

    public CachedAttributes(IPropertyDescriptor inner)
    {
      this.inner = inner;
      converterType = new Lazy<Type?>(() => inner.ConverterType);
    }

    public string Name => inner.Name;

    public bool AllowNulls => inner.AllowNulls;

    public bool CanWrite => inner.CanWrite;

    public Type Type => inner.Type;

    public Type? TypeOverride
    {
      get => inner.TypeOverride;
      set => inner.TypeOverride = value;
    }

    public int Order
    {
      get => inner.Order;
      set => inner.Order = value;
    }

    public ScalarStyle ScalarStyle
    {
      get => inner.ScalarStyle;
      set => inner.ScalarStyle = value;
    }

    public bool Required => inner.Required;

    public Type? ConverterType => converterType.Value;

    public T? GetCustomAttribute<T>() where T : Attribute
    {
      return (T?)attributes.GetOrAdd(typeof(T), _ => inner.GetCustomAttribute<T>());
    }

    public IObjectDescriptor Read(object target)
    {
      return inner.Read(target);
    }

    public void Write(object target, object? value)
    {
      inner.Write(target, value);
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