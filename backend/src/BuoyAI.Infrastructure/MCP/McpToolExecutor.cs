using System.Reflection;
using System.Text.Json;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.MCP.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

namespace BuoyAI.Infrastructure.MCP;

/// <summary>
/// Service to execute MCP tools for a specific user context.
/// This bridges the gap between MCP tools and the AI service.
/// </summary>
public interface IMcpToolExecutor
{
    /// <summary>
    /// Get all available tool definitions in MCP tool format.
    /// </summary>
    List<McpToolDefinition> GetAvailableTools();
    
    /// <summary>
    /// Execute a tool on the model's behalf (chat, insight drill-down, the public MCP server).
    /// A write never happens here: any <c>confirmed</c> flag is forced to false, so a write tool
    /// returns its preview, and the user confirms it through <see cref="ExecuteConfirmedToolAsync"/>.
    /// </summary>
    Task<string> ExecuteToolAsync(int userId, string toolName, Dictionary<string, object>? arguments = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a write the user confirmed: a Confirm tap on a preview (POST /api/ai/tools/execute),
    /// or a tap on an insight action, which authorizes that action's reversible writes. Sets
    /// <c>confirmed</c> to true. Never call this with a model's arguments the user hasn't approved.
    /// </summary>
    Task<string> ExecuteConfirmedToolAsync(int userId, string toolName, Dictionary<string, object>? arguments = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get a formatted string listing all tools for the AI prompt.
    /// </summary>
    string GetToolsPrompt();
    
    /// <summary>
    /// Get Gemini-native function declarations for native function calling.
    /// This is the preferred approach over text-based tool calling.
    /// </summary>
    List<FunctionDeclaration> GetGeminiFunctionDeclarations();

    /// <summary>
    /// Get Gemini function declarations for a filtered set of tools. Used to expose only
    /// read-only tools (agentic insight drill-down) or a curated write subset (the
    /// "act on suggestion" handoff) rather than the full tool surface.
    /// </summary>
    /// <param name="readOnlyOnly">When true, only tools marked ReadOnly are returned.</param>
    /// <param name="allowedToolNames">
    /// When non-null, only tools whose name is in this set are returned (applied on top of
    /// <paramref name="readOnlyOnly"/>).
    /// </param>
    List<FunctionDeclaration> GetGeminiFunctionDeclarations(bool readOnlyOnly, ISet<string>? allowedToolNames = null);

    /// <summary>Distinct capability domains across all discovered tools (drives intent-based filtering, #740).</summary>
    IReadOnlyList<string> GetToolDomains();

    /// <summary>Tool names whose declaring-class domain is in <paramref name="domains"/> (case-insensitive).</summary>
    ISet<string> GetToolNamesForDomains(IEnumerable<string> domains);
}

/// <summary>
/// Tool definition for MCP tools.
/// </summary>
public class McpToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    /// <summary>Coarse capability domain (financial, budget, savings, subscription, income, user,
    /// transaction), derived from the declaring tool class. Drives intent-based tool filtering (#740).</summary>
    public string Domain { get; set; } = "general";
    public List<McpToolParameter> Parameters { get; set; } = new();
}

/// <summary>
/// Tool parameter definition.
/// </summary>
public class McpToolParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
    public object? DefaultValue { get; set; }
}

