using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OtpNet;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// TOTP secrets and recovery codes were plain text. These cover: plain text still verifies,
/// encrypted values round-trip, tampered values are rejected, and the conversion job is idempotent.
/// </summary>
public class MfaSecretEncryptionTests
{
  private const string Key = "mfa-secret-test-key-0123456789abcdef";
  private const string PlainSecret = "JBSWY3DPEHPK3PXP";

  private static EncryptionService Crypto(bool writeV2) => new(new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["Encryption:Key"] = Key,
      ["Encryption:WriteV2"] = writeV2 ? "true" : "false",
    }).Build());

  private static BuoyAIDbContext NewDb()
  {
    var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase($"mfa-secret-{Guid.NewGuid()}").Options);
    db.Database.EnsureCreated();
    return db;
  }

  private static string CurrentCode(string secret) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow);

  private static User TotpUser(int id, string? secret, string? recovery = null) => new()
  {
    Id = id,
    Username = $"mfa{id}",
    PasswordHash = "hash",
    Email = $"mfa{id}@example.com",
    FirstName = "T",
    LastName = "U",
    MfaType = MfaType.Totp,
    HasMfaEnabled = true,
    TotpSecret = secret,
    RecoveryCodes = recovery,
  };

  private static string Tamper(string v2Value)
  {
    var chars = v2Value.ToCharArray();
    var i = chars.Length - 3;
    chars[i] = chars[i] == 'A' ? 'B' : 'A';
    return new string(chars);
  }

  private static StepUpAuthService StepUp(BuoyAIDbContext db, IEncryptionService crypto) =>
    new(db,
      new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["Jwt:Key"] = "this-is-a-sufficiently-long-test-signing-key-1234567890",
        ["Jwt:Issuer"] = "BuoyAI",
        ["Jwt:Audience"] = "BuoyAI-Users",
      }).Build(),
      Mock.Of<ITwoFactorService>(),
      NullLogger<StepUpAuthService>.Instance,
      crypto);

  private static (TwoFactorService Service, BuoyAIDbContext Db) TwoFactor(IEncryptionService crypto)
  {
    var db = NewDb();
    var users = new Mock<IUserRepository>();
    users.Setup(r => r.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => db.Users.SingleOrDefault(u => u.Id == id));
    users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => { db.SaveChanges(); return u; });
    var service = new TwoFactorService(db, Mock.Of<IEmailService>(), users.Object,
      NullLogger<TwoFactorService>.Instance, Options.Create(new WebAuthnSettings()), crypto);
    return (service, db);
  }

  [Fact]
  public async Task StepUp_LegacyPlaintextSecret_StillVerifiesCode()
  {
    var db = NewDb();
    db.Users.Add(TotpUser(1, PlainSecret));
    await db.SaveChangesAsync();

    var result = await StepUp(db, Crypto(writeV2: true)).VerifyStepUpMfaAsync(1, CurrentCode(PlainSecret), ProtectedOperation.ChangePassword);

    result.Should().NotBeNull();
  }

  [Fact]
  public async Task StepUp_EncryptedSecret_VerifiesCode()
  {
    var crypto = Crypto(writeV2: true);
    var db = NewDb();
    db.Users.Add(TotpUser(1, crypto.Encrypt(PlainSecret)));
    await db.SaveChangesAsync();

    var result = await StepUp(db, crypto).VerifyStepUpMfaAsync(1, CurrentCode(PlainSecret), ProtectedOperation.ChangePassword);

    result.Should().NotBeNull();
  }

  [Fact]
  public async Task StepUp_TamperedEncryptedSecret_IsRejected()
  {
    var crypto = Crypto(writeV2: true);
    var db = NewDb();
    db.Users.Add(TotpUser(1, Tamper(crypto.Encrypt(PlainSecret))));
    await db.SaveChangesAsync();

    var result = await StepUp(db, crypto).VerifyStepUpMfaAsync(1, CurrentCode(PlainSecret), ProtectedOperation.ChangePassword);

    result.Should().BeNull();
  }

  [Fact]
  public async Task StepUp_EncryptedRecoveryCode_WorksOnceAndStaysEncrypted()
  {
    var crypto = Crypto(writeV2: true);
    var db = NewDb();
    db.Users.Add(TotpUser(1, crypto.Encrypt(PlainSecret), crypto.Encrypt("aaaa1111;bbbb2222")));
    await db.SaveChangesAsync();
    var service = StepUp(db, crypto);

    (await service.VerifyStepUpMfaAsync(1, "aaaa1111", ProtectedOperation.ChangePassword)).Should().NotBeNull();
    (await service.VerifyStepUpMfaAsync(1, "aaaa1111", ProtectedOperation.ChangePassword)).Should().BeNull();

    var stored = (await db.Users.SingleAsync()).RecoveryCodes!;
    stored.Should().StartWith("v2:");
    crypto.Decrypt(stored).Should().Be("bbbb2222");
  }

  [Fact]
  public async Task StepUp_LegacyPlaintextRecoveryCodes_StillWork()
  {
    var db = NewDb();
    db.Users.Add(TotpUser(1, PlainSecret, "aaaa1111;bbbb2222"));
    await db.SaveChangesAsync();

    var result = await StepUp(db, Crypto(writeV2: false)).VerifyStepUpMfaAsync(1, "bbbb2222", ProtectedOperation.ChangePassword);

    result.Should().NotBeNull();
    (await db.Users.SingleAsync()).RecoveryCodes.Should().Be("aaaa1111");
  }

  [Fact]
  public async Task StepUp_TamperedRecoveryCodes_AreRejected()
  {
    var crypto = Crypto(writeV2: true);
    var db = NewDb();
    db.Users.Add(TotpUser(1, crypto.Encrypt(PlainSecret), Tamper(crypto.Encrypt("aaaa1111"))));
    await db.SaveChangesAsync();

    var result = await StepUp(db, crypto).VerifyStepUpMfaAsync(1, "aaaa1111", ProtectedOperation.ChangePassword);

    result.Should().BeNull();
  }

  [Fact]
  public async Task Setup_WithWriteV2Off_StoresPlaintextAsBefore()
  {
    var (service, db) = TwoFactor(Crypto(writeV2: false));
    var user = TotpUser(1, null);
    user.MfaType = MfaType.None;
    db.Users.Add(user);
    await db.SaveChangesAsync();

    var setup = await service.EnableTotpMfaAsync(1);
    var codes = await service.CompleteTotpSetupAsync(1, CurrentCode(setup.Secret));

    var stored = await db.Users.SingleAsync();
    stored.TotpSecret.Should().Be(setup.Secret);
    stored.RecoveryCodes.Should().Be(string.Join(";", codes));
  }

  [Fact]
  public async Task Setup_WithWriteV2On_EncryptsBothAndStillCompletes()
  {
    var crypto = Crypto(writeV2: true);
    var (service, db) = TwoFactor(crypto);
    var user = TotpUser(1, null);
    user.MfaType = MfaType.None;
    db.Users.Add(user);
    await db.SaveChangesAsync();

    var setup = await service.EnableTotpMfaAsync(1);
    var codes = await service.CompleteTotpSetupAsync(1, CurrentCode(setup.Secret));

    var stored = await db.Users.SingleAsync();
    stored.TotpSecret.Should().StartWith("v2:").And.NotContain(setup.Secret);
    crypto.Decrypt(stored.TotpSecret!).Should().Be(setup.Secret);
    stored.RecoveryCodes.Should().StartWith("v2:");
    crypto.Decrypt(stored.RecoveryCodes!).Should().Be(string.Join(";", codes));
    stored.MfaType.Should().Be(MfaType.Totp);
  }

  [Fact]
  public async Task Setup_WithTamperedPendingSecret_Throws()
  {
    var crypto = Crypto(writeV2: true);
    var (service, db) = TwoFactor(crypto);
    var user = TotpUser(1, Tamper(crypto.Encrypt(PlainSecret)));
    user.MfaType = MfaType.None;
    db.Users.Add(user);
    await db.SaveChangesAsync();

    var act = async () => await service.CompleteTotpSetupAsync(1, CurrentCode(PlainSecret));

    await act.Should().ThrowAsync<Exception>();
  }

  // ---- conversion job ----

  private static (ServiceProvider Provider, EncryptionService Crypto, EncryptionReEncryptionService Job) Job()
  {
    var crypto = Crypto(writeV2: true);
    var dbName = Guid.NewGuid().ToString();
    var services = new ServiceCollection();
    services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName));
    services.AddSingleton<IEncryptionService>(crypto);
    var provider = services.BuildServiceProvider();
    return (provider, crypto, new EncryptionReEncryptionService(
      provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EncryptionReEncryptionService>.Instance, TimeSpan.Zero));
  }

  [Fact]
  public async Task Job_ConvertsPlaintextTotpAndRecoveryCodes_AndIsIdempotent()
  {
    var (p, crypto, job) = Job();
    using (var scope = p.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.Users.Add(TotpUser(1, PlainSecret, "aaaa1111;bbbb2222"));
      db.Users.Add(TotpUser(2, crypto.Encrypt("KRSXG5CTMVRXEZLU"), crypto.Encrypt("cccc3333")));
      var none = TotpUser(3, null);
      none.MfaType = MfaType.None;
      db.Users.Add(none);
      await db.SaveChangesAsync();
    }

    var first = await job.RunOnceAsync(CancellationToken.None);

    first.Converted.Should().Be(2);
    first.Failed.Should().Be(0);
    string?[] snapshot;
    using (var scope = p.CreateScope())
    {
      var users = await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.OrderBy(u => u.Id).ToListAsync();
      users[0].TotpSecret.Should().StartWith("v2:");
      crypto.Decrypt(users[0].TotpSecret!).Should().Be(PlainSecret);
      crypto.Decrypt(users[0].RecoveryCodes!).Should().Be("aaaa1111;bbbb2222");
      users[2].TotpSecret.Should().BeNull();
      snapshot = users.Select(u => u.TotpSecret).Concat(users.Select(u => u.RecoveryCodes)).ToArray();
    }

    var second = await job.RunOnceAsync(CancellationToken.None);

    second.Converted.Should().Be(0);
    using var scope2 = p.CreateScope();
    var after = await scope2.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.OrderBy(u => u.Id).ToListAsync();
    after.Select(u => u.TotpSecret).Concat(after.Select(u => u.RecoveryCodes)).Should().Equal(snapshot);
  }

  [Fact]
  public async Task Job_ConvertsMoreRowsThanOneBatch()
  {
    var (p, _, job) = Job();
    var count = EncryptionReEncryptionService.BatchSize * 2 + 3;
    using (var scope = p.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      for (var i = 1; i <= count; i++) db.Users.Add(TotpUser(i, PlainSecret));
      await db.SaveChangesAsync();
    }

    var result = await job.RunOnceAsync(CancellationToken.None);

    result.Converted.Should().Be(count);
  }

  [Fact]
  public async Task Job_RowChangedBetweenReadAndWrite_IsNotOverwritten()
  {
    var (p, crypto, job) = Job();
    using (var scope = p.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.Users.Add(TotpUser(1, PlainSecret, "aaaa1111;bbbb2222"));
      await db.SaveChangesAsync();
    }
    // The user spends a recovery code after the job has read the row.
    var calls = 0;
    job.BeforeMfaWriteHook = async id =>
    {
      if (++calls != 2) return; // first call is the TOTP column, second the recovery codes
      using var scope = p.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      var u = await db.Users.SingleAsync(x => x.Id == id);
      u.RecoveryCodes = "bbbb2222";
      await db.SaveChangesAsync();
    };

    var first = await job.RunOnceAsync(CancellationToken.None);

    using (var scope = p.CreateScope())
    {
      var u = await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.SingleAsync();
      u.RecoveryCodes.Should().Be("bbbb2222", "the spent code must not come back, and the stale value is not written");
      crypto.Decrypt(u.TotpSecret!).Should().Be(PlainSecret);
    }
    first.Converted.Should().Be(1, "only the TOTP secret; the changed recovery row was skipped");

    // Next pass picks the skipped row up.
    job.BeforeMfaWriteHook = null;
    await job.RunOnceAsync(CancellationToken.None);
    using var scope2 = p.CreateScope();
    var after = await scope2.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.SingleAsync();
    crypto.Decrypt(after.RecoveryCodes!).Should().Be("bbbb2222");
  }

  private static (ServiceProvider Provider, EncryptionService Crypto, EncryptionReEncryptionService Job) RelationalJob()
  {
    var crypto = Crypto(writeV2: true);
    // Real relational provider, so the conditional ExecuteUpdate branch runs (InMemory takes the fallback).
    var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:;Foreign Keys=False");
    connection.Open();
    var services = new ServiceCollection();
    services.AddDbContext<BuoyAIDbContext>(o => o.UseSqlite(connection));
    services.AddSingleton<IEncryptionService>(crypto);
    var provider = services.BuildServiceProvider();
    using (var init = provider.CreateScope())
      init.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Database.EnsureCreated();
    return (provider, crypto, new EncryptionReEncryptionService(
      provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EncryptionReEncryptionService>.Instance, TimeSpan.Zero));
  }

  [Fact]
  public async Task Job_Relational_ConvertsPlaintextRows()
  {
    var (p, crypto, job) = RelationalJob();
    using (var scope = p.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.Users.Add(TotpUser(1, PlainSecret, "aaaa1111;bbbb2222"));
      await db.SaveChangesAsync();
    }

    var result = await job.RunOnceAsync(CancellationToken.None);

    result.Failed.Should().Be(0);
    using var scope2 = p.CreateScope();
    var u = await scope2.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.SingleAsync();
    crypto.Decrypt(u.TotpSecret!).Should().Be(PlainSecret);
    crypto.Decrypt(u.RecoveryCodes!).Should().Be("aaaa1111;bbbb2222");
  }

  [Fact]
  public async Task Job_Relational_RowChangedBetweenReadAndWrite_IsNotOverwritten()
  {
    var (p, crypto, job) = RelationalJob();
    using (var scope = p.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.Users.Add(TotpUser(1, PlainSecret, "aaaa1111;bbbb2222"));
      await db.SaveChangesAsync();
    }
    var calls = 0;
    job.BeforeMfaWriteHook = async id =>
    {
      if (++calls != 2) return;
      using var scope = p.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      var u = await db.Users.SingleAsync(x => x.Id == id);
      u.RecoveryCodes = "bbbb2222";
      await db.SaveChangesAsync();
    };

    await job.RunOnceAsync(CancellationToken.None);

    using (var scope = p.CreateScope())
    {
      var u = await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.SingleAsync();
      u.RecoveryCodes.Should().Be("bbbb2222");
      crypto.Decrypt(u.TotpSecret!).Should().Be(PlainSecret);
    }

    job.BeforeMfaWriteHook = null;
    await job.RunOnceAsync(CancellationToken.None);
    using var scope2 = p.CreateScope();
    var after = await scope2.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.SingleAsync();
    crypto.Decrypt(after.RecoveryCodes!).Should().Be("bbbb2222");
  }

  // ---- login MFA path (AuthService.CompleteMfaLoginAsync) ----

  private static (AuthService Auth, BuoyAIDbContext Db, Mock<IUserRepository> Users) Auth(IEncryptionService crypto)
  {
    var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase($"mfa-login-{Guid.NewGuid()}")
      .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    var users = new Mock<IUserRepository>();
    users.Setup(r => r.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => db.Users.SingleOrDefault(u => u.Id == id));
    users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => { db.SaveChanges(); return u; });
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["Jwt:Key"] = "this-is-a-sufficiently-long-test-signing-key-1234567890",
      ["Jwt:Issuer"] = "BuoyAI",
      ["Jwt:Audience"] = "BuoyAI-Users",
      ["App:BaseUrl"] = "http://localhost:3000",
    }).Build();
    var env = new Mock<IHostEnvironment>();
    env.Setup(e => e.EnvironmentName).Returns("Production");
    var tracker = new Mock<ILastSyncTracker>();
    tracker.Setup(t => t.IsDataStale(It.IsAny<int>(), It.IsAny<TimeSpan>())).ReturnsAsync(false);
    var auth = new AuthService(
      users.Object, config, new Lazy<IBankAggregatorRegistry>(() => Mock.Of<IBankAggregatorRegistry>()), tracker.Object, NullLogger<AuthService>.Instance, db,
      Mock.Of<IEmailService>(), Mock.Of<IEmailPreferencesService>(), env.Object,
      new ServiceCollection().BuildServiceProvider(), Mock.Of<IEntitlementService>(), Mock.Of<INotificationService>(),
      Mock.Of<IDataRetentionManager>(), Mock.Of<IFamilyInvitationRepository>(), Mock.Of<IGoogleTokenValidator>(),
      Mock.Of<IAppleTokenValidator>(), Mock.Of<IRefreshTokenService>(), new BuoyAiMetrics(new TestMeterFactory()),
      crypto, Mock.Of<IAppleAuthClient>(), Mock.Of<IServiceScopeFactory>(), Mock.Of<IMarketingLeadService>());
    return (auth, db, users);
  }

  [Fact]
  public async Task Login_EncryptedTotpSecret_Verifies()
  {
    var crypto = Crypto(writeV2: true);
    var (auth, db, _) = Auth(crypto);
    db.Users.Add(TotpUser(1, crypto.Encrypt(PlainSecret)));
    await db.SaveChangesAsync();

    var result = await auth.CompleteMfaLoginAsync(await auth.GenerateMfaTokenAsync(1), CurrentCode(PlainSecret));

    result.Should().NotBeNull();
  }

  [Fact]
  public async Task Login_EncryptedRecoveryCode_WorksOnce_AndRemainderStaysEncrypted()
  {
    var crypto = Crypto(writeV2: true);
    var (auth, db, _) = Auth(crypto);
    db.Users.Add(TotpUser(1, crypto.Encrypt(PlainSecret), crypto.Encrypt("aaaa1111;bbbb2222")));
    await db.SaveChangesAsync();

    (await auth.CompleteMfaLoginAsync(await auth.GenerateMfaTokenAsync(1), "aaaa1111")).Should().NotBeNull();
    (await auth.CompleteMfaLoginAsync(await auth.GenerateMfaTokenAsync(1), "aaaa1111")).Should().BeNull();

    var stored = (await db.Users.SingleAsync()).RecoveryCodes!;
    stored.Should().StartWith("v2:");
    crypto.Decrypt(stored).Should().Be("bbbb2222");
  }

  [Fact]
  public async Task Login_TamperedSecretAndCodes_AreRejectedWithoutThrowing()
  {
    var crypto = Crypto(writeV2: true);
    var (auth, db, _) = Auth(crypto);
    db.Users.Add(TotpUser(1, Tamper(crypto.Encrypt(PlainSecret)), Tamper(crypto.Encrypt("aaaa1111"))));
    await db.SaveChangesAsync();

    (await auth.CompleteMfaLoginAsync(await auth.GenerateMfaTokenAsync(1), CurrentCode(PlainSecret))).Should().BeNull();
    (await auth.CompleteMfaLoginAsync(await auth.GenerateMfaTokenAsync(1), "aaaa1111")).Should().BeNull();
  }
}
