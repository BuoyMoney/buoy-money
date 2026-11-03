using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class EncryptionReEncryptionServiceTests
{
  private const string Key = "reencrypt-test-key-0123456789abcdef";

  private static string LegacyEncrypt(string plainText)
  {
    using var aes = Aes.Create();
    aes.Key = Encoding.UTF8.GetBytes(Key.PadRight(32).Substring(0, 32));
    aes.GenerateIV();
    using var ms = new MemoryStream();
    ms.Write(aes.IV, 0, aes.IV.Length);
    using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
    using (var sw = new StreamWriter(cs))
    {
      sw.Write(plainText);
    }
    return Convert.ToBase64String(ms.ToArray());
  }

  private static (ServiceProvider Provider, EncryptionService Crypto, EncryptionReEncryptionService Job) Build()
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = Key, ["Encryption:WriteV2"] = "true" })
      .Build();
    var crypto = new EncryptionService(config);
    // Real relational provider: the job uses conditional ExecuteUpdate, which InMemory cannot run.
    var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:;Foreign Keys=False");
    connection.Open();
    var services = new ServiceCollection();
    services.AddDbContext<BuoyAIDbContext>(o => o.UseSqlite(connection));
    services.AddSingleton<BuoyAI.Core.Interfaces.IEncryptionService>(crypto);
    var provider = services.BuildServiceProvider();
    using (var init = provider.CreateScope())
      init.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Database.EnsureCreated();
    var job = new EncryptionReEncryptionService(
      provider.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<EncryptionReEncryptionService>.Instance,
      TimeSpan.Zero);
    return (provider, crypto, job);
  }

  private static async Task SeedAsync(ServiceProvider p, EncryptionService crypto, int legacyCount, int currentCount)
  {
    using var scope = p.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    for (var i = 0; i < legacyCount; i++)
      db.PlaidConnections.Add(new PlaidConnection { UserId = 1, ItemId = $"legacy-{i}", AccessToken = LegacyEncrypt($"access-legacy-{i}") });
    for (var i = 0; i < currentCount; i++)
      db.PlaidConnections.Add(new PlaidConnection { UserId = 1, ItemId = $"current-{i}", AccessToken = crypto.Encrypt($"access-current-{i}") });
    await db.SaveChangesAsync();
  }

  [Fact]
  public async Task RunOnce_ConvertsLegacyRows_AndPreservesPlaintext()
  {
    var (p, crypto, job) = Build();
    await SeedAsync(p, crypto, legacyCount: 3, currentCount: 1);

    var result = await job.RunOnceAsync(CancellationToken.None);

    result.Converted.Should().Be(3);
    result.Failed.Should().Be(0);
    using var scope = p.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var rows = await db.PlaidConnections.ToListAsync();
    rows.Should().OnlyContain(r => r.AccessToken.StartsWith("v2:"));
    crypto.Decrypt(rows.Single(r => r.ItemId == "legacy-2").AccessToken).Should().Be("access-legacy-2");
    crypto.Decrypt(rows.Single(r => r.ItemId == "current-0").AccessToken).Should().Be("access-current-0");
  }

  [Fact]
  public async Task RunOnce_DoesNotOverwriteATokenStoredAfterItWasRead()
  {
    var (p, crypto, job) = Build();
    await SeedAsync(p, crypto, legacyCount: 1, currentCount: 0);
    // Simulate an item-update landing between the batch read and the write: the job's decrypt
    // hook is the only point between the two, so swap the row from inside a wrapping crypto.
    var racing = new RacingEncryption(crypto, () =>
    {
      using var scope = p.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.PlaidConnections.Single().AccessToken = crypto.Encrypt("fresh-token");
      db.SaveChanges();
    });
    var raceJob = new EncryptionReEncryptionService(
      new ReplacingScopeFactory(p.GetRequiredService<IServiceScopeFactory>(), racing),
      NullLogger<EncryptionReEncryptionService>.Instance,
      TimeSpan.Zero);

    var result = await raceJob.RunOnceAsync(CancellationToken.None);

    result.Converted.Should().Be(0);
    using var check = p.CreateScope();
    var row = await check.ServiceProvider.GetRequiredService<BuoyAIDbContext>().PlaidConnections.SingleAsync();
    crypto.Decrypt(row.AccessToken).Should().Be("fresh-token");
  }

  private sealed class RacingEncryption : BuoyAI.Core.Interfaces.IEncryptionService
  {
    private readonly BuoyAI.Core.Interfaces.IEncryptionService _inner;
    private readonly Action _onEncrypt;
    public RacingEncryption(BuoyAI.Core.Interfaces.IEncryptionService inner, Action onEncrypt) { _inner = inner; _onEncrypt = onEncrypt; }
    public string Decrypt(string cipherText) => _inner.Decrypt(cipherText);
    public bool IsCurrentFormat(string? value) => _inner.IsCurrentFormat(value);
    public bool WritesCurrentFormat => _inner.WritesCurrentFormat;
    public string Encrypt(string plainText) { var r = _inner.Encrypt(plainText); _onEncrypt(); return r; }
  }

  private sealed class ReplacingScopeFactory : IServiceScopeFactory
  {
    private readonly IServiceScopeFactory _inner;
    private readonly BuoyAI.Core.Interfaces.IEncryptionService _crypto;
    public ReplacingScopeFactory(IServiceScopeFactory inner, BuoyAI.Core.Interfaces.IEncryptionService crypto) { _inner = inner; _crypto = crypto; }
    public IServiceScope CreateScope() => new Scope(_inner.CreateScope(), _crypto);
    private sealed class Scope : IServiceScope, IServiceProvider
    {
      private readonly IServiceScope _s; private readonly BuoyAI.Core.Interfaces.IEncryptionService _c;
      public Scope(IServiceScope s, BuoyAI.Core.Interfaces.IEncryptionService c) { _s = s; _c = c; }
      public IServiceProvider ServiceProvider => this;
      public object? GetService(Type t) => t == typeof(BuoyAI.Core.Interfaces.IEncryptionService) ? _c : _s.ServiceProvider.GetService(t);
      public void Dispose() => _s.Dispose();
    }
  }

  [Fact]
  public async Task RunOnce_IsIdempotent_SecondPassChangesNothing()
  {
    var (p, crypto, job) = Build();
    await SeedAsync(p, crypto, legacyCount: 2, currentCount: 0);
    await job.RunOnceAsync(CancellationToken.None);
    string[] before;
    using (var scope = p.CreateScope())
      before = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().PlaidConnections
        .OrderBy(c => c.Id).Select(c => c.AccessToken).ToArray();

    var second = await job.RunOnceAsync(CancellationToken.None);

    second.Converted.Should().Be(0);
    using var scope2 = p.CreateScope();
    var after = scope2.ServiceProvider.GetRequiredService<BuoyAIDbContext>().PlaidConnections
      .OrderBy(c => c.Id).Select(c => c.AccessToken).ToArray();
    after.Should().Equal(before);
  }

  [Fact]
  public async Task RunOnce_HandlesMoreRowsThanOneBatch()
  {
    var (p, crypto, job) = Build();
    var count = EncryptionReEncryptionService.BatchSize * 2 + 7;
    await SeedAsync(p, crypto, legacyCount: count, currentCount: 0);

    var result = await job.RunOnceAsync(CancellationToken.None);

    result.Converted.Should().Be(count);
  }

  [Fact]
  public async Task RunOnce_UnreadableRow_IsSkippedNotLooped_AndOthersConvert()
  {
    var (p, crypto, job) = Build();
    await SeedAsync(p, crypto, legacyCount: 2, currentCount: 0);
    using (var scope = p.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.PlaidConnections.Add(new PlaidConnection { UserId = 1, ItemId = "bad", AccessToken = "not-valid-ciphertext" });
      await db.SaveChangesAsync();
    }

    var result = await job.RunOnceAsync(CancellationToken.None);

    result.Converted.Should().Be(2);
    result.Failed.Should().Be(1);
    using var scope2 = p.CreateScope();
    var bad = await scope2.ServiceProvider.GetRequiredService<BuoyAIDbContext>().PlaidConnections.SingleAsync(c => c.ItemId == "bad");
    bad.AccessToken.Should().Be("not-valid-ciphertext");
  }

  [Fact]
  public async Task RunOnce_ConvertsAppleRefreshTokens_AndSkipsUsersWithoutOne()
  {
    var (p, crypto, job) = Build();
    using (var scope = p.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.Users.Add(new User { Username = "a", Email = "a@example.com", AppleRefreshToken = LegacyEncrypt("apple-rt") });
      db.Users.Add(new User { Username = "b", Email = "b@example.com", AppleRefreshToken = null });
      await db.SaveChangesAsync();
    }

    var result = await job.RunOnceAsync(CancellationToken.None);

    result.Converted.Should().Be(1);
    using var scope2 = p.CreateScope();
    var users = await scope2.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.OrderBy(u => u.Id).ToListAsync();
    crypto.Decrypt(users[0].AppleRefreshToken!).Should().Be("apple-rt");
    users[1].AppleRefreshToken.Should().BeNull();
  }

  [Fact]
  public async Task Job_WhenWriteV2IsOff_DoesNothing()
  {
    var (p, crypto, _) = Build();
    await SeedAsync(p, crypto, legacyCount: 2, currentCount: 0);
    var job = new EncryptionReEncryptionService(
      p.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<EncryptionReEncryptionService>.Instance,
      TimeSpan.Zero,
      enabled: false);

    await job.StartAsync(CancellationToken.None);
    await (job.ExecuteTask ?? Task.CompletedTask);

    using var scope = p.CreateScope();
    var rows = await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().PlaidConnections.ToListAsync();
    rows.Should().OnlyContain(r => !r.AccessToken.StartsWith("v2:"));
  }

  [Fact]
  public void Job_ConfigGate_NeedsWriteV2AndReEncryptNotDisabled()
  {
    var sf = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    bool Enabled(params (string, string)[] kv)
    {
      var cfg = new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(k => k.Item1, k => (string?)k.Item2)).Build();
      var job = new EncryptionReEncryptionService(sf, NullLogger<EncryptionReEncryptionService>.Instance, cfg);
      return (bool)typeof(EncryptionReEncryptionService).GetField("_enabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(job)!;
    }

    Enabled().Should().BeFalse();
    Enabled(("Encryption:WriteV2", "true")).Should().BeTrue();
    Enabled(("Encryption:WriteV2", "true"), ("Encryption:ReEncryptEnabled", "false")).Should().BeFalse();
    Enabled(("Encryption:ReEncryptEnabled", "true")).Should().BeFalse();
  }
}
