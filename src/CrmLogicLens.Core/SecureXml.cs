using System.Xml;
using System.Xml.Linq;

namespace CrmLogicLens.Core;

internal static class SecureXml
{
    public static XDocument Parse(string xml, int maxCharacters = 8 * 1024 * 1024)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = true
        };

        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    public static bool HasName(XElement element, string localName) =>
        element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<XElement> Descendants(XContainer container, string localName) =>
        container.Descendants().Where(element => HasName(element, localName));

    public static string? Attribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    public static string Location(XElement element, string description)
    {
        var lineInfo = (IXmlLineInfo)element;
        return lineInfo.HasLineInfo() ? $"line {lineInfo.LineNumber}: {description}" : description;
    }
}
