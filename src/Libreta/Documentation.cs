using System.Reflection;

namespace Libreta;

public static class Documentation
{
  public static void Write(DocumentationTopic topic, TextWriter writer)
  {
    if (topic == DocumentationTopic.Index)
    {
      writer.WriteLine("# Libreta documentation");
      writer.WriteLine();
      writer.WriteLine("Libreta keeps spreadsheets as plain text: inputs in CSV, formulas in YAML, rendered as live tables.");
      writer.WriteLine();
      writer.WriteLine("- `libreta --docs format` explains what Libreta does and defines the book format.");
      writer.WriteLine("- `libreta --docs running` explains the server, commands, checks, and export.");
      return;
    }

    string name = topic switch
    {
      DocumentationTopic.Format => "format",
      DocumentationTopic.Running => "running",
      _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, null)
    };
    Assembly assembly = typeof(Documentation).Assembly;
    using Stream stream = assembly.GetManifestResourceStream($"Libreta.Docs.{name}.md")
      ?? throw new InvalidOperationException($"Packaged documentation '{name}' is missing.");
    using var reader = new StreamReader(stream);
    writer.Write(reader.ReadToEnd());
  }


}