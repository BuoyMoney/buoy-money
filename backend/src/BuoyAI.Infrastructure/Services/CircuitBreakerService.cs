using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Simple circuit breaker implementation for external service calls.
/// Provides fail-fast behavior when external services are experiencing issues.
/// </summary>
public class CircuitBreakerService : ICircuitBreakerService
{
    private readonly ILogger<CircuitBreakerService> _logger;
    private readonly ConcurrentDictionary<string, CircuitState> _circuits = new();
    
    // Configuration
    private const int FailureThreshold = 5;       // Number of failures before opening circuit
    private const int SuccessThreshold = 3;       // Number of successes in half-open before closing
    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(30);  // How long circuit stays open

    public CircuitBreakerService(ILogger<CircuitBreakerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Executes an async operation with circuit breaker protection.
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="circuitName">Name of the circuit (e.g., "Plaid", "Stripe")</param>
    /// <param name="operation">The async operation to execute</param>
    /// <param name="fallback">Optional fallback value if circuit is open</param>
    /// <returns>Result of the operation or fallback</returns>
    public async Task<T> ExecuteAsync<T>(
        string circuitName, 
        Func<Task<T>> operation, 
        Func<Task<T>>? fallback = null)
    {
        var circuit = _circuits.GetOrAdd(circuitName, _ => new CircuitState());
        
        // Check if circuit is open
        if (circuit.State == CircuitStates.Open)
        {
            if (DateTime.UtcNow - circuit.LastStateChange < OpenDuration)
            {
                _logger.LogWarning("Circuit {CircuitName} is OPEN. Failing fast.", circuitName);
                
                if (fallback != null)
                {
                    return await fallback();
                }
                
                throw new CircuitBreakerOpenException(circuitName);
            }
            
            // Transition to half-open
            circuit.State = CircuitStates.HalfOpen;
            circuit.SuccessCount = 0;
            _logger.LogInformation("Circuit {CircuitName} transitioning to HALF-OPEN", circuitName);
        }

        try
        {
            var result = await operation();
            
            RecordSuccess(circuit, circuitName);
            
            return result;
        }
        catch (Exception ex)
        {
            RecordFailure(circuit, circuitName, ex);
            throw;
        }
    }

    /// <summary>
    /// Executes an async operation with circuit breaker protection (void return).
    /// </summary>
    public async Task ExecuteAsync(
        string circuitName, 
        Func<Task> operation, 
        Func<Task>? fallback = null)
    {
        await ExecuteAsync(
            circuitName,
            async () => { await operation(); return true; },
            fallback != null ? async () => { await fallback(); return true; } : null
        );
    }

    /// <summary>
    /// Gets the current state of a circuit.
    /// </summary>
    public CircuitStates GetCircuitState(string circuitName)
    {
        return _circuits.TryGetValue(circuitName, out var circuit) 
            ? circuit.State 
            : CircuitStates.Closed;
    }

    /// <summary>
    /// Gets statistics for a circuit.
    /// </summary>
    public CircuitStatistics GetCircuitStatistics(string circuitName)
    {
        if (!_circuits.TryGetValue(circuitName, out var circuit))
        {
            return new CircuitStatistics(circuitName, CircuitStates.Closed, 0, 0, null);
        }

        return new CircuitStatistics(
            circuitName,
            circuit.State,
            circuit.FailureCount,
            circuit.SuccessCount,
            circuit.LastStateChange
        );
    }

    /// <summary>
    /// Gets statistics for all circuits.
    /// </summary>
    public IEnumerable<CircuitStatistics> GetAllCircuitStatistics()
    {
        return _circuits.Select(kvp => new CircuitStatistics(
            kvp.Key,
            kvp.Value.State,
            kvp.Value.FailureCount,
            kvp.Value.SuccessCount,
            kvp.Value.LastStateChange
        ));
    }

    /// <summary>
    /// Resets a circuit to closed state (for admin/recovery purposes).
    /// </summary>
    public void ResetCircuit(string circuitName)
    {
        if (_circuits.TryGetValue(circuitName, out var circuit))
        {
            circuit.State = CircuitStates.Closed;
            circuit.FailureCount = 0;
            circuit.SuccessCount = 0;
            circuit.LastStateChange = DateTime.UtcNow;
            _logger.LogInformation("Circuit {CircuitName} manually reset to CLOSED", circuitName);
        }
    }

    private void RecordSuccess(CircuitState circuit, string circuitName)
    {
        circuit.SuccessCount++;
        
        if (circuit.State == CircuitStates.HalfOpen && circuit.SuccessCount >= SuccessThreshold)
        {
            circuit.State = CircuitStates.Closed;
            circuit.FailureCount = 0;
            circuit.LastStateChange = DateTime.UtcNow;
            _logger.LogInformation("Circuit {CircuitName} closed after {SuccessCount} successful calls", 
                circuitName, circuit.SuccessCount);
        }
        else if (circuit.State == CircuitStates.Closed)
        {
            // Reset failure count on success in closed state
            circuit.FailureCount = 0;
        }
    }

    private void RecordFailure(CircuitState circuit, string circuitName, Exception ex)
    {
        circuit.FailureCount++;
        circuit.LastException = ex;
        
        if (circuit.State == CircuitStates.HalfOpen)
        {
            // Any failure in half-open returns to open
            circuit.State = CircuitStates.Open;
            circuit.LastStateChange = DateTime.UtcNow;
            _logger.LogWarning("Circuit {CircuitName} reopened after failure in half-open state: {Error}",
                circuitName, ex.Message);
        }
        else if (circuit.State == CircuitStates.Closed && circuit.FailureCount >= FailureThreshold)
        {
            circuit.State = CircuitStates.Open;
            circuit.LastStateChange = DateTime.UtcNow;
            _logger.LogWarning("Circuit {CircuitName} opened after {FailureCount} consecutive failures. Last error: {Error}",
                circuitName, circuit.FailureCount, ex.Message);
        }
    }

    private class CircuitState
    {
        public CircuitStates State { get; set; } = CircuitStates.Closed;
        public int FailureCount { get; set; }
        public int SuccessCount { get; set; }
        public DateTime LastStateChange { get; set; } = DateTime.UtcNow;
        public Exception? LastException { get; set; }
    }
}

/// <summary>
/// Circuit breaker states
/// </summary>
public enum CircuitStates
{
    /// <summary>Circuit is operating normally</summary>
    Closed,
    
