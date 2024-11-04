using Application.Edge.Interfaces;
using Application.Edge.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.AspNetCore.Mvc;
using RestfulHelpers.Common;

namespace Presentation.Controllers;

/// <summary>
/// Controller for managing Edge entities.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class EdgeController(IEdgeService edgeService) : ControllerBase
{
    private readonly IEdgeService _edgeApiService = edgeService;

    /// <summary>
    /// Retrieves all Edge entities.
    /// </summary>
    /// <returns>An HTTP result containing an array of EdgeTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet]
    public Task<HttpResult<GetEdgeInfoDto[]>> GetAll()
    {
        return _edgeApiService.GetAll();
    }

    /// <summary>
    /// Retrieves a specific Edge entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the Edge entity to retrieve.</param>
    /// <returns>An HTTP result containing the EdgeTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the provided ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("{id}")]
    public Task<HttpResult<GetEdgeInfoDto>> Get(string id)
    {
        return _edgeApiService.Get(id);
    }

    /// <summary>
    /// Retrieves a specific Edge entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the Edge entity to retrieve.</param>
    /// <returns>An HTTP result containing the EdgeTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the provided ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("{id}/token")]
    public Task<HttpResult<GetEdgeWithTokenDto>> GetToken(string id)
    {
        return _edgeApiService.GetToken(id);
    }

    /// <summary>
    /// Creates a new Edge entity.
    /// </summary>
    /// <param name="edge">The data for the new Edge entity.</param>
    /// <returns>An HTTP result containing the created EdgeTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided data is invalid.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPost]
    public Task<HttpResult<GetEdgeWithTokenDto>> Create([FromBody] AddEdgeDto edge)
    {
        return _edgeApiService.Create(edge);
    }

    /// <summary>
    /// Updates an existing Edge entity.
    /// </summary>
    /// <param name="id">The ID of the Edge entity to update.</param>
    /// <param name="edge">The updated data for the Edge entity.</param>
    /// <returns>An HTTP result containing the updated EdgeTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID or data is invalid.</response>
    /// <response code="404">Returns when the Edge entity with the given ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPut("{id}")]
    public Task<HttpResult<GetEdgeWithTokenDto>> Edit(string id, [FromBody] EditEdgeDto edge)
    {
        return _edgeApiService.Edit(id, edge);
    }

    /// <summary>
    /// Deletes a specific Edge entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the Edge entity to delete.</param>
    /// <returns>An HTTP result indicating the success of the operation.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the Edge entity with the given ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpDelete("{id}")]
    public Task<HttpResult<GetEdgeInfoDto>> Delete(string id)
    {
        return _edgeApiService.Delete(id);
    }
}
