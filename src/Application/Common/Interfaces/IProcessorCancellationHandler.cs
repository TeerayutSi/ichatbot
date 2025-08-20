using System.Threading;
using System.Threading.Tasks;

namespace ChatbotApi.Application.Common.Interfaces
{
    /// <summary>
    /// Defines the contract for cancelling operations in processors that maintain session data
    /// </summary>
    public interface IProcessorCancellationHandler
    {
        /// <summary>
        /// Cancels any ongoing operations for a specific user by removing session data
        /// </summary>
        /// <param name="userId">The user ID for which to cancel operations</param>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task CancelOperationsAsync(string userId, CancellationToken cancellationToken = default);
    }
}