using System.Text.RegularExpressions;
using BuoyAI.Core.DTOs.Import;

namespace BuoyAI.Infrastructure.Services.Import;

/// <summary>
/// Reads OFX (1.x SGML and 2.x XML) and QFX. A tolerant, line-oriented tag reader: leaf tags may
/// or may not have closers, aggregates may or may not be closed, and the header block can declare
/// a character set. Dates are taken as written (no time zone shift).
/// </summary>
public static class OfxImportParser
{
  private static readonly Regex TagPattern = new(@"<(/?)([A-Za-z0-9_.]+)>([^<\r\n]*)", RegexOptions.Compiled);
  private static readonly Regex CharsetPattern = new(@"CHARSET\s*[:=]\s*""?([A-Za-z0-9\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

  public static bool LooksLikeOfx(byte[] bytes)
  {
    var head = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
    return head.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase)
           || head.Contains("<OFX", StringComparison.OrdinalIgnoreCase);
  }

  public static ParsedImport Parse(byte[] bytes, ImportMapping mapping)
  {
    var parsed = new ParsedImport { Source = ImportSource.Ofx };

    var asciiHead = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
    var charset = CharsetPattern.Match(asciiHead);
    var (text, encodingName) = ImportText.Decode(bytes, charset.Success ? charset.Groups[1].Value.ToUpperInvariant() : null);
    parsed.Detected.Source = "ofx";
    parsed.Detected.Encoding = encodingName;

    var accountKey = string.Empty;
    var accountIsCard = false;

    Dictionary<string, string>? current = null;
    var rawRows = new List<(string Key, Dictionary<string, string> Fields)>();
    var skippedInvestment = false;

    string? pendingAcctId = null;
    string? pendingAcctType = null;

    void CloseAccountBlock()
    {
      if (pendingAcctId == null) return;
      accountKey = pendingAcctId;
      if (!parsed.Accounts.Any(a => a.Key == accountKey))
      {
        var type = accountIsCard ? "CreditCard"
          : pendingAcctType?.ToUpperInvariant() switch
          {
            "SAVINGS" or "MONEYMRKT" => "Savings",
            "CREDITLINE" => "CreditCard",
            _ => "Checking"
          };
        var label = accountIsCard ? "Credit card" : pendingAcctType != null ? ToTitle(pendingAcctType) : "Account";
        var tail = pendingAcctId.Length > 4 ? pendingAcctId[^4..] : pendingAcctId;
        parsed.Accounts.Add(new ParsedImportAccount { Key = accountKey, Name = $"{label} {tail}", Type = type });
      }
      pendingAcctId = null;
      pendingAcctType = null;
    }

    foreach (Match m in TagPattern.Matches(text))
    {
      var closing = m.Groups[1].Value == "/";
      var tag = m.Groups[2].Value.ToUpperInvariant();
      var value = DecodeEntities(m.Groups[3].Value.Trim());

      if (!closing && (tag == "CCSTMTRS" || tag == "CCSTMTTRNRS")) { accountIsCard = true; }
      else if (!closing && (tag == "STMTRS" || tag == "STMTTRNRS")) { accountIsCard = false; }
      else if (!closing && tag == "INVSTMTRS") { skippedInvestment = true; }

      if (closing)
      {
        if (tag == "BANKACCTFROM" || tag == "CCACCTFROM")
        {
          if (tag == "CCACCTFROM") accountIsCard = true;
          CloseAccountBlock();
        }
        else if (tag == "STMTTRN") Finish();
        else if (tag == "BANKTRANLIST") Finish();
        continue;
      }

      switch (tag)
      {
        case "ACCTID": pendingAcctId = value; break;
        case "ACCTTYPE": pendingAcctType = value; break;
        case "STMTTRN":
          CloseAccountBlock();
          Finish();
          current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
          break;
        default:
          // SGML without closers: the account block ends where its leaf tags stop.
          if (pendingAcctId != null && (tag == "BANKTRANLIST" || tag == "LEDGERBAL" || tag == "DTSTART"))
            CloseAccountBlock();
          // A balance block means the transaction list is over, even when no closer says so.
          if (tag == "LEDGERBAL" || tag == "AVAILBAL") Finish();
          if (current != null && value.Length > 0 && !current.ContainsKey(tag))
            current[tag] = value;
          break;
      }
    }
    Finish();

    void Finish()
    {
      if (current == null) return;
      rawRows.Add((accountKey, current));
      current = null;
    }

    if (rawRows.Count == 0)
    {
      parsed.Warnings.Add(skippedInvestment
        ? "This looks like an investment statement. Buy and sell activity is not imported; only bank-style transactions are."
        : "No transactions were found in this file. It may be empty or in a format this importer does not read.");
      return parsed;
    }
    if (skippedInvestment)
      parsed.Warnings.Add("Investment activity in this file was skipped. Only bank-style transactions are imported.");

    if (parsed.Accounts.Count == 0)
    {
      parsed.Accounts.Add(new ParsedImportAccount { Key = string.Empty, Name = "Imported account" });
    }

    // OFX amounts use a decimal point; some European banks use a comma. Detect once for the file.
    var numberFormat = mapping.NumberFormat != ImportNumberFormat.Auto
      ? mapping.NumberFormat
      : ImportNumberParser.Detect(rawRows.Select(r => r.Fields.GetValueOrDefault("TRNAMT")));
    parsed.Detected.NumberFormat = numberFormat.ToString();

    var occurrences = new Dictionary<string, int>();
    var rowId = 0;
    foreach (var (key, fields) in rawRows)
    {
      var row = new ParsedImportRow
      {
        RowId = rowId++,
        AccountKey = parsed.Accounts.Any(a => a.Key == key) ? key : parsed.Accounts[0].Key,
        SourceId = fields.GetValueOrDefault("FITID"),
        CheckNumber = fields.GetValueOrDefault("CHECKNUM")
      };

      var name = fields.GetValueOrDefault("NAME");
      var memo = fields.GetValueOrDefault("MEMO");
      row.Payee = !string.IsNullOrWhiteSpace(name) ? name : memo ?? "Unknown Transaction";
      row.OriginalDescription = row.Payee;
      row.Memo = !string.IsNullOrWhiteSpace(name) ? memo : null;

      if (!TryParseOfxDate(fields.GetValueOrDefault("DTPOSTED"), out var date))
        row.Error = "The posted date could not be read.";
      else
        row.Date = date;

      if (!ImportNumberParser.TryParse(fields.GetValueOrDefault("TRNAMT"), numberFormat, out var amount))
        row.Error ??= "The amount could not be read.";
      else
        row.Amount = mapping.FlipSign ? -amount : amount;

      if (row.Error == null)
      {
        var occKey = $"{row.AccountKey}|{row.Date:yyyyMMdd}|{row.Amount}|{row.Payee.ToLowerInvariant()}";
        row.Occurrence = occurrences.GetValueOrDefault(occKey);
        occurrences[occKey] = row.Occurrence + 1;
      }
      parsed.Rows.Add(row);
    }

    return parsed;
  }

  private static string ToTitle(string s) =>
    s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

  private static string DecodeEntities(string s) =>
    s.Contains('&')
      ? s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'")
      : s;

  /// <summary>YYYYMMDD[HHMMSS[.XXX]][[offset:TZ]]. The date part is used as written.</summary>
  private static bool TryParseOfxDate(string? value, out DateOnly date)
  {
    date = default;
    if (string.IsNullOrWhiteSpace(value) || value.Length < 8) return false;
    var digits = value.Trim()[..8];
    return ImportDateParser.TryParse(digits, ImportDateOrder.YearFirst, out date);
  }
}
