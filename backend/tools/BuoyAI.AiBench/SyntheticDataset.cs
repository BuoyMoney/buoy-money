using System.Globalization;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.AiBench;

/// <summary>One labelled benchmark row plus the history the real prompt would carry for it.</summary>
/// <param name="Expected">"21" for a system category, "C1001" for a custom one.</param>
/// <param name="Bucket">plain, income, split (needs the user-history block) or custom (the C-id form).</param>
internal sealed record BenchRow(
    int Id,
    Transaction Transaction,
    string Expected,
    string Bucket,
    Dictionary<string, CategoryPattern> UserPatterns);

/// <summary>
/// A made-up labelled set, generated from a fixed seed so every run scores the same rows. No real
/// user data: merchants are public brands with the messy bank strings real feeds produce, amounts
/// and dates are random. Transfers and card payments are left out because the real pipeline
/// filters them before any model sees them.
///
/// Four buckets, matching how the real prompt is used:
/// plain merchants, income, split merchants (Walmart as groceries AND shopping, told apart only by
/// amount and weekday, which the prompt's history block describes), and custom categories
/// (the C-id form).
/// </summary>
internal static class SyntheticDataset
{
    public const string Plain = "plain";
    public const string Income = "income";
    public const string Split = "split";
    public const string Custom = "custom";

    /// <summary>The user's custom categories, offered on every expense prompt as in the real app.</summary>
    public static List<Category> CustomCategories() =>
    [
        new Category { Id = 1001, Name = "Pet Care", Description = "Food, vet visits and grooming for the dog and cat" },
        new Category { Id = 1002, Name = "Kids Activities", Description = "Sports, lessons and school fees for the kids" },
        new Category { Id = 1003, Name = "Home Projects", Description = "Hardware, paint, lumber and repairs around the house" },
    ];

    private sealed record Merchant(string Name, string Bank, decimal Min, decimal Max);

