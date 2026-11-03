using BuoyAI.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuoyAI.UnitTests.EditionSetting;

public class EditionDefaultsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bm-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData(null, BuoyAI.Infrastructure.Configuration.Edition.Hosted)]
    [InlineData("", BuoyAI.Infrastructure.Configuration.Edition.Hosted)]
    [InlineData("hosted", BuoyAI.Infrastructure.Configuration.Edition.Hosted)]
    [InlineData("HOSTED", BuoyAI.Infrastructure.Configuration.Edition.Hosted)]
    [InlineData("selfhost", BuoyAI.Infrastructure.Configuration.Edition.SelfHost)]
    [InlineData(" SelfHost ", BuoyAI.Infrastructure.Configuration.Edition.SelfHost)]
    [InlineData("self-host", BuoyAI.Infrastructure.Configuration.Edition.SelfHost)]
    public void Parse_AcceptsTheKnownNames(string? value, BuoyAI.Infrastructure.Configuration.Edition expected) =>
        EditionInfo.Parse(value).Should().Be(expected);

    [Fact]
    public void Parse_RefusesATypoInsteadOfGuessingHosted()
    {
        var act = () => EditionInfo.Parse("selfhsot");
        act.Should().Throw<InvalidOperationException>().WithMessage("*BuoyMoney:Edition*");
    }

    [Fact]
    public void Hosted_AddsNothing()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>());
        var edition = EditionDefaults.Apply(builder, builder.Build());

        edition.IsHosted.Should().BeTrue();
        var config = builder.Build();
        config["Migrations:BackupBeforeMigrate"].Should().BeNull();
        config["Encryption:WriteV2"].Should().BeNull();
        config.GetSection("ForwardedHeaders").GetChildren().Should().BeEmpty();
    }

    [Fact]
    public void SelfHost_TurnsOnBackupAndV2Encryption_AndNamesTrustedNetworks()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BuoyMoney:Edition"] = "selfhost",
            ["BuoyMoney:SecretsDirectory"] = _dir,
        });
        var edition = EditionDefaults.Apply(builder, builder.Build());

        edition.IsSelfHost.Should().BeTrue();
        var config = builder.Build();
        config.GetValue<bool>("Migrations:BackupBeforeMigrate").Should().BeTrue();
        config.GetValue<bool>("Encryption:WriteV2").Should().BeTrue();
        config.GetValue<bool>("Https:RedirectEnabled").Should().BeFalse();
        var networks = config.GetSection("ForwardedHeaders:TrustedNetworks").Get<string[]>();
        networks.Should().Contain("127.0.0.0/8");
        networks.Should().NotContain(new[] { "192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12", "fc00::/7" },
            "private ranges can be a port-forwarded client behind Docker's gateway");
        networks.Should().NotContain("0.0.0.0/0", "a public client must never be a trusted proxy");
    }

    [Fact]
    public void SelfHost_Defaults_YieldToAnExplicitSetting()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BuoyMoney:Edition"] = "selfhost",
            ["BuoyMoney:SecretsDirectory"] = _dir,
        });
        EditionDefaults.Apply(builder, builder.Build());
        // Added after, as appsettings and environment variables are: they must win.
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Migrations:BackupBeforeMigrate"] = "false",
            ["Encryption:WriteV2"] = "false",
        });

        var config = builder.Build();
        config.GetValue<bool>("Migrations:BackupBeforeMigrate").Should().BeFalse();
        config.GetValue<bool>("Encryption:WriteV2").Should().BeFalse();
    }

    [Fact]
    public void SelfHost_ReadsGeneratedSecretsFromFiles_AndAnExplicitSettingStillWins()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Jwt__Key"), "generated-jwt-key-0123456789abcdef0123456789\n");
        File.WriteAllText(Path.Combine(_dir, "ConnectionStrings__BuoyAI"), "Host=db;Database=buoy");
        File.WriteAllText(Path.Combine(_dir, "Encryption__Key"), "from-the-file-xxxxxxxxxxxxxxxxxxxxxxx");
        File.WriteAllText(Path.Combine(_dir, "Empty__One"), "  \n");
        File.WriteAllText(Path.Combine(_dir, ".hidden"), "ignored");

        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BuoyMoney:Edition"] = "selfhost",
            ["BuoyMoney:SecretsDirectory"] = _dir,
        });
        EditionDefaults.Apply(builder, builder.Build());
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = "from-the-environment-xxxxxxxxxxxx" });

        var config = builder.Build();
        config["Jwt:Key"].Should().Be("generated-jwt-key-0123456789abcdef0123456789");
        config.GetConnectionString("BuoyAI").Should().Be("Host=db;Database=buoy");
        config["Encryption:Key"].Should().Be("from-the-environment-xxxxxxxxxxxx");
        config["Empty:One"].Should().BeNull();
    }

    [Fact]
    public void SelfHost_ASecretFileBeatsABlankSetting_ButNotAFilledOne()
    {
        // compose.yaml maps every optional setting, so an unset one is an empty environment variable.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Oidc__ClientSecret"), "secret-from-the-volume");
        File.WriteAllText(Path.Combine(_dir, "Smtp__Password"), "file-password");

        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BuoyMoney:Edition"] = "selfhost",
            ["BuoyMoney:SecretsDirectory"] = _dir,
            ["Oidc:ClientSecret"] = "",
            ["Smtp:Password"] = "set-in-the-environment",
        });
        EditionDefaults.Apply(builder, builder.Build());

        var config = builder.Build();
        config["Oidc:ClientSecret"].Should().Be("secret-from-the-volume");
        config["Smtp:Password"].Should().Be("set-in-the-environment");
    }

    [Fact]
    public void Hosted_IgnoresASecretsFolder()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Jwt__Key"), "should-not-load");
        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BuoyMoney:SecretsDirectory"] = _dir,
        });
        EditionDefaults.Apply(builder, builder.Build());

        builder.Build()["Jwt:Key"].Should().BeNull();
    }
}
