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
}