    private static readonly Dictionary<TransactionCategory, Merchant[]> PlainMerchants = new()
    {
        [TransactionCategory.Groceries] =
        [
            new("Dillons", "DILLONS #34 TOPEKA KS", 18, 160), new("Aldi", "ALDI 72014", 20, 110),
            new("Trader Joe's", "TRADER JOE S #112", 15, 90), new("Whole Foods", "WHOLEFDS TPA 10234", 25, 180),
            new("Kroger", "KROGER #0412 FUEL PTS", 20, 150), new("Hy-Vee", "HY-VEE 1234 LAWRENCE KS", 12, 140),
        ],
        [TransactionCategory.DiningOut] =
        [
            new("Blue Bottle Coffee", "SQ *BLUE BOTTLE COFFEE 0412", 4, 14), new("Chipotle", "CHIPOTLE 2847", 9, 32),
            new("McDonald's", "MCDONALD'S F8841", 5, 22), new("Starbucks", "STARBUCKS STORE 12043", 4, 16),
            new("DoorDash", "DOORDASH*PIZZA PLANET", 15, 60), new("Smokehouse", "TST* THE SMOKEHOUSE", 18, 75),
        ],
        [TransactionCategory.Transportation] =
        [
            new("Shell", "SHELL OIL 57444123", 22, 85), new("Casey's", "CASEYS GENERAL STORE 2231", 18, 70),
            new("Uber", "UBER *TRIP HELP.UBER.COM", 7, 48), new("Jiffy Lube", "JIFFY LUBE #1182", 45, 140),
            new("Kwik Trip", "KWIK TRIP 441", 20, 75), new("Turnpike", "KTA TURNPIKE TOLL", 2, 18),
        ],
        [TransactionCategory.Entertainment] =
        [
            new("AMC Theatres", "AMC 7054 ONLINE", 10, 60), new("Steam", "STEAMGAMES.COM 4259522", 8, 70),
            new("Topeka Zoo", "TOPEKA ZOO TICKETS", 12, 55), new("Bowlero", "BOWLERO LAWRENCE", 20, 90),
            new("Ticketmaster", "TICKETMASTER *ORDER", 40, 220), new("PlayStation", "PLAYSTATION NETWORK", 10, 70),
        ],
        [TransactionCategory.Shopping] =
        [
            new("Target", "TARGET 00012345", 12, 180), new("Best Buy", "BEST BUY 00001234", 25, 600),
            new("Old Navy", "OLD NAVY US 7234", 20, 140), new("Etsy", "ETSY.COM - SELLERNAME", 10, 90),
            new("IKEA", "IKEA KANSAS CITY", 30, 400), new("Nike", "NIKE.COM", 40, 180),
        ],
        [TransactionCategory.Healthcare] =
        [
            new("CVS Pharmacy", "CVS/PHARMACY #08421", 6, 120), new("Walgreens", "WALGREENS #5531", 5, 90),
            new("Stormont Vail", "STORMONT VAIL HEALTH PMT", 40, 400), new("Smile Dental", "SMILE DENTAL ASSOC", 60, 350),
            new("LabCorp", "LABCORP 0192", 30, 250), new("Quest", "QUEST DIAGNOSTICS", 30, 250),
        ],
        [TransactionCategory.Utilities] =
        [
            new("Evergy", "EVERGY KANSAS CENTRAL", 60, 220), new("Verizon", "VERIZON WIRELESS PAYMENT", 45, 190),
            new("Xfinity", "COMCAST XFINITY 8888", 55, 130), new("AT&T", "ATT*BILL PAYMENT", 45, 160),
            new("Kansas Gas Service", "KANSAS GAS SERVICE", 30, 190), new("City Water", "CITY OF TOPEKA UTILITIES", 35, 110),
        ],
        [TransactionCategory.Rent] =
        [
            new("Maple Apartments", "RENT PAYMENT - MAPLE APTS", 900, 1900), new("Rocket Mortgage", "ROCKET MORTGAGE PMT", 1100, 2300),
            new("Wells Fargo Home", "WELLS FARGO HOME MTG", 1000, 2200), new("Lakeview Property", "LAKEVIEW PROPERTY MGMT", 850, 1800),
            new("Oak Ridge Rentals", "OAK RIDGE RENTALS ACH", 800, 1700), new("Mr Cooper", "MR COOPER MORTGAGE", 1000, 2100),
        ],
        [TransactionCategory.Insurance] =
        [
            new("State Farm", "STATE FARM INSURANCE", 60, 320), new("Geico", "GEICO *AUTO", 70, 260),
            new("Progressive", "PROGRESSIVE INS PREM", 60, 280), new("Allstate", "ALLSTATE INS CO", 60, 300),
            new("Blue Cross", "BLUE CROSS BLUE SHIELD", 150, 600), new("Farmers", "FARMERS INS EXCHANGE", 70, 320),
        ],
        [TransactionCategory.Subscriptions] =
        [
            new("Netflix", "NETFLIX.COM", 7, 23), new("Spotify", "SPOTIFY USA", 10, 17),
            new("Hulu", "HULU 877-8244858", 8, 20), new("Apple", "APPLE.COM/BILL", 1, 15),
            new("Adobe", "ADOBE *CREATIVE CLOUD", 10, 60), new("NYT", "NYTIMES*DIGITAL", 5, 25),
        ],
        [TransactionCategory.LoanPayments] =
        [
            new("Nelnet", "NELNET STUDENT LOAN", 120, 600), new("Navient", "NAVIENT LOAN PMT", 120, 500),
            new("Toyota Financial", "TOYOTA FINANCIAL SERVICES", 280, 650), new("SoFi", "SOFI LOAN PMT", 150, 700),
            new("Ally Auto", "ALLY AUTO PAYMENT", 250, 700), new("Great Lakes", "GREAT LAKES EDUCATIONAL", 100, 450),
        ],
        [TransactionCategory.Travel] =
        [
            new("Delta", "DELTA AIR 0062391", 120, 900), new("Marriott", "MARRIOTT KC AIRPORT", 90, 500),
            new("Airbnb", "AIRBNB * HM4K2", 100, 800), new("Southwest", "SOUTHWEST AIRLINES", 100, 700),
            new("Hertz", "HERTZ RENT-A-CAR", 60, 400), new("Expedia", "EXPEDIA 7283", 150, 1200),
        ],
        [TransactionCategory.PersonalCare] =
        [
            new("Great Clips", "GREAT CLIPS #1923", 15, 40), new("Sally Beauty", "SALLY BEAUTY 3392", 8, 60),
            new("Planet Fitness", "PLANET FITNESS CLUB FEES", 10, 30), new("Supercuts", "SUPERCUTS 4410", 18, 45),
            new("Ulta", "ULTA #0123", 12, 110), new("Spa 360", "SPA 360 TOPEKA", 45, 160),
        ],
        [TransactionCategory.Education] =
        [
            new("Washburn", "WASHBURN UNIVERSITY TUITION", 400, 3000), new("Udemy", "UDEMY ONLINE COURSE", 10, 90),
            new("Pearson", "PEARSON EDUCATION", 40, 250), new("Coursera", "COURSERA.ORG", 30, 80),
            new("Campus Bookstore", "CAMPUS BOOKSTORE", 20, 300), new("Khan Academy", "KHAN ACADEMY DONATION EDU", 10, 40),
        ],
        [TransactionCategory.Gifts] =
        [
            new("Red Cross", "AMERICAN RED CROSS DONATION", 10, 200), new("1-800-Flowers", "1-800-FLOWERS.COM", 30, 120),
            new("GoFundMe", "GOFUNDME *DONATION", 10, 150), new("Hallmark", "HALLMARK CARDS 221", 4, 40),
            new("Salvation Army", "SALVATION ARMY DONATION", 10, 100), new("Toys for Tots", "TOYS FOR TOTS FOUNDATION", 10, 80),
        ],
    };

