using Application.Server.Edge.Services;
using Application.Server.PortRoute.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.PortRoute.Dtos;
using Domain.PortRoute.Entities;
using Microsoft.AspNetCore.Mvc;
using RestfulHelpers.Common;

namespace Presentation.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
public class RouteController(PortRouteService portRouteService) : ControllerBase
{
    private readonly PortRouteService _portRouteService = portRouteService;

    [HttpGet]
    public Task<HttpResult<PortRouteEntity[]>> GetAll()
    {
        throw new NotImplementedException();
    }

    public Task<HttpResult<PortRouteEntity>> Get(string id)
    {
        throw new NotImplementedException();
    }

    public Task<HttpResult<PortRouteEntity>> Create([FromBody] PortRouteAddDto edge)
    {
        throw new NotImplementedException();
    }

    public Task<HttpResult<PortRouteEntity>> Edit(string id, [FromBody] PortRouteEditDto edge)
    {
        throw new NotImplementedException();
    }

    public Task<HttpResult> Delete(string id)
    {
        throw new NotImplementedException();
    }
}
