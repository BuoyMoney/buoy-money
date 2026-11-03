using System.Linq;
using System.Text;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Infrastructure.Services.Import;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services.Import;

public class OfxImportParserTests
{
  // OFX 1.x SGML: header block, leaf tags with no closers.
  private const string Sgml = @"OFXHEADER:100
DATA:OFXSGML
VERSION:102
SECURITY:NONE
ENCODING:USASCII
CHARSET:1252
COMPRESSION:NONE
OLDFILEUID:NONE
NEWFILEUID:NONE

<OFX>
<SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20260105120000<LANGUAGE>ENG</SONRS></SIGNONMSGSRSV1>
<BANKMSGSRSV1><STMTTRNRS><TRNUID>1<STATUS><CODE>0<SEVERITY>INFO</STATUS>
<STMTRS><CURDEF>USD
<BANKACCTFROM><BANKID>123456789<ACCTID>00112233<ACCTTYPE>CHECKING</BANKACCTFROM>
<BANKTRANLIST><DTSTART>20260101<DTEND>20260131
<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260102120000[0:GMT]<TRNAMT>-45.20<FITID>2026010201<NAME>GROCERY MART<MEMO>POS 1234</STMTTRN>
<STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260103<TRNAMT>1500.00<FITID>2026010301<NAME>ACME PAYROLL</STMTTRN>
<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260104<TRNAMT>-4.50<FITID>2026010401<NAME>COFFEE &amp; CO</STMTTRN>
</BANKTRANLIST>
<LEDGERBAL><BALAMT>1000<DTASOF>20260131</LEDGERBAL>
</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

  // OFX 2.x XML: every tag closed, one per line.
  private const string Xml = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""no""?>
<?OFX OFXHEADER=""200"" VERSION=""211"" SECURITY=""NONE"" OLDFILEUID=""NONE"" NEWFILEUID=""NONE""?>
<OFX>
  <CREDITCARDMSGSRSV1>
    <CCSTMTTRNRS>
      <CCSTMTRS>
        <CURDEF>USD</CURDEF>
        <CCACCTFROM>
          <ACCTID>4111111111111111</ACCTID>
        </CCACCTFROM>
        <BANKTRANLIST>
          <STMTTRN>
            <TRNTYPE>DEBIT</TRNTYPE>
            <DTPOSTED>20260110</DTPOSTED>
            <TRNAMT>-19.99</TRNAMT>
            <FITID>A1</FITID>
            <NAME>STREAMING</NAME>
          </STMTTRN>
        </BANKTRANLIST>
      </CCSTMTRS>
    </CCSTMTTRNRS>
  </CREDITCARDMSGSRSV1>
</OFX>";

  [Fact]
  public void Sgml_ReadsAccountAndTransactions()
  {
    var parsed = OfxImportParser.Parse(Encoding.ASCII.GetBytes(Sgml), new ImportMapping());

    parsed.Accounts.Should().ContainSingle();
    parsed.Accounts[0].Key.Should().Be("00112233");
    parsed.Accounts[0].Type.Should().Be("Checking");
    parsed.Rows.Should().HaveCount(3);

    var grocery = parsed.Rows[0];
    grocery.Date.Should().Be(new System.DateOnly(2026, 1, 2)); // time and zone are ignored
    grocery.Amount.Should().Be(-45.20m);
    grocery.SourceId.Should().Be("2026010201");
    grocery.Payee.Should().Be("GROCERY MART");
    grocery.Memo.Should().Be("POS 1234");
    grocery.AccountKey.Should().Be("00112233");

    parsed.Rows[1].Amount.Should().Be(1500m);
    parsed.Rows[2].Payee.Should().Be("COFFEE & CO"); // entity decoded
  }

  [Fact]
  public void Xml_ReadsCreditCardStatement()
  {
    var parsed = OfxImportParser.Parse(Encoding.UTF8.GetBytes(Xml), new ImportMapping());

    parsed.Accounts.Should().ContainSingle();
    parsed.Accounts[0].Type.Should().Be("CreditCard");
    parsed.Rows.Should().ContainSingle();
    parsed.Rows[0].Amount.Should().Be(-19.99m);
    parsed.Rows[0].SourceId.Should().Be("A1");
  }

