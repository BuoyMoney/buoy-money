using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace BuoyAI.UnitTests.Services.Import;

/// <summary>
/// Builds a small, synthetic Actual Budget export (a zip with db.sqlite and metadata.json).
/// Two schema spellings are supported because Actual renamed columns between versions.
/// </summary>
internal static class ActualFixture
{
  public static byte[] Build(bool newerColumnNames = false)
  {
    var dbPath = Path.Combine(Path.GetTempPath(), $"actual-fixture-{Guid.NewGuid():N}.sqlite");
    try
    {
      using (var db = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
      {
        db.Open();
        var cat = newerColumnNames ? "category_id" : "category";
        var payee = newerColumnNames ? "payee_id" : "description";
        var isParent = newerColumnNames ? "is_parent" : "isParent";
        var isChild = newerColumnNames ? "is_child" : "isChild";

        Exec(db, "CREATE TABLE accounts (id TEXT PRIMARY KEY, name TEXT, offbudget INTEGER DEFAULT 0, closed INTEGER DEFAULT 0, type TEXT, subtype TEXT, tombstone INTEGER DEFAULT 0)");
        Exec(db, "CREATE TABLE payees (id TEXT PRIMARY KEY, name TEXT, transfer_acct TEXT, tombstone INTEGER DEFAULT 0)");
        Exec(db, "CREATE TABLE payee_mapping (id TEXT PRIMARY KEY, targetId TEXT)");
        Exec(db, "CREATE TABLE category_groups (id TEXT PRIMARY KEY, name TEXT, is_income INTEGER DEFAULT 0, tombstone INTEGER DEFAULT 0)");
        Exec(db, "CREATE TABLE categories (id TEXT PRIMARY KEY, name TEXT, cat_group TEXT, is_income INTEGER DEFAULT 0, tombstone INTEGER DEFAULT 0)");
        Exec(db, "CREATE TABLE category_mapping (id TEXT PRIMARY KEY, targetId TEXT)");
        Exec(db, $"CREATE TABLE transactions (id TEXT PRIMARY KEY, acct TEXT, {cat} TEXT, amount INTEGER, {payee} TEXT, notes TEXT, date INTEGER, financial_id TEXT, imported_description TEXT, starting_balance_flag INTEGER DEFAULT 0, {isParent} INTEGER DEFAULT 0, {isChild} INTEGER DEFAULT 0, parent_id TEXT, transferred_id TEXT, tombstone INTEGER DEFAULT 0)");
        Exec(db, "CREATE TABLE zero_budgets (id TEXT PRIMARY KEY, month INTEGER, category TEXT, amount INTEGER, carryover INTEGER DEFAULT 0)");

        Exec(db, "INSERT INTO accounts (id,name,offbudget,closed,type) VALUES ('a1','Checking',0,0,'checking'),('a2','Savings',0,0,'savings'),('a3','Old Card',0,1,'credit'),('a4','Brokerage',1,0,NULL),('a5','Deleted',0,0,NULL)");
        Exec(db, "UPDATE accounts SET tombstone = 1 WHERE id = 'a5'");

        Exec(db, "INSERT INTO payees (id,name,transfer_acct) VALUES ('p1','Grocery Mart',NULL),('p2','Employer',NULL),('p3','Transfer : Savings','a2'),('p4','Transfer : Checking','a1'),('p5','Starting Balance',NULL),('p6','Grocery Mart (old)',NULL),('p7','Big Store',NULL)");
        Exec(db, "INSERT INTO payee_mapping (id,targetId) VALUES ('p6','p1')");

        Exec(db, "INSERT INTO category_groups (id,name,is_income) VALUES ('g1','Everyday',0),('g2','Income',1)");
        Exec(db, "INSERT INTO categories (id,name,cat_group,is_income) VALUES ('c1','Groceries','g1',0),('c2','Salary','g2',1),('c3','Fun','g1',0),('c4','Gone','g1',0)");
        Exec(db, "UPDATE categories SET tombstone = 1 WHERE id = 'c4'");
        Exec(db, "INSERT INTO category_mapping (id,targetId) VALUES ('c4','c1')");

        // Starting balance, groceries (payee merged away), paycheck, a transfer pair, a deleted row, a split.
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,{payee},notes,date,starting_balance_flag) VALUES ('t0','a1',NULL,200000,'p5',NULL,20260101,1)");
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,{payee},notes,date,imported_description) VALUES ('t1','a1','c1',-4520,'p6','weekly',20260115,'GROCERY MART #12')");
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,{payee},date) VALUES ('t2','a1','c2',150000,'p2',20260103)");
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,{payee},date,transferred_id) VALUES ('t3','a1',NULL,-30000,'p3',20260116,'t4')");
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,{payee},date,transferred_id) VALUES ('t4','a2',NULL,30000,'p4',20260116,'t3')");
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,{payee},date,tombstone) VALUES ('t5','a1','c1',-999,'p1',20260117,1)");
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,{payee},date,{isParent}) VALUES ('t6','a1',NULL,-5000,'p7',20260120,1)");
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,date,{isChild},parent_id) VALUES ('t6a','a1','c1',-2000,20260120,1,'t6')");
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,date,{isChild},parent_id) VALUES ('t6b','a1','c3',-3000,20260120,1,'t6')");
        Exec(db, $"INSERT INTO transactions (id,acct,{cat},amount,{payee},date) VALUES ('t7','a5','c1',-100,'p1',20260121)");

        // Envelope budgets: a YYYYMM month and a YYYYMMDD month, to cover both spellings.
        Exec(db, "INSERT INTO zero_budgets (id,month,category,amount) VALUES ('b1',202512,'c1',35000),('b2',202601,'c1',40000),('b3',20260101,'c3',10000),('b4',202601,'c2',0)");
      }

      using var zipStream = new MemoryStream();
      using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
      {
        var dbEntry = zip.CreateEntry("db.sqlite");
        using (var s = dbEntry.Open()) s.Write(File.ReadAllBytes(dbPath));
        var meta = zip.CreateEntry("metadata.json");
        using (var w = new StreamWriter(meta.Open())) w.Write("{\"id\":\"My-Finances-abc123\",\"budgetName\":\"My Finances\"}");
      }
      return zipStream.ToArray();
    }
    finally
    {
      SqliteConnection.ClearAllPools();
      try { File.Delete(dbPath); } catch (IOException) { }
    }
  }

  private static void Exec(SqliteConnection db, string sql)
  {
    using var cmd = db.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
  }
}
