using AiTestCrew.Core.Models;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Abstraction for user storage.
/// </summary>
public interface IUserRepository
{
    Task<User> CreateAsync(string name);
    Task<User?> GetByIdAsync(string id);
    Task<User?> GetByApiKeyAsync(string apiKey);
    Task<List<User>> ListAllAsync();
    Task DeleteAsync(string id);
    Task SetActiveAsync(string id, bool isActive);

    /// <summary>Updates the user"s role column. Valid values: "User" | "AuthSteward" | "Admin".</summary>
    Task SetRoleAsync(string id, string role);

    /// <summary>Returns the number of active Admin users (used for last-admin guard).</summary>
    Task<int> CountAdminsAsync();
}
