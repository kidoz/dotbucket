// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using AwesomeAssertions;
using DotBucket.Server.Configuration;

namespace DotBucket.Server.Tests.Configuration;

public class StartupValidatorTests
{
    private static StorageOptions ValidStorage() =>
        new()
        {
            RootPath = "storage",
            MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };

    private static AuthOptions ValidAuth() =>
        new() { RootAccessKey = "operator", RootSecretKey = "a-strong-secret" };

    [Fact]
    public void Production_MissingRootCredentials_IsFatal()
    {
        var result = StartupValidator.Validate(
            isDevelopment: false,
            new AuthOptions(),
            ValidStorage(),
            cluster: null
        );

        result.Errors.Should().Contain(e => e.Contains("RootAccessKey"));
    }

    [Fact]
    public void Development_MissingRootCredentials_IsWarningOnly()
    {
        var result = StartupValidator.Validate(
            isDevelopment: true,
            new AuthOptions(),
            ValidStorage(),
            cluster: null
        );

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void MasterKey_WrongLength_IsFatalEvenInDevelopment()
    {
        var storage = ValidStorage();
        storage.MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var result = StartupValidator.Validate(true, ValidAuth(), storage, cluster: null);

        result.Errors.Should().Contain(e => e.Contains("32 bytes"));
    }

    [Fact]
    public void Cluster_MissingTokenAndIdentity_IsFatalInProduction()
    {
        var cluster = new ClusterOptions { Enabled = true };

        var result = StartupValidator.Validate(false, ValidAuth(), ValidStorage(), cluster);

        result.Errors.Should().Contain(e => e.Contains("NodeId"));
        result.Errors.Should().Contain(e => e.Contains("ClusterToken"));
    }

    [Fact]
    public void ValidProductionConfig_HasNoErrors()
    {
        var result = StartupValidator.Validate(false, ValidAuth(), ValidStorage(), cluster: null);

        result.Errors.Should().BeEmpty();
    }
}