  [Fact]
  public void Qfx_WithIntuBid_ReadsLikeOfx()
  {
    var qfx = Sgml.Replace("<LANGUAGE>ENG", "<LANGUAGE>ENG<FI><ORG>Some Bank<FID>1234</FI><INTU.BID>01234");
    var parsed = OfxImportParser.Parse(Encoding.ASCII.GetBytes(qfx), new ImportMapping());
    parsed.Rows.Should().HaveCount(3);
  }

  [Fact]
  public void Windows1252Header_DecodesAccents()
  {
    var text = Sgml.Replace("GROCERY MART", "CAFÉ DU MARCHÉ");
    var bytes = Encoding.GetEncoding(1252).GetBytes(text);
    var parsed = OfxImportParser.Parse(bytes, new ImportMapping());
    parsed.Rows[0].Payee.Should().Be("CAFÉ DU MARCHÉ");
  }

  [Fact]
  public void SgmlWithoutAnyClosers_StillSplitsTransactions()
  {
    const string bare = @"OFXHEADER:100
DATA:OFXSGML
VERSION:102

<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS>
<BANKACCTFROM><BANKID>1<ACCTID>99<ACCTTYPE>SAVINGS
<BANKTRANLIST>
<STMTTRN><DTPOSTED>20260102<TRNAMT>-1.00<FITID>1<NAME>One
<STMTTRN><DTPOSTED>20260103<TRNAMT>-2.00<FITID>2<NAME>Two
<LEDGERBAL><BALAMT>5";
    var parsed = OfxImportParser.Parse(Encoding.ASCII.GetBytes(bare), new ImportMapping());
    parsed.Rows.Select(r => r.Payee).Should().Equal("One", "Two");
    parsed.Accounts[0].Type.Should().Be("Savings");
  }

  [Fact]
  public void IdenticalRowsGetDistinctOccurrences()
  {
    var twoCoffees = Sgml.Replace("<FITID>2026010401", "<FITID>").Replace("<NAME>COFFEE &amp; CO</STMTTRN>",
      "<NAME>COFFEE</STMTTRN>\n<STMTTRN><DTPOSTED>20260104<TRNAMT>-4.50<NAME>COFFEE</STMTTRN>");
    var parsed = OfxImportParser.Parse(Encoding.ASCII.GetBytes(twoCoffees), new ImportMapping());
    var coffees = parsed.Rows.Where(r => r.Payee == "COFFEE").ToList();
    coffees.Should().HaveCount(2);
    coffees.Select(c => c.Occurrence).Should().BeEquivalentTo(new[] { 0, 1 });
  }

  [Fact]
  public void BadAmountOrDate_MarksRowInvalidWithoutFailingTheFile()
  {
    var broken = Sgml.Replace("<TRNAMT>1500.00", "<TRNAMT>lots").Replace("<DTPOSTED>20260104", "<DTPOSTED>soon");
    var parsed = OfxImportParser.Parse(Encoding.ASCII.GetBytes(broken), new ImportMapping());
    parsed.Rows.Should().HaveCount(3);
    parsed.Rows.Count(r => r.Error != null).Should().Be(2);
    parsed.Rows[0].Error.Should().BeNull();
  }

  [Fact]
  public void EmptyFile_WarnsInsteadOfThrowing()
  {
    var parsed = OfxImportParser.Parse(Encoding.ASCII.GetBytes("<OFX></OFX>"), new ImportMapping());
    parsed.Rows.Should().BeEmpty();
    parsed.Warnings.Should().NotBeEmpty();
  }

  [Fact]
  public void LooksLikeOfx_DetectsHeaderOrRootTag()
  {
    OfxImportParser.LooksLikeOfx(Encoding.ASCII.GetBytes(Sgml)).Should().BeTrue();
    OfxImportParser.LooksLikeOfx(Encoding.ASCII.GetBytes("Date,Amount\n1,2")).Should().BeFalse();
  }
}
