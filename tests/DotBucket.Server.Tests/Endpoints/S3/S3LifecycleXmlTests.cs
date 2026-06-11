// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using AwesomeAssertions;
using DotBucket.Server.Endpoints.S3;

namespace DotBucket.Server.Tests.Endpoints.S3;

public class S3LifecycleXmlTests
{
    private const string Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public void Parse_DaysRule_WithFilterPrefix()
    {
        var xml = $"""
            <LifecycleConfiguration xmlns="{Ns}">
              <Rule>
                <ID>temp</ID>
                <Filter><Prefix>tmp/</Prefix></Filter>
                <Status>Enabled</Status>
                <Expiration><Days>7</Days></Expiration>
              </Rule>
            </LifecycleConfiguration>
            """;

        var config = S3LifecycleXml.Parse(xml);

        config.Rules.Should().ContainSingle();
        var rule = config.Rules[0];
        rule.Id.Should().Be("temp");
        rule.Prefix.Should().Be("tmp/");
        rule.Enabled.Should().BeTrue();
        rule.ExpirationDays.Should().Be(7);
        rule.ExpirationDate.Should().BeNull();
    }

    [Fact]
    public void Parse_LegacyTopLevelPrefix_AndDisabledStatus()
    {
        var xml = $"""
            <LifecycleConfiguration xmlns="{Ns}">
              <Rule>
                <Prefix>logs/</Prefix>
                <Status>Disabled</Status>
                <Expiration><Days>30</Days></Expiration>
              </Rule>
            </LifecycleConfiguration>
            """;

        var rule = S3LifecycleXml.Parse(xml).Rules[0];
        rule.Prefix.Should().Be("logs/");
        rule.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Parse_RejectsBothDaysAndDate()
    {
        var xml = $"""
            <LifecycleConfiguration xmlns="{Ns}">
              <Rule>
                <Status>Enabled</Status>
                <Expiration><Days>7</Days><Date>2030-01-01T00:00:00Z</Date></Expiration>
              </Rule>
            </LifecycleConfiguration>
            """;

        var act = () => S3LifecycleXml.Parse(xml);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_RejectsNonPositiveDays()
    {
        var xml = $"""
            <LifecycleConfiguration xmlns="{Ns}">
              <Rule>
                <Status>Enabled</Status>
                <Expiration><Days>0</Days></Expiration>
              </Rule>
            </LifecycleConfiguration>
            """;

        var act = () => S3LifecycleXml.Parse(xml);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void BuildThenParse_RoundTrips()
    {
        var xml = $"""
            <LifecycleConfiguration xmlns="{Ns}">
              <Rule>
                <ID>r1</ID>
                <Filter><Prefix>a/</Prefix></Filter>
                <Status>Enabled</Status>
                <Expiration><Days>14</Days></Expiration>
              </Rule>
            </LifecycleConfiguration>
            """;

        var roundTripped = S3LifecycleXml.Parse(S3LifecycleXml.Build(S3LifecycleXml.Parse(xml)));

        var rule = roundTripped.Rules.Should().ContainSingle().Subject;
        rule.Id.Should().Be("r1");
        rule.Prefix.Should().Be("a/");
        rule.Enabled.Should().BeTrue();
        rule.ExpirationDays.Should().Be(14);
    }
}
