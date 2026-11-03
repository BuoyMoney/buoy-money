namespace BuoyAI.Core.Utilities;

public static class SemanticTagVocabulary
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // Food & Drink
        "food", "drink", "coffee", "cafe", "restaurant", "fast_food", "takeout", "delivery",
        "pizza", "bakery", "bar", "brewery", "winery", "juice", "tea", "ice_cream", "dessert",
        "catering", "food_truck", "buffet", "deli", "brunch",

        // Grocery
        "grocery", "supermarket", "produce", "organic", "bulk", "wholesale", "convenience_store",
        "farmers_market", "butcher", "seafood", "specialty_food",

        // Transportation
        "gas", "fuel", "parking", "tolls", "rideshare", "taxi", "public_transit", "subway",
        "bus", "train", "car_rental", "car_wash", "auto_repair", "auto_parts", "tire",
        "ev_charging", "bicycle",

        // Shopping & Retail
        "retail", "clothing", "shoes", "accessories", "electronics", "furniture", "home_goods",
        "hardware", "appliances", "jewelry", "books", "toys", "sporting_goods", "pet_supplies",
        "office_supplies", "craft", "art", "music", "garden", "thrift", "discount",
        "department_store", "online_shopping", "marketplace",

        // Entertainment & Recreation
        "entertainment", "movies", "theater", "concert", "music_venue", "sports", "gym",
        "fitness", "yoga", "recreation", "gaming", "arcade", "amusement_park", "museum",
        "zoo", "aquarium", "bowling", "golf", "outdoors", "camping", "streaming",

        // Health & Medical
        "healthcare", "medical", "doctor", "dentist", "vision", "pharmacy", "prescription",
        "hospital", "clinic", "lab", "mental_health", "therapy", "chiropractic", "veterinary",
        "wellness", "supplement",

        // Personal Care & Beauty
        "personal_care", "haircut", "salon", "spa", "cosmetics", "skincare", "nail",
        "barber", "tattoo", "massage", "grooming",

        // Home & Utilities
        "utilities", "electric", "water", "gas_utility", "internet", "phone", "cable",
        "trash", "sewer", "home_improvement", "plumbing", "electrical", "hvac",
        "cleaning", "laundry", "dry_cleaning", "pest_control", "landscaping", "moving",
        "storage", "security",

        // Housing
        "rent", "mortgage", "property_tax", "hoa", "home_insurance",

        // Insurance
        "insurance", "health_insurance", "auto_insurance", "life_insurance", "renters_insurance",

        // Education
        "education", "tuition", "books_supplies", "school", "college", "university",
        "online_course", "tutoring", "childcare", "daycare",

        // Travel
        "travel", "hotel", "motel", "airbnb", "flight", "airline", "cruise",
        "vacation", "resort", "luggage", "travel_agency",

        // Financial
        "transfer", "atm", "bank_fee", "interest", "investment", "dividend", "credit_payment",
        "loan_payment", "wire_transfer", "currency_exchange",

        // Income
        "salary", "paycheck", "bonus", "commission", "freelance", "side_hustle",
        "refund", "reimbursement", "cashback", "rebate", "gift_received",

        // Subscriptions & Digital
        "subscription", "membership", "software", "cloud_service", "app", "digital",
        "saas", "news", "magazine",

        // Gifts & Donations
        "gift", "donation", "charity", "nonprofit", "religious", "tithe",

        // Government & Taxes
        "tax", "government", "license", "permit", "fine", "court",

        // Professional Services
        "legal", "accounting", "consulting", "professional_services",

        // Childcare & Family
        "childcare", "baby", "kids", "family", "school_supplies"
    };

    public static string? ValidateAndFilter(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return null;

        var validated = tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant().Trim())
            .Where(t => AllowedTags.Contains(t))
            .Distinct()
            .ToList();

        return validated.Count > 0 ? string.Join(", ", validated) : null;
    }

    public static bool IsValidTag(string tag)
    {
        return AllowedTags.Contains(tag.Trim());
    }

    public static string? ParsePlaidCategoryToTags(string? plaidCategoryDetailed)
    {
        if (string.IsNullOrWhiteSpace(plaidCategoryDetailed)) return null;

        // Plaid detailed categories follow patterns like:
        // "FOOD_AND_DRINK_COFFEE" → food, drink, coffee
        // "TRANSPORTATION_GAS" → transportation, gas
        // "GENERAL_MERCHANDISE_ELECTRONICS" → electronics
        // "RENT_AND_UTILITIES_ELECTRIC" → rent, utilities, electric
        var parts = plaidCategoryDetailed
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLowerInvariant())
            .Where(p => p != "and" && p != "other" && p != "general" && p.Length > 2)
            .ToList();

        // Map common Plaid terms to our vocabulary
        var mapped = parts.Select(MapPlaidTermToTag).Where(t => t != null).Cast<string>();

        return ValidateAndFilter(string.Join(", ", mapped));
    }

    private static string? MapPlaidTermToTag(string plaidTerm)
    {
        return plaidTerm switch
        {
            "food" => "food",
            "drink" => "drink",
            "coffee" => "coffee",
            "restaurants" => "restaurant",
            "fast" => "fast_food",
            "grocery" => "grocery",
            "supermarkets" => "grocery",
            "transportation" => null, // too generic, let sub-categories carry it
            "gas" => "gas",
            "parking" => "parking",
            "tolls" => "tolls",
            "public" => null,
            "transit" => "public_transit",
            "taxi" => "taxi",
            "rideshare" => "rideshare",
            "entertainment" => "entertainment",
            "recreation" => "recreation",
            "sports" => "sports",
            "music" => "music",
            "movies" => "movies",
            "gaming" => "gaming",
            "merchandise" => null,
            "clothing" => "clothing",
            "electronics" => "electronics",
            "furniture" => "furniture",
            "hardware" => "hardware",
            "books" => "books",
            "pets" => "pet_supplies",
            "sporting" => "sporting_goods",
            "discount" => "discount",
            "department" => "department_store",
            "stores" => null,
            "medical" => "medical",
            "healthcare" => "healthcare",
            "dental" => "dentist",
            "pharmacy" => "pharmacy",
            "vision" => "vision",
            "personal" => null,
            "care" => null,
            "haircut" => "haircut",
            "spa" => "spa",
            "gym" => "gym",
            "rent" => "rent",
            "utilities" => "utilities",
            "electric" => "electric",
            "water" => "water",
            "internet" => "internet",
            "phone" => "phone",
            "cable" => "cable",
            "education" => "education",
            "tuition" => "tuition",
            "school" => "school",
            "travel" => "travel",
            "hotels" => "hotel",
            "flights" => "flight",
            "airline" => "airline",
            "vacation" => "vacation",
            "subscription" => "subscription",
            "insurance" => "insurance",
            "loan" => "loan_payment",
            "payments" => null,
            "transfer" => "transfer",
            "atm" => "atm",
            "bank" => null,
            "fees" => "bank_fee",
            "interest" => "interest",
            "investment" => "investment",
            "income" => "salary",
            "salary" => "salary",
            "payroll" => "salary",
            "refund" => "refund",
            "gifts" => "gift",
            "donations" => "donation",
            "charity" => "charity",
            "tax" => "tax",
            "government" => "government",
            _ => plaidTerm.Length > 3 ? plaidTerm : null
        };
    }
}
