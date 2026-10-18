using System.Globalization;
using System.Text;
using System.Xml;

namespace PictureSharp.Formats.Heif.Codecs;

// The simple and ordered-array properties of an XMP packet, keyed by namespace URI and local name. Values come from
// rdf:Description attributes and from child elements (text or rdf:Seq/Bag/Alt items); nested structures are skipped.
// Parsing prohibits DTDs and external resources; a malformed packet yields null.
internal sealed class XmpProperties
{
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private readonly Dictionary<(string Namespace, string Name), string> values = [];
    private readonly Dictionary<(string Namespace, string Name), string[]> arrays = [];

    private XmpProperties()
    {
    }

    public static XmpProperties? Parse(ReadOnlySpan<byte> xmp)
    {
        // Packets are often padded with NUL bytes or whitespace after the trailer.
        xmp = xmp.TrimEnd((byte)0);
        if (xmp.IsEmpty)
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            ConformanceLevel = ConformanceLevel.Document,
        };

        var properties = new XmpProperties();
        try
        {
            using var text = new StringReader(Encoding.UTF8.GetString(xmp).TrimStart('﻿'));
            using var reader = XmlReader.Create(text, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Description" && reader.NamespaceURI == Rdf)
                {
                    properties.ReadDescription(reader);
                }
            }
        }
        catch (XmlException)
        {
            return null;
        }

        return properties;
    }

    public string? Get(string ns, string name) => values.GetValueOrDefault((ns, name));

    public string[]? GetArray(string ns, string name) => arrays.GetValueOrDefault((ns, name));

    public int? GetInt(string ns, string name) =>
        int.TryParse(Get(ns, name)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    public float? GetFloat(string ns, string name) => ParseFloat(Get(ns, name));

    public float[]? GetFloats(string ns, string name)
    {
        if (GetArray(ns, name) is not { } items)
        {
            return null;
        }

        var result = new float[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            if (ParseFloat(items[i]) is not { } value)
            {
                return null;
            }

            result[i] = value;
        }

        return result;
    }

    private static float? ParseFloat(string? text) =>
        float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
            ? value : null;

    private void ReadDescription(XmlReader reader)
    {
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.NamespaceURI is not (Rdf or "http://www.w3.org/2000/xmlns/" or ""))
                {
                    values.TryAdd((reader.NamespaceURI, reader.LocalName), reader.Value);
                }
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        if (reader.IsEmptyElement)
        {
            return;
        }

        int depth = reader.Depth;
        reader.Read();
        while (reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                ReadProperty(reader);
            }
            else
            {
                reader.Read();
            }
        }
    }

    // Leaves the reader on the node after the property element.
    private void ReadProperty(XmlReader reader)
    {
        (string, string) key = (reader.NamespaceURI, reader.LocalName);
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return;
        }

        int depth = reader.Depth;
        var text = new StringBuilder();
        List<string>? items = null;
        bool nested = false;
        reader.Read();
        while (reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && reader.NamespaceURI == Rdf
                && reader.LocalName is "Seq" or "Bag" or "Alt")
            {
                items ??= [];
                reader.Read();
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 2 && items is not null
                && reader.NamespaceURI == Rdf && reader.LocalName == "li")
            {
                items.Add(ReadSimpleContent(reader));
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && reader.Depth == depth + 1)
            {
                text.Append(reader.Value);
                reader.Read();
            }
            else
            {
                nested |= reader.NodeType == XmlNodeType.Element;
                reader.Read();
            }
        }

        // The end element of the property.
        reader.Read();
        if (items is not null)
        {
            arrays.TryAdd(key, [.. items]);
        }
        else if (!nested)
        {
            values.TryAdd(key, text.ToString());
        }
    }

    // The text of an array item, or empty for a structured item; leaves the reader after the item.
    private static string ReadSimpleContent(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return "";
        }

        int depth = reader.Depth;
        var text = new StringBuilder();
        bool nested = false;
        reader.Read();
        while (reader.Depth > depth)
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                text.Append(reader.Value);
            }

            nested |= reader.NodeType == XmlNodeType.Element;
            reader.Read();
        }

        reader.Read();
        return nested ? "" : text.ToString();
    }
}
