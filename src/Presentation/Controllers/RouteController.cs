using Application.PortRoute.Interfaces;
using Application.Server.Edge.Services;
using Application.Server.PortRoute.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.PortRoute.Dtos;
using Domain.PortRoute.Entities;
using Microsoft.AspNetCore.Mvc;
using RestfulHelpers.Common;

namespace Presentation.Server.Controllers;

/// <summary>
/// Controller for managing port routes.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class RouteController(PortRouteApiService portRouteApiService) : ControllerBase
{
    private readonly PortRouteApiService _portRouteApiService = portRouteApiService;

    /// <summary>
    /// Retrieves all port routes.
    /// </summary>
    /// <returns>HTTP result containing an array of PortRouteEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="404">Returns when the field edgeId is provided and is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet]
    public Task<HttpResult<PortRouteEntity[]>> GetAll(string? sourceEdgeId, string? destinationEdgeId)
    {
        return _portRouteApiService.GetAll(sourceEdgeId: sourceEdgeId, destinationEdgeId: destinationEdgeId);
    }

    /// <summary>
    /// Retrieves a specific port route by ID.
    /// </summary>
    /// <param name="id">ID of the port route to retrieve.</param>
    /// <returns>HTTP result containing the PortRouteEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the provided ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("{id}")]
    public Task<HttpResult<PortRouteEntity>> Get(string id)
    {
        return _portRouteApiService.Get(id);
    }

    /// <summary>
    /// Creates a new port route.
    /// </summary>
    /// <param name="portRouteAddDto">PortRouteAddDto containing data for the new port route.</param>
    /// <returns>HTTP result containing the created PortRouteEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided body is invalid.</response>
    /// <response code="404">Returns when the provided sourceEdgeId or destinationEdgeId is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPost]
    public Task<HttpResult<PortRouteEntity>> Create([FromBody] PortRouteAddDto portRouteAddDto)
    {
        return _portRouteApiService.Create(portRouteAddDto);
    }

    /// <summary>
    /// Edits an existing port route.
    /// </summary>
    /// <param name="id">ID of the port route to edit.</param>
    /// <param name="portRouteEditDto">PortRouteEditDto containing updated data for the port route.</param>
    /// <returns>HTTP result containing the edited PortRouteEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided body is invalid.</response>
    /// <response code="404">Returns when the provided sourceEdgeId or destinationEdgeId is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPut("{id}")]
    public Task<HttpResult<PortRouteEntity>> Edit(string id, [FromBody] PortRouteEditDto portRouteEditDto)
    {
        return _portRouteApiService.Edit(id, portRouteEditDto);
    }

    /// <summary>
    /// Deletes a port route.
    /// </summary>
    /// <param name="id">ID of the port route to delete.</param>
    /// <returns>HTTP result indicating the success of the operation.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the ID query is not provided.</response>
    /// <response code="404">Returns when the ID query is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpDelete("{id}")]
    public Task<HttpResult> Delete(string id)
    {
        return _portRouteApiService.Delete(id);
    }
}