/// <summary>
/// Implementation of MCP tool executor that discovers and runs MCP tools.
/// </summary>
public class McpToolExecutor : IMcpToolExecutor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<McpToolExecutor> _logger;
    private readonly IReadOnlyDictionary<string, (Type ToolType, MethodInfo Method, bool ReadOnly)> _toolRegistry;
    private readonly IReadOnlyList<McpToolDefinition> _toolDefinitions;

    public McpToolExecutor(IServiceProvider serviceProvider, ILogger<McpToolExecutor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        (_toolRegistry, _toolDefinitions) = DiscoverTools();
    }

    /// <summary>
    /// Every tool class the executor scans. A tool missing from this list is invisible to the
    /// model no matter how it is registered in DI, so it is exposed for tests to assert against.
    /// </summary>
    public static readonly IReadOnlyList<Type> KnownToolTypes = new[]
    {
        typeof(FinancialReadTools),
        typeof(BudgetReadTools),
        typeof(CashFlowReadTools),   // actual income/spend — the only correct source for totals
        typeof(SavingsReadTools),
        typeof(SubscriptionReadTools),
        typeof(IncomeReadTools),
        typeof(UserPreferencesTools),
        typeof(UserContextReadTools),      // User profile/quiz data
        typeof(GamificationReadTools),     // Level/streaks/badges + oops-repeat behavioral signals
        typeof(HelpReadTools),             // Published help/blog article search (global)
        typeof(MemoryReadTools),           // Recall durable facts (cross-agent memory, read half)
        typeof(MemoryWriteTools),          // Remember a fact (cross-agent memory, write half — non-financial)
        typeof(BudgetWriteTools),
        typeof(SavingsWriteTools),
        typeof(TransactionWriteTools)
    };

    /// <summary>Write tools that modify PERSONAL notes, not shared financial data, and so are
    /// exempt from the family "can modify financial data" gate. Kept tiny and explicit on purpose.</summary>
    public static readonly IReadOnlySet<string> NonFinancialWriteTools =
        new HashSet<string>(StringComparer.Ordinal) { "remember" };

    private (IReadOnlyDictionary<string, (Type ToolType, MethodInfo Method, bool ReadOnly)>, IReadOnlyList<McpToolDefinition>) DiscoverTools()
    {
        var registry = new Dictionary<string, (Type ToolType, MethodInfo Method, bool ReadOnly)>();
        var definitions = new List<McpToolDefinition>();
        
        foreach (var toolType in KnownToolTypes)
        {
            if (!toolType.GetCustomAttributes<McpServerToolTypeAttribute>().Any())
            {
                _logger.LogWarning("Type {TypeName} missing McpServerToolType attribute", toolType.Name);
                continue;
            }

            foreach (var method in toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var toolAttribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttribute == null) continue;

                var toolName = toolAttribute.Name ?? method.Name;
                var descriptionAttr = method.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                    .Cast<System.ComponentModel.DescriptionAttribute>()
                    .FirstOrDefault();
                var description = descriptionAttr?.Description ?? string.Empty;

                registry[toolName] = (toolType, method, toolAttribute.ReadOnly);

                var parameters = new List<McpToolParameter>();
                foreach (var param in method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
                {
                    var paramDescAttr = param.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                        .Cast<System.ComponentModel.DescriptionAttribute>()
                        .FirstOrDefault();

                    parameters.Add(new McpToolParameter
                    {
                        Name = param.Name ?? string.Empty,
                        Type = GetJsonType(param.ParameterType),
                        Description = paramDescAttr?.Description ?? string.Empty,
                        Required = !param.HasDefaultValue,
                        DefaultValue = param.HasDefaultValue ? param.DefaultValue : null
                    });
                }

                definitions.Add(new McpToolDefinition
                {
                    Name = toolName,
                    Description = description,
                    ReadOnly = toolAttribute.ReadOnly,
                    Domain = DeriveDomain(toolType),
                    Parameters = parameters
                });

                _logger.LogDebug("Registered MCP tool: {ToolName} (ReadOnly={ReadOnly})", toolName, toolAttribute.ReadOnly);
            }
        }

        _logger.LogInformation("Discovered {Count} MCP tools", definitions.Count);
        return (registry, definitions);
    }

    // Declaring tool class -> coarse capability domain, for intent-based tool filtering (#740).
    private static readonly IReadOnlyDictionary<string, string> ToolClassDomains =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FinancialReadTools"] = "financial",
            ["BudgetReadTools"] = "budget",
            // "financial" deliberately, NOT "budget": ChatToolRouter.AlwaysDomains includes
            // "financial", so get_cash_flow stays reachable on every filtered turn. Re-tagging it
            // to "budget" would hide actuals from a non-budget question and leave the model
            // inferring totals from allocations — the exact failure this tool exists to prevent.
            ["CashFlowReadTools"] = "financial",
            ["BudgetWriteTools"] = "budget",
            ["SavingsReadTools"] = "savings",
            ["SavingsWriteTools"] = "savings",
            ["SubscriptionReadTools"] = "subscription",
            ["SubscriptionWriteTools"] = "subscription",
            ["IncomeReadTools"] = "income",
            ["UserPreferencesTools"] = "user",
            ["UserContextReadTools"] = "user",
            ["GamificationReadTools"] = "gamification",
            ["HelpReadTools"] = "help",
            ["TransactionWriteTools"] = "transaction",
        };

    private static string DeriveDomain(Type toolType) => DomainForToolClass(toolType.Name);

    /// <summary>Capability domain for a tool class name. Exposed so tests can assert a tool sits
    /// in a domain the router always offers (see ChatToolRouter.AlwaysDomains).</summary>
    public static string DomainForToolClass(string toolClassName) =>
        ToolClassDomains.TryGetValue(toolClassName, out var d) ? d : "general";

    public IReadOnlyList<string> GetToolDomains() =>
        _toolDefinitions.Select(t => t.Domain).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();

    public ISet<string> GetToolNamesForDomains(IEnumerable<string> domains)
    {
        var wanted = new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);
        return _toolDefinitions
            .Where(t => wanted.Contains(t.Domain))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string GetJsonType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        
        if (underlyingType == typeof(int) || underlyingType == typeof(long) || 
            underlyingType == typeof(short) || underlyingType == typeof(byte))
            return "integer";
        if (underlyingType == typeof(decimal) || underlyingType == typeof(double) || 
            underlyingType == typeof(float))
            return "number";
        if (underlyingType == typeof(bool))
            return "boolean";
        if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
            return "string";
        
        return "string";
    }

    public List<McpToolDefinition> GetAvailableTools() => _toolDefinitions.ToList();

    public Task<string> ExecuteToolAsync(
        int userId,
        string toolName,
        Dictionary<string, object>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        // The model can't confirm its own write: whatever it sent, a write tool returns its preview.
        if (arguments != null)
        {
            foreach (var key in arguments.Keys.Where(IsConfirmedKey).ToList())
            {
                arguments[key] = false;
            }
        }
        return ExecuteCoreAsync(userId, toolName, arguments, cancellationToken);
    }

    public Task<string> ExecuteConfirmedToolAsync(
        int userId,
        string toolName,
        Dictionary<string, object>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var args = arguments ?? new Dictionary<string, object>();
        foreach (var key in args.Keys.Where(IsConfirmedKey).ToList())
        {
            args.Remove(key);
        }
        args["confirmed"] = true;
        return ExecuteCoreAsync(userId, toolName, args, cancellationToken);
    }

    private static bool IsConfirmedKey(string key) =>
        string.Equals(key, "confirmed", StringComparison.OrdinalIgnoreCase);

    private async Task<string> ExecuteCoreAsync(
        int userId,
        string toolName,
        Dictionary<string, object>? arguments,
        CancellationToken cancellationToken)
    {
        if (!_toolRegistry.TryGetValue(toolName, out var toolInfo))
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolName);
            return JsonSerializer.Serialize(new { Error = $"Unknown tool: {toolName}" });
        }

        try
        {
            var (toolType, method, isReadOnly) = toolInfo;
            
            // Create a scope to resolve scoped services (like IBudgetService, ISavingsService, etc.)
            using var scope = _serviceProvider.CreateScope();
            var scopedProvider = scope.ServiceProvider;
            
            // Authorization check: For write tools, verify user has permission to modify data
            // This enforces family role-based access control (e.g., ReadOnly members cannot modify).
            // Memory tools are exempt: they write personal notes, not shared FINANCIAL data, so a
            // view-only family member is still allowed to store their own memory.
            if (!isReadOnly && !NonFinancialWriteTools.Contains(toolName))
            {
                var familyService = scopedProvider.GetRequiredService<IFamilyService>();
                var canModify = await familyService.CanUserModifyFinancialDataAsync(userId);
                
                if (!canModify)
                {
                    _logger.LogWarning(
                        "User {UserId} denied write access to tool {ToolName} - ReadOnly family member", 
                        userId, toolName);
                    
                    return JsonSerializer.Serialize(new
                    {
                        Error = "Permission denied",
                        Message = "You have view-only access to family finances. Contact the family owner to modify your permissions.",
                        Code = "READONLY_ACCESS"
                    });
                }
            }
            
            // Create tool instance via DI using the scoped provider
            var toolInstance = ActivatorUtilities.CreateInstance(scopedProvider, toolType);

            // Build parameter list
            var methodParams = method.GetParameters();
            var invokeArgs = new object?[methodParams.Length];

            for (int i = 0; i < methodParams.Length; i++)
            {
                var param = methodParams[i];
                
                // Handle CancellationToken
                if (param.ParameterType == typeof(CancellationToken))
                {
                    invokeArgs[i] = cancellationToken;
                    continue;
                }

                // UserId is always injected
                if (param.Name?.Equals("userId", StringComparison.OrdinalIgnoreCase) == true)
                {
                    invokeArgs[i] = userId;
                    continue;
                }

                // Try to get from arguments
                if (arguments != null && param.Name != null && arguments.TryGetValue(param.Name, out var argValue))
                {
                    invokeArgs[i] = ConvertArgument(argValue, param.ParameterType);
                }
                else if (param.HasDefaultValue)
                {
                    invokeArgs[i] = param.DefaultValue;
                }
                else
                {
                    invokeArgs[i] = GetDefaultValue(param.ParameterType);
                }
            }

            _logger.LogInformation("Executing MCP tool: {ToolName} for user {UserId}", toolName, userId);

            // Invoke the method
            var result = method.Invoke(toolInstance, invokeArgs);

            // Handle async methods
            if (result is Task<string> stringTask)
            {
                return await stringTask;
            }
            if (result is Task task)
            {
                await task;
                // Use reflection to get result from Task<T>
                var resultProperty = task.GetType().GetProperty("Result");
                if (resultProperty != null)
                {
                    var taskResult = resultProperty.GetValue(task);
                    return taskResult?.ToString() ?? "{}";
                }
                return "{}";
            }
            
            return result?.ToString() ?? "{}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing tool {ToolName}", toolName);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    private static object? ConvertArgument(object value, Type targetType)
    {
        if (value == null) return null;

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // Handle JsonElement
        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.String => ConvertStringValue(jsonElement.GetString()!, underlyingType),
                JsonValueKind.Number when underlyingType == typeof(int) => jsonElement.GetInt32(),
                JsonValueKind.Number when underlyingType == typeof(long) => jsonElement.GetInt64(),
                JsonValueKind.Number when underlyingType == typeof(decimal) => jsonElement.GetDecimal(),
                JsonValueKind.Number when underlyingType == typeof(double) => jsonElement.GetDouble(),
                JsonValueKind.Number => jsonElement.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => jsonElement.GetRawText()
            };
        }

        // Direct type match
        if (targetType.IsInstanceOfType(value))
            return value;

        // String conversions
        if (value is string strValue)
            return ConvertStringValue(strValue, underlyingType);

        // Numeric conversions
        return Convert.ChangeType(value, underlyingType);
    }

    private static object? ConvertStringValue(string value, Type targetType)
    {
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(int)) return int.Parse(value);
        if (targetType == typeof(long)) return long.Parse(value);
        if (targetType == typeof(decimal)) return decimal.Parse(value);
        if (targetType == typeof(double)) return double.Parse(value);
        if (targetType == typeof(bool)) return bool.Parse(value);
        if (targetType == typeof(DateTime)) return DateTime.Parse(value);
        if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
        
        return value;
    }

    private static object? GetDefaultValue(Type type)
    {
        if (type.IsValueType)
            return Activator.CreateInstance(type);
        return null;
    }

    public string GetToolsPrompt()
    {
        var prompt = new System.Text.StringBuilder();
        prompt.AppendLine("=== AVAILABLE TOOLS ===");
        prompt.AppendLine("You have access to the following tools to help the user manage their finances:");
        prompt.AppendLine();

        // Group by read/write
        var readTools = _toolDefinitions.Where(t => t.ReadOnly).ToList();
        var writeTools = _toolDefinitions.Where(t => !t.ReadOnly).ToList();

        prompt.AppendLine("--- READ TOOLS (no confirmation needed) ---");
        foreach (var tool in readTools)
        {
            prompt.AppendLine($"• {tool.Name}: {tool.Description}");
            if (tool.Parameters.Any(p => p.Name != "userId"))
            {
                foreach (var param in tool.Parameters.Where(p => p.Name != "userId"))
                {
                    var reqStr = param.Required ? "(required)" : $"(optional, default: {param.DefaultValue})";
                    prompt.AppendLine($"    - {param.Name} ({param.Type}): {param.Description} {reqStr}");
                }
            }
        }
        prompt.AppendLine();

        prompt.AppendLine("--- WRITE TOOLS (REQUIRE user confirmation) ---");
        prompt.AppendLine("IMPORTANT: For write tools, first call with confirmed=false to preview the action.");
        prompt.AppendLine("The preview shows what will be created/modified. The user confirms by tapping Confirm on the preview card; you cannot confirm for them.");
        prompt.AppendLine();
        foreach (var tool in writeTools)
        {
            prompt.AppendLine($"• {tool.Name}: {tool.Description}");
            if (tool.Parameters.Any(p => p.Name != "userId"))
            {
                foreach (var param in tool.Parameters.Where(p => p.Name != "userId"))
                {
                    var reqStr = param.Required ? "(required)" : $"(optional, default: {param.DefaultValue})";
                    prompt.AppendLine($"    - {param.Name} ({param.Type}): {param.Description} {reqStr}");
                }
            }
        }
        prompt.AppendLine();

        prompt.AppendLine("=== TOOL CALLING FORMAT (REQUIRED) ===");
        prompt.AppendLine("IMPORTANT: When you need to fetch data or perform actions, you MUST use the tool_call format below.");
        prompt.AppendLine("Do NOT describe what you would do - actually call the tool using this exact markdown format:");
        prompt.AppendLine();
        prompt.AppendLine("```tool_call");
        prompt.AppendLine("{");
        prompt.AppendLine("  \"tool\": \"tool_name\",");
        prompt.AppendLine("  \"arguments\": {");
        prompt.AppendLine("    \"param1\": \"value1\",");
        prompt.AppendLine("    \"param2\": \"value2\"");
        prompt.AppendLine("  }");
        prompt.AppendLine("}");
        prompt.AppendLine("```");
        prompt.AppendLine();
        prompt.AppendLine("Example - to create a savings goal:");
        prompt.AppendLine("```tool_call");
        prompt.AppendLine("{");
        prompt.AppendLine("  \"tool\": \"create_savings_goal\",");
        prompt.AppendLine("  \"arguments\": {");
        prompt.AppendLine("    \"name\": \"Vacation Fund\",");
        prompt.AppendLine("    \"targetAmount\": 5000,");
        prompt.AppendLine("    \"confirmed\": false");
        prompt.AppendLine("  }");
        prompt.AppendLine("}");
        prompt.AppendLine("```");
        prompt.AppendLine();
        prompt.AppendLine("You can call multiple tools by including multiple tool_call blocks.");
        prompt.AppendLine("After tool results are returned, synthesize them into a helpful response for the user.");
        prompt.AppendLine("NEVER respond with Python-like function syntax. ALWAYS use the ```tool_call JSON format above.");
        prompt.AppendLine();

        return prompt.ToString();
    }
    
    /// <summary>
    /// Convert MCP tool definitions to Gemini-native FunctionDeclarations.
    /// This enables proper native function calling instead of text-based parsing.
    /// </summary>
    public List<FunctionDeclaration> GetGeminiFunctionDeclarations()
        => GetGeminiFunctionDeclarations(readOnlyOnly: false, allowedToolNames: null);

    public List<FunctionDeclaration> GetGeminiFunctionDeclarations(bool readOnlyOnly, ISet<string>? allowedToolNames = null)
    {
        var declarations = new List<FunctionDeclaration>();

        var selected = _toolDefinitions
            .Where(t => !readOnlyOnly || t.ReadOnly)
            .Where(t => allowedToolNames == null || allowedToolNames.Contains(t.Name));

        foreach (var tool in selected)
        {
            // Build properties as a Dictionary where each property has Type and Description
            var properties = new Dictionary<string, object>();
            var requiredParams = new List<string>();
            
            foreach (var param in tool.Parameters.Where(p => p.Name != "userId"))
            {
                // Use anonymous objects for properties like SDK tests do
                properties[param.Name] = new 
                {
                    Type = MapTypeToGeminiType(param.Type),
                    Description = param.Description ?? $"The {param.Name} parameter"
                };
                
                if (param.Required)
                {
                    requiredParams.Add(param.Name);
                }
            }
            
            var declaration = new FunctionDeclaration
            {
                Name = tool.Name,
                Description = BuildToolDescription(tool),
                Parameters = new Schema
                {
                    Type = ParameterType.Object,
                    Properties = properties,  // Now Dictionary<string, object> with anonymous types
                    Required = requiredParams.Count > 0 ? requiredParams : null
                }
            };
            
            declarations.Add(declaration);
            _logger.LogDebug("Created FunctionDeclaration for tool: {ToolName} with {ParamCount} parameters", 
                tool.Name, properties.Count);
        }
        
        _logger.LogInformation("Generated {Count} Gemini FunctionDeclarations from MCP tools", declarations.Count);
        return declarations;
    }
    
    private static ParameterType MapTypeToGeminiType(string jsonType)
    {
        return jsonType.ToLowerInvariant() switch
        {
            "integer" => ParameterType.Integer,
            "number" => ParameterType.Number,
            "boolean" => ParameterType.Boolean,
            "array" => ParameterType.Array,
            "object" => ParameterType.Object,
            _ => ParameterType.String
        };
    }
    
    private static string BuildToolDescription(McpToolDefinition tool)
    {
        var desc = tool.Description;
        
        // Add confirmation guidance for write tools
        if (!tool.ReadOnly && tool.Parameters.Any(p => p.Name == "confirmed"))
        {
            desc += " IMPORTANT: Call with confirmed=false to preview. The user confirms with the Confirm button; you cannot confirm for them.";
        }
        
        return desc;
    }
}
