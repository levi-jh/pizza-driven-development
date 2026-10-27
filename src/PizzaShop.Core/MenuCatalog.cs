using System.Text.Json;

namespace PizzaShop.Core;

/// <summary>
/// Central registry for the restaurant's menu items, tax rate, and promo rules.
/// Uses case-insensitive string dictionaries rather than enums for sizes and topping categories
/// so adding a new size or topping tier is just a dictionary entry or an entry in menu.json.
/// </summary>
public class MenuCatalog
{
    public const decimal DefaultTaxRate = 0.065m;
    public const string DefaultMenuFileName = "menu.json";

    private readonly Dictionary<string, SizeDef> _sizes = new Dictionary<string, SizeDef>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToppingCategoryDef> _categories = new Dictionary<string, ToppingCategoryDef>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToppingDef> _toppings = new Dictionary<string, ToppingDef>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPromoRule> _promos = new Dictionary<string, IPromoRule>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sales tax rate applied to the post-discount subtotal.
    /// Defaults to 6.5% (0.065m), and can be overridden in menu.json.
    /// </summary>
    public decimal TaxRate { get; set; } = DefaultTaxRate;

    /// <summary>
    /// Registered pizza sizes keyed by size code.
    /// </summary>
    public IReadOnlyDictionary<string, SizeDef> Sizes => _sizes;

    /// <summary>
    /// Registered topping categories keyed by category code.
    /// </summary>
    public IReadOnlyDictionary<string, ToppingCategoryDef> Categories => _categories;

    /// <summary>
    /// Registered toppings keyed by topping code.
    /// </summary>
    public IReadOnlyDictionary<string, ToppingDef> Toppings => _toppings;

    /// <summary>
    /// Registered promo rules keyed by promo code.
    /// </summary>
    public IReadOnlyDictionary<string, IPromoRule> Promos => _promos;

    /// <summary>
    /// Registers or overwrites a pizza size in the catalog.
    /// </summary>
    /// <returns>
    /// The current MenuCatalog instance for fluent chaining.
    /// </returns>
    public MenuCatalog AddSize(string code, string name, decimal basePrice)
    {
        _sizes[code.Trim()] = new SizeDef(code.Trim().ToLowerInvariant(), name.Trim(), basePrice);
        return this;
    }

    /// <summary>
    /// Registers or overwrites a topping category (e.g. Basic @ 8%, Premium @ 11%).
    /// </summary>
    /// <returns>
    /// The current MenuCatalog instance for fluent chaining.
    /// </returns>
    public MenuCatalog AddToppingCategory(string code, string name, decimal basePricePct)
    {
        _categories[code.Trim()] = new ToppingCategoryDef(code.Trim().ToLowerInvariant(), name.Trim(), basePricePct);
        return this;
    }

    /// <summary>
    /// Registers a topping under an existing topping category.
    /// </summary>
    /// <returns>
    /// The current MenuCatalog instance for fluent chaining.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the specified category code has not been registered in the catalog.
    /// </exception>
    public MenuCatalog AddTopping(string code, string name, string categoryCode)
    {
        if (!_categories.ContainsKey(categoryCode.Trim()))
        {
            throw new InvalidOperationException($"Unknown topping category '{categoryCode}'. Register the category first.");
        }

        var normalizedCode = code.Trim().ToLowerInvariant();
        _toppings[normalizedCode] = new ToppingDef(normalizedCode, name.Trim(), categoryCode.Trim().ToLowerInvariant());
        return this;
    }

    /// <summary>
    /// Registers a promo rule by its code.
    /// </summary>
    /// <returns>
    /// The current MenuCatalog instance for fluent chaining.
    /// </returns>
    public MenuCatalog AddPromoRule(IPromoRule rule)
    {
        _promos[rule.Code.Trim()] = rule;
        return this;
    }

