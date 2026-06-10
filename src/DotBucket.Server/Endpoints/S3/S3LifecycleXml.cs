// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Xml.Linq;
using DotBucket.Server.Models;

namespace DotBucket.Server.Endpoints.S3;

/// <summary>
/// Parses and builds S3 BucketLifecycleConfiguration XML. Only object Expiration
/// (by Days or Date) is supported; other actions (e.g. AbortIncompleteMultipartUpload,
/// NoncurrentVersionExpiration) are tolerated on parse but not acted upon.
/// </summary>
public static class S3LifecycleXml
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>
    /// Parses lifecycle XML. Throws <see cref="FormatException"/> with a human-readable
    /// message when a rule is invalid (caller maps this to a MalformedXML response).
    /// </summary>
    public static LifecycleConfiguration Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException("Missing LifecycleConfiguration root.");
        var ns = root.Name.Namespace;

        var rules = new List<LifecycleRule>();
        foreach (var ruleEl in root.Elements(ns + "Rule"))
        {
            var id = ruleEl.Element(ns + "ID")?.Value;
            var status = ruleEl.Element(ns + "Status")?.Value;

            // Prefix may be at the top level (legacy) or inside Filter/Prefix.
            var prefix =
                ruleEl.Element(ns + "Prefix")?.Value
                ?? ruleEl.Element(ns + "Filter")?.Element(ns + "Prefix")?.Value;

            var expiration = ruleEl.Element(ns + "Expiration");
            int? days = null;
            DateTime? date = null;
            if (expiration != null)
            {
                var daysStr = expiration.Element(ns + "Days")?.Value;
                var dateStr = expiration.Element(ns + "Date")?.Value;

                var hasDays = !string.IsNullOrEmpty(daysStr);
                var hasDate = !string.IsNullOrEmpty(dateStr);

                if (hasDays && hasDate)
                    throw new FormatException("Expiration cannot specify both Days and Date.");

                if (hasDays)
                {
                    if (!int.TryParse(daysStr, out var d) || d < 1)
                        throw new FormatException("Expiration Days must be a positive integer.");
                    days = d;
                }
                else if (hasDate)
                {
                    if (
                        !DateTime.TryParse(
                            dateStr,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                            out var parsedDate
                        )
                    )
                        throw new FormatException("Expiration Date is not a valid date.");
                    // S3 requires midnight UTC.
                    date = parsedDate.Date;
                }
            }

            rules.Add(
                new LifecycleRule
                {
                    Id = id,
                    Prefix = prefix,
                    Enabled = string.Equals(status, "Enabled", StringComparison.Ordinal),
                    ExpirationDays = days,
                    ExpirationDate = date,
                }
            );
        }

        return new LifecycleConfiguration { Rules = rules };
    }

    public static string Build(LifecycleConfiguration config)
    {
        var doc = new XDocument(
            new XElement(
                S3Ns + "LifecycleConfiguration",
                config.Rules.Select(r =>
                {
                    XElement? expiration = null;
                    if (r.ExpirationDays.HasValue)
                        expiration = new XElement(
                            S3Ns + "Expiration",
                            new XElement(S3Ns + "Days", r.ExpirationDays.Value)
                        );
                    else if (r.ExpirationDate.HasValue)
                        expiration = new XElement(
                            S3Ns + "Expiration",
                            new XElement(
                                S3Ns + "Date",
                                r.ExpirationDate.Value.ToString(
                                    "yyyy-MM-ddTHH:mm:ss.fffZ",
                                    CultureInfo.InvariantCulture
                                )
                            )
                        );

                    return new XElement(
                        S3Ns + "Rule",
                        r.Id != null ? new XElement(S3Ns + "ID", r.Id) : null,
                        new XElement(
                            S3Ns + "Filter",
                            new XElement(S3Ns + "Prefix", r.Prefix ?? "")
                        ),
                        new XElement(S3Ns + "Status", r.Enabled ? "Enabled" : "Disabled"),
                        expiration
                    );
                })
            )
        );

        return doc.ToString();
    }
}