    private static readonly Dictionary<TransactionCategory, Merchant[]> IncomeMerchants = new()
    {
        [TransactionCategory.Salary] =
        [
            new("Acme Corp", "ACME CORP PAYROLL DIRECT DEP", 1800, 4200), new("Gusto", "GUSTO PAY 4412", 1500, 3800),
            new("ADP", "ADP PAYROLL 8821", 1600, 4000), new("State of Kansas", "STATE OF KS PAYROLL", 1700, 3500),
        ],
        [TransactionCategory.Bonus] =
        [
            new("Acme Corp", "ACME CORP BONUS PAYMENT", 500, 5000), new("Gusto", "GUSTO ANNUAL INCENTIVE", 400, 4000),
            new("ADP", "ADP COMMISSION PAYOUT", 300, 3000),
        ],
        [TransactionCategory.Investment] =
        [
            new("Vanguard", "VANGUARD DIVIDEND", 12, 400), new("Schwab", "SCHWAB INTEREST", 5, 250),
            new("Marcus", "MARCUS SAVINGS INTEREST PAID", 3, 120),
        ],
        [TransactionCategory.OtherIncome] =
        [
            new("IRS", "IRS TREAS 310 TAX REF", 120, 2400), new("Venmo", "VENMO CASHOUT", 10, 300),
            new("Geico", "GEICO CLAIM REFUND", 40, 900),
        ],
    };

    /// <summary>A merchant the user files under two categories, told apart by amount and weekday.</summary>
    private sealed record SplitCluster(TransactionCategory Category, decimal Average, double WeekendShare);

    private static readonly (string Name, string Bank, SplitCluster A, SplitCluster B)[] SplitMerchants =
    [
        ("Walmart", "WAL-MART #1234",
            new(TransactionCategory.Groceries, 96m, 0.8), new(TransactionCategory.Shopping, 31m, 0.1)),
        ("Target", "TARGET 00023456",
            new(TransactionCategory.Groceries, 45m, 0.1), new(TransactionCategory.Shopping, 130m, 0.8)),
        ("Amazon", "AMZN Mktp US*2K4",
            new(TransactionCategory.Shopping, 62m, 0.5), new(TransactionCategory.Entertainment, 9m, 0.5)),
        ("Dillons", "DILLONS #52",
            new(TransactionCategory.Groceries, 88m, 0.85), new(TransactionCategory.Transportation, 41m, 0.1)),
    ];

    private static readonly (int Id, string[] Banks, decimal Min, decimal Max)[] CustomMerchants =
    [
        (1001, ["PETCO 1234", "CHEWY.COM", "BANFIELD PET HOSPITAL", "PETSMART #0412", "WAG VET CLINIC"], 12, 220),
        (1002, ["YMCA YOUTH SPORTS", "TOPEKA DANCE ACADEMY", "LITTLE LEAGUE REG FEE", "KUMON LEARNING CTR", "SCHOOL FEES PTO"], 20, 300),
        (1003, ["HOME DEPOT 1234", "LOWES #0432", "ACE HARDWARE 8821", "SHERWIN-WILLIAMS 7123", "MENARDS 3341"], 12, 450),
    ];