    /// <summary>
    /// Case-insensitive lookup for a pizza size.
    /// </summary>
    /// <returns>
    /// True if the size was found; false if the code is null, blank, or unregistered.
    /// </returns>
    public bool TryGetSize(string? code, out SizeDef size)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            size = null!;
            return false;
        }

        return _sizes.TryGetValue(code.Trim(), out size!);
    }

    /// <summary>
    /// Resolves both a topping definition and its parent pricing category in one step.
    /// </summary>
    /// <returns>
    /// True if both the topping and its category were found; false if the code is null, blank, or unregistered.
    /// </returns>
    public bool TryGetTopping(string? code, out ToppingDef topping, out ToppingCategoryDef category)
    {
        if (!string.IsNullOrWhiteSpace(code) && _toppings.TryGetValue(code.Trim(), out topping!) && _categories.TryGetValue(topping.CategoryCode, out category!))
        {
            return true;
        }

        topping = null!;
        category = null!;
        return false;
    }

    /// <summary>
    /// Case-insensitive lookup for a promo rule.
    /// </summary>
    /// <returns>
    /// True if the promo rule was found; false if the code is null, blank, or unregistered.
    /// </returns>
    public bool TryGetPromo(string? code, out IPromoRule rule)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            rule = null!;
            return false;
        }

        return _promos.TryGetValue(code.Trim(), out rule!);
    }

    /// <summary>
    /// Asynchronously loads the menu catalog from menu.json if present, falling back to CreateDefault
    /// if the file isn't found or fails to parse.
    /// </summary>
    /// <returns>
    /// Hydrated MenuCatalog instance.
    /// </returns>
    public static async Task<MenuCatalog> LoadFromFileOrDefaultAsync(string? filePath = null, CancellationToken cancellationToken = default)
    {
        var candidatePaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            candidatePaths.Add(filePath);
        }
        else
        {
            candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, DefaultMenuFileName));
            candidatePaths.Add(DefaultMenuFileName);
        }

        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                return LoadFromJson(json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to load menu seed from '{path}' ({ex.Message}). Falling back to default in-memory catalog.");
                return CreateDefault();
            }
        }

        return CreateDefault();
    }

    /// <summary>
    /// Hydrates a MenuCatalog from a JSON seed string.
    /// Allows adding new sizes, topping tiers, toppings, and promo rules through data.
    /// </summary>
    /// <returns>
    /// Hydrated MenuCatalog instance.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the JSON payload cannot be deserialized or contains an unknown promo rule type or topping category.
    /// </exception>
    public static MenuCatalog LoadFromJson(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var seed = JsonSerializer.Deserialize<MenuSeedConfig>(json, options) ?? throw new InvalidOperationException("Failed to deserialize menu JSON.");

        var catalog = new MenuCatalog();
        if (seed.TaxRate > 0m)
        {
            catalog.TaxRate = seed.TaxRate;
        }

        if (seed.Sizes != null)
        {
            foreach (var s in seed.Sizes)
            {
                catalog.AddSize(s.Code, s.Name, s.BasePrice);
            }
        }

        if (seed.Categories != null)
        {
            foreach (var c in seed.Categories)
            {
                catalog.AddToppingCategory(c.Code, c.Name, c.BasePricePct);
            }
        }

        if (seed.Toppings != null)
        {
            foreach (var t in seed.Toppings)
            {
                var toppingName = t.Name;
                if (string.IsNullOrWhiteSpace(toppingName))
                {
                    toppingName = ToTitleCase(t.Code);
                }

                catalog.AddTopping(t.Code, toppingName, t.CategoryCode);
            }
        }

        if (seed.Promos != null)
        {
            foreach (var p in seed.Promos)
            {
                if (string.Equals(p.Type, "percentage", StringComparison.OrdinalIgnoreCase))
                {
                    catalog.AddPromoRule(new PercentageDiscountPromo(p.Code, p.Description, p.Rate, p.ConflictsWith));
                    continue;
                }

                if (string.Equals(p.Type, "thresholdFlat", StringComparison.OrdinalIgnoreCase))
                {
                    catalog.AddPromoRule(new ThresholdFlatDiscountPromo(p.Code, p.Description, p.Amount, p.MinSubtotalExclusive, p.ConflictsWith));
                    continue;
                }

                throw new InvalidOperationException($"Unknown promo rule type '{p.Type}' for code '{p.Code}'.");
            }
        }

        return catalog;
    }

    /// <summary>
    /// Fallback in-memory seed matching menu.json so unit tests and tools always have a working catalog even without the JSON file on disk.
    /// </summary>
    /// <returns>
    /// Default MenuCatalog populated with standard sizes, topping categories, toppings, and promos.
    /// </returns>
    public static MenuCatalog CreateDefault()
    {
        var catalog = new MenuCatalog { TaxRate = DefaultTaxRate };

        catalog.AddSize("small", "Small", 5.00m);
        catalog.AddSize("medium", "Medium", 7.00m);
        catalog.AddSize("large", "Large", 12.00m);

        catalog.AddToppingCategory("basic", "Basic", 0.08m);
        catalog.AddToppingCategory("premium", "Premium", 0.11m);

        // "extra cheese" is listed as a basic topping in the spec, so toppings are treated as binary selections rather than portion multipliers.
        string[] basicToppings = new[] { "pepperoni", "sausage", "extra cheese", "onions", "black olives", "green peppers" };
        foreach (var t in basicToppings)
        {
            catalog.AddTopping(t, ToTitleCase(t), "basic");
        }

        string[] premiumToppings = new[] { "bacon", "chicken", "garlic", "artichoke hearts", "sun-dried tomatoes", "pineapple" };
        foreach (var t in premiumToppings)
        {
            catalog.AddTopping(t, ToTitleCase(t), "premium");
        }

        // Promo rules reference each other in conflictsWith so neither order of application is allowed.
        catalog.AddPromoRule(new PercentageDiscountPromo("ILOVEPIZZA", "10% off order total before tax", 0.10m, new[] { "SAVE5" }));
        catalog.AddPromoRule(new ThresholdFlatDiscountPromo("SAVE5", "$5 off when order total before tax is over $20", 5.00m, 20.00m, new[] { "ILOVEPIZZA" }));

        return catalog;
    }

    /// <summary>
    /// Simple helper so we don't have to repeat display strings when seeding toppings.
    /// </summary>
    private static string ToTitleCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1));
        return string.Join(' ', words);
    }
}