    /// <summary>Circuit is open - failing fast</summary>
    Open,
    
    /// <summary>Circuit is testing if service recovered</summary>
    HalfOpen
}

/// <summary>
/// Statistics for a circuit
/// </summary>
public record CircuitStatistics(
    string Name,
    CircuitStates State,
    int FailureCount,
    int SuccessCount,
    DateTime? LastStateChange
);

/// <summary>
/// Exception thrown when circuit breaker is open
/// </summary>
public class CircuitBreakerOpenException : Exception
{
    public string CircuitName { get; }
    
    public CircuitBreakerOpenException(string circuitName) 
        : base($"Circuit breaker '{circuitName}' is open. Service temporarily unavailable.")
    {
        CircuitName = circuitName;
    }
}

/// <summary>
/// Interface for circuit breaker service
/// </summary>
public interface ICircuitBreakerService
{
    Task<T> ExecuteAsync<T>(string circuitName, Func<Task<T>> operation, Func<Task<T>>? fallback = null);
    Task ExecuteAsync(string circuitName, Func<Task> operation, Func<Task>? fallback = null);
    CircuitStates GetCircuitState(string circuitName);
    CircuitStatistics GetCircuitStatistics(string circuitName);
    IEnumerable<CircuitStatistics> GetAllCircuitStatistics();
    void ResetCircuit(string circuitName);
}