    private static readonly Dictionary<TransactionCategory, (string Primary, string Detailed)> PlaidGuesses = new()
    {
        [TransactionCategory.Groceries] = ("FOOD_AND_DRINK", "FOOD_AND_DRINK_GROCERIES"),
        [TransactionCategory.DiningOut] = ("FOOD_AND_DRINK", "FOOD_AND_DRINK_RESTAURANT"),
        [TransactionCategory.Transportation] = ("TRANSPORTATION", "TRANSPORTATION_GAS"),
        [TransactionCategory.Entertainment] = ("ENTERTAINMENT", "ENTERTAINMENT_TV_AND_MOVIES"),
        [TransactionCategory.Shopping] = ("GENERAL_MERCHANDISE", "GENERAL_MERCHANDISE_OTHER_GENERAL_MERCHANDISE"),
        [TransactionCategory.Healthcare] = ("MEDICAL", "MEDICAL_PHARMACIES_AND_SUPPLEMENTS"),
        [TransactionCategory.Utilities] = ("RENT_AND_UTILITIES", "RENT_AND_UTILITIES_GAS_AND_ELECTRICITY"),
        [TransactionCategory.Rent] = ("RENT_AND_UTILITIES", "RENT_AND_UTILITIES_RENT"),
        [TransactionCategory.Insurance] = ("GENERAL_SERVICES", "GENERAL_SERVICES_INSURANCE"),
        [TransactionCategory.Subscriptions] = ("ENTERTAINMENT", "ENTERTAINMENT_MUSIC_AND_AUDIO"),
        [TransactionCategory.LoanPayments] = ("LOAN_PAYMENTS", "LOAN_PAYMENTS_CAR_PAYMENT"),
        [TransactionCategory.Travel] = ("TRAVEL", "TRAVEL_FLIGHTS"),
        [TransactionCategory.PersonalCare] = ("PERSONAL_CARE", "PERSONAL_CARE_HAIR_AND_BEAUTY"),
        [TransactionCategory.Education] = ("GENERAL_SERVICES", "GENERAL_SERVICES_EDUCATION"),
        [TransactionCategory.Gifts] = ("GENERAL_SERVICES", "GENERAL_SERVICES_OTHER_GENERAL_SERVICES"),
    };

    private static readonly string[] BankPrefixes = ["", "", "", "POS DEBIT ", "PURCHASE AUTHORIZED ON 10/03 ", "CHECKCARD "];

    /// <summary>
    /// Builds the full set. <paramref name="seed"/> fixes every random choice. Rows come back
    /// shuffled (stably), so taking the first N gives a mix of all four buckets.
    /// </summary>
    public static List<BenchRow> Build(int seed = 42)
    {
        var rng = new Random(seed);
        var rows = new List<BenchRow>();
        int id = 1;
        var plaidPool = PlaidGuesses.Keys.ToArray();

        foreach (var (category, merchants) in PlainMerchants)
        {
            for (int i = 0; i < 15; i++)
            {
                var m = merchants[i % merchants.Length];
                rows.Add(new BenchRow(id, MakeTransaction(id++, m, category, isIncome: false, rng, plaidPool),
                    ((int)category).ToString(CultureInfo.InvariantCulture), Plain, []));
            }
        }

        foreach (var (category, merchants) in IncomeMerchants)
        {
            for (int i = 0; i < 10; i++)
            {
                var m = merchants[i % merchants.Length];
                rows.Add(new BenchRow(id, MakeTransaction(id++, m, category, isIncome: true, rng, plaidPool),
                    ((int)category).ToString(CultureInfo.InvariantCulture), Income, []));
            }
        }

        foreach (var (name, bank, a, b) in SplitMerchants)
        {
            var patterns = new Dictionary<string, CategoryPattern>(StringComparer.OrdinalIgnoreCase)
            {
                [name.ToLowerInvariant()] = new CategoryPattern(a.Category, null, a.Category.ToString(), 2,
                    [
                        new MerchantSplit(a.Category.ToString(), 24, a.Average, a.WeekendShare),
                        new MerchantSplit(b.Category.ToString(), 18, b.Average, b.WeekendShare),
                    ]),
            };
            foreach (var cluster in new[] { a, b })
            {
                for (int i = 0; i < 10; i++)
                {
                    var amount = Math.Round(cluster.Average * (decimal)(0.8 + rng.NextDouble() * 0.4), 2);
                    var weekend = rng.NextDouble() < cluster.WeekendShare;
                    var tx = Base(id, name, bank, amount, DayOfSeptember(rng, weekend), TransactionType.Debit);
                    // Plaid sees only the merchant, so its guess is the merchant's majority category.
                    AddPlaid(tx, a.Category, wrong: false, rng, plaidPool);
                    rows.Add(new BenchRow(id++, tx, ((int)cluster.Category).ToString(CultureInfo.InvariantCulture), Split, patterns));
                }
            }
        }

        foreach (var (customId, banks, min, max) in CustomMerchants)
        {
            for (int i = 0; i < 13; i++)
            {
                var bank = banks[i % banks.Length];
                var amount = Math.Round(min + (decimal)rng.NextDouble() * (max - min), 2);
                var tx = Base(id, bank.Split(' ')[0], bank, amount, DayOfSeptember(rng, rng.NextDouble() < 0.4), TransactionType.Debit);
                if (rng.NextDouble() < 0.5)
                    AddPlaid(tx, TransactionCategory.Shopping, wrong: false, rng, plaidPool);
                rows.Add(new BenchRow(id++, tx, "C" + customId.ToString(CultureInfo.InvariantCulture), Custom, []));
            }
        }

        // Stable shuffle: order by a seeded key, ids unchanged so results can be traced back.
        return rows.OrderBy(_ => rng.Next()).ToList();
    }

