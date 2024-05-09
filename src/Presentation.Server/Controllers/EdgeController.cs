using Application.Server.Edge.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.Edge.Models;
using Microsoft.AspNetCore.Mvc;
using RestfulHelpers.Common;

namespace Presentation.Server.Controllers;

/// <summary>
/// Controller for managing Edge entities.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class EdgeController(EdgeService edgeService) : ControllerBase
{
    private readonly EdgeService _edgeService = edgeService;

    /// <summary>
    /// Retrieves all Edge entities.
    /// </summary>
    /// <returns>An HTTP result containing an array of EdgeEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet]
    public Task<HttpResult<EdgeEntity[]>> GetAll()
    {
        return _edgeService.GetAll();
    }

    /// <summary>
    /// Retrieves a specific Edge entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the Edge entity to retrieve.</param>
    /// <returns>An HTTP result containing the EdgeEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the provided ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("{id}")]
    public Task<HttpResult<EdgeConnectionEntity>> Get(string id)
    {
        return _edgeService.GetHandshake(id);
    }

    /// <summary>
    /// Creates a new Edge entity.
    /// </summary>
    /// <param name="edge">The data for the new Edge entity.</param>
    /// <returns>An HTTP result containing the created EdgeEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided data is invalid.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPost]
    public Task<HttpResult<EdgeEntity>> Create([FromBody] EdgeAddDto edge)
    {
        return _edgeService.Create(edge);
    }

    /// <summary>
    /// Updates an existing Edge entity.
    /// </summary>
    /// <param name="id">The ID of the Edge entity to update.</param>
    /// <param name="edge">The updated data for the Edge entity.</param>
    /// <returns>An HTTP result containing the updated EdgeEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID or data is invalid.</response>
    /// <response code="404">Returns when the Edge entity with the given ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPut("{id}")]
    public Task<HttpResult<EdgeEntity>> Edit(string id, [FromBody] EdgeEditDto edge)
    {
        return _edgeService.Edit(id, edge);
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
    public Task<HttpResult> Delete(string id)
    {
        return _edgeService.Delete(id);
    }
}
