using System.Xml.Linq;

namespace CensusScope.Core.Parsing;

/// <summary>One code from an SDMX-ML codelist: id plus English name and optional English description.</summary>
public sealed record SdmxCode(string Id, string NameEn, string? DescriptionEn);

/// <summary>
/// Parses SDMX-ML codelist responses (e.g. Statistics Canada CL_GEO_* geography codelists).
/// Elements are matched by local name so the parser is agnostic to namespace prefixes.
/// </summary>
public static class SdmxCodelistParser
{
    private static readonly XName XmlLang = XNamespace.Xml + "lang";

    /// <summary>
    /// Extracts all codelist codes from an SDMX-ML document. A code is any element with the
    /// local name "Code" carrying an "id" attribute; its English name comes from the child
    /// "Name" element with xml:lang="en" (falling back to the first Name child when no
    /// language matches), and its description from the child "Description" with xml:lang="en".
    /// </summary>
    /// <param name="xml">SDMX-ML codelist document text.</param>
    /// <returns>All codes found, in document order.</returns>
    public static List<SdmxCode> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var result = new List<SdmxCode>();

        foreach (var code in doc.Descendants().Where(e => e.Name.LocalName == "Code"))
        {
            var id = code.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(id))
                continue; // not an actual codelist code

            var names = code.Elements().Where(e => e.Name.LocalName == "Name").ToList();
            var name = names.FirstOrDefault(e => Lang(e) == "en") ?? names.FirstOrDefault();

            var description = code.Elements()
                .Where(e => e.Name.LocalName == "Description")
                .FirstOrDefault(e => Lang(e) == "en");

            result.Add(new SdmxCode(id, name?.Value.Trim() ?? "", description?.Value.Trim()));
        }
        return result;
    }

    private static string? Lang(XElement element) => element.Attribute(XmlLang)?.Value;
}