    /// <summary>A deterministic subset of <paramref name="count"/> rows that keeps the bucket mix.</summary>
    public static List<BenchRow> Take(List<BenchRow> all, int count)
    {
        if (count >= all.Count) return all;
        // Floors first, then the leftover rows go to the largest fractional remainders, so the
        // total is exactly `count` (paid providers bill per call).
        var groups = all.GroupBy(r => r.Bucket).ToList();
        var exact = groups.Select(g => (double)count * g.Count() / all.Count).ToList();
        var alloc = exact.Select(e => (int)Math.Floor(e)).ToList();
        var left = count - alloc.Sum();
        foreach (var i in Enumerable.Range(0, groups.Count).OrderByDescending(i => exact[i] - alloc[i]).Take(Math.Max(0, left)))
            alloc[i]++;
        var result = new List<BenchRow>();
        for (var i = 0; i < groups.Count; i++)
            result.AddRange(groups[i].Take(alloc[i]));
        return result.OrderBy(r => r.Id).ToList();
    }

    private static Transaction MakeTransaction(int id, Merchant m, TransactionCategory category, bool isIncome, Random rng, TransactionCategory[] plaidPool)
    {
        var amount = Math.Round(m.Min + (decimal)rng.NextDouble() * (m.Max - m.Min), 2);
        var bank = BankPrefixes[rng.Next(BankPrefixes.Length)] + m.Bank;
        var tx = Base(id, rng.NextDouble() < 0.25 ? "" : m.Name, bank, amount, DayOfSeptember(rng, rng.NextDouble() < 0.3),
            isIncome ? TransactionType.Credit : TransactionType.Debit);
        if (!isIncome && rng.NextDouble() < 0.6)
            AddPlaid(tx, category, wrong: rng.NextDouble() < 0.2, rng, plaidPool);
        return tx;
    }

    private static Transaction Base(int id, string merchant, string bank, decimal amount, DateTime date, TransactionType type) => new()
    {
        Id = id,
        MerchantName = merchant,
        Description = bank,
        OriginalDescription = bank,
        Amount = amount,
        Type = type,
        Date = date,
    };

    private static void AddPlaid(Transaction tx, TransactionCategory category, bool wrong, Random rng, TransactionCategory[] pool)
    {
        if (!PlaidGuesses.ContainsKey(category)) return;
        var shown = wrong ? pool[rng.Next(pool.Length)] : category;
        var (primary, detailed) = PlaidGuesses[shown];
        tx.PlaidCategoryPrimary = primary;
        tx.PlaidCategoryDetailed = detailed;
        tx.PlaidCategoryConfidence = wrong ? "LOW" : rng.NextDouble() < 0.5 ? "HIGH" : "VERY_HIGH";
    }

    private static DateTime DayOfSeptember(Random rng, bool weekend)
    {
        while (true)
        {
            var date = new DateTime(2026, 9, 1).AddDays(rng.Next(30));
            var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (isWeekend == weekend) return date;
        }
    }
}
